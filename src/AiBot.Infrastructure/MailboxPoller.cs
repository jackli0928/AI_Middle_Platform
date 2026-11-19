using AiBot.Application;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Pop3;
using MailKit.Search;
using MailKit.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace AiBot.Infrastructure;

public sealed class MailboxPoller(IServiceScopeFactory scopes, ISecretProtector secrets, ILogger<MailboxPoller> logger)
{
    private const int BatchSize = 40;

    public async Task PollAsync(SortingMailbox account, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(account.Host) || string.IsNullOrWhiteSpace(account.Username) ||
            account.Port is < 0 or > 65535 || account.Protocol.ToLowerInvariant() is not ("imap" or "pop3"))
            return;

        await using var gateScope = scopes.CreateAsyncScope();
        var db = gateScope.ServiceProvider.GetRequiredService<BotDbContext>();
        await db.Database.OpenConnectionAsync(ct);
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        var key = SortingMailboxIdentity.Key(account);
        await using var claim = Command(connection, "SELECT pg_try_advisory_lock(hashtextextended(@key, 26061118))", key);
        if (await claim.ExecuteScalarAsync(ct) is not true) return;
        try
        {
            await using var getPassword = Command(connection, "SELECT protected_password FROM ai_bot.mail_credentials WHERE mailbox = @key", key);
            if (await getPassword.ExecuteScalarAsync(ct) is not string encrypted) return;
            var password = secrets.Unprotect(encrypted);
            var source = string.IsNullOrWhiteSpace(account.Name) ? account.Username : account.Name;
            if (account.Protocol.Equals("pop3", StringComparison.OrdinalIgnoreCase))
                await PollPopAsync(connection, key, account, source, password, ct);
            else
                await PollImapAsync(connection, key, account, source, password, ct);
        }
        finally
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await using var unlock = Command(connection, "SELECT pg_advisory_unlock(hashtextextended(@key, 26061118))", key);
            try { await unlock.ExecuteScalarAsync(timeout.Token); }
            catch (Exception ex) when (ex is NpgsqlException or OperationCanceledException or InvalidOperationException)
            {
                logger.LogWarning("邮件轮询锁连接已失效（{Type}）。", ex.GetType().Name);
            }
        }
    }

    private async Task PollImapAsync(NpgsqlConnection db, string key, SortingMailbox account,
        string source, string password, CancellationToken ct)
    {
        using var client = new ImapClient { Timeout = 20_000 };
        await client.ConnectAsync(account.Host, account.Port > 0 ? account.Port : 993, SecureSocketOptions.SslOnConnect, ct);
        await client.AuthenticateAsync(account.Username, password, ct);
        var folder = account.Folder.Equals("INBOX", StringComparison.OrdinalIgnoreCase)
            ? client.Inbox : await client.GetFolderAsync(account.Folder, ct);
        await folder.OpenAsync(FolderAccess.ReadOnly, ct);
        long lastUid = -1;
        long validity = -1;
        await using (var position = Command(db, "SELECT validity, last_uid FROM ai_bot.mail_sync WHERE mailbox = @key", key))
        await using (var reader = await position.ExecuteReaderAsync(ct))
            if (await reader.ReadAsync(ct)) { validity = reader.GetInt64(0); lastUid = reader.GetInt64(1); }

        IReadOnlyList<UniqueId> uids;
        if (lastUid < 0)
        {
            if (!account.ImportRecent)
            {
                // UIDNEXT is the first possible new UID, even when higher UIDs were expunged.
                var next = folder.UidNext?.Id;
                if (next is null)
                {
                    var current = await folder.SearchAsync(SearchQuery.All, ct);
                    next = current.Count == 0 ? 1u : checked(current.Max(uid => uid.Id) + 1);
                }
                await SavePositionAsync(db, key, folder.UidValidity, next.Value - 1, ct);
                return;
            }
            uids = (await folder.SearchAsync(SearchQuery.All, ct)).OrderBy(x => x.Id).TakeLast(20).ToArray();
            await SavePositionAsync(db, key, folder.UidValidity, uids.Count == 0 ? 0 : uids[0].Id - 1, ct);
        }
        else
        {
            if (validity != folder.UidValidity)
            {
                lastUid = 0;
                await SavePositionAsync(db, key, folder.UidValidity, lastUid, ct);
            }
            if (lastUid >= uint.MaxValue || folder.UidNext is { } next && next.Id <= lastUid + 1) return;
            var range = new UniqueIdRange(new UniqueId(checked((uint)lastUid + 1)), UniqueId.MaxValue);
            uids = (await folder.SearchAsync(SearchQuery.Uids(range), ct))
                .Where(uid => uid.Id > lastUid).OrderBy(uid => uid.Id).Take(BatchSize).ToArray();
        }

        var sizes = uids.Count == 0 ? [] : await folder.FetchAsync(uids.ToArray(), new FetchRequest(MessageSummaryItems.UniqueId | MessageSummaryItems.Size), ct);
        foreach (var uid in uids)
        {
            if (sizes.FirstOrDefault(item => item.UniqueId == uid) is { } message)
            {
                if (message.Size is null or <= MailSorting.MaxMailBytes)
                    await ImportAsync(await folder.GetStreamAsync(uid, ct), source, account.Category, ct);
                else
                    logger.LogWarning("已跳过超出大小限制的 IMAP 邮件。");
            }
            await SavePositionAsync(db, key, folder.UidValidity, uid.Id, ct);
        }
    }

    private async Task PollPopAsync(NpgsqlConnection db, string key, SortingMailbox account,
        string source, string password, CancellationToken ct)
    {
        using var client = new Pop3Client { Timeout = 20_000 };
        await client.ConnectAsync(account.Host, account.Port > 0 ? account.Port : 995, SecureSocketOptions.SslOnConnect, ct);
        await client.AuthenticateAsync(account.Username, password, ct);
        if (!client.SupportsUids) throw new NotSupportedException("POP3 UIDL is required.");
        var uids = await client.GetMessageUidsAsync(ct);
        var sizes = await client.GetMessageSizesAsync(ct);

        await using var initialize = Command(db, """
            WITH inserted AS (
                INSERT INTO ai_bot.mail_sync (mailbox) VALUES (@key) ON CONFLICT DO NOTHING RETURNING mailbox
            )
            INSERT INTO ai_bot.mail_pop_seen (mailbox, uid)
            SELECT mailbox, unnest(@ignored) FROM inserted ON CONFLICT DO NOTHING
            """, key);
        initialize.Parameters.AddWithValue("ignored", (account.ImportRecent ? uids.Take(Math.Max(0, uids.Count - 20)) : uids).ToArray());
        await initialize.ExecuteNonQueryAsync(ct);

        HashSet<string> seen = new(StringComparer.Ordinal);
        await using (var lookup = Command(db, "SELECT uid FROM ai_bot.mail_pop_seen WHERE mailbox = @key AND uid = ANY(@uids)", key))
        {
            lookup.Parameters.AddWithValue("uids", uids.ToArray());
            await using var reader = await lookup.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) seen.Add(reader.GetString(0));
        }
        foreach (var index in Enumerable.Range(0, uids.Count).Where(i => !seen.Contains(uids[i])).Take(BatchSize))
        {
            if (sizes[index] <= MailSorting.MaxMailBytes)
                await ImportAsync(await client.GetStreamAsync(index, false, ct), source, account.Category, ct);
            else
                logger.LogWarning("已跳过超出大小限制的 POP3 邮件。");

            await using var save = Command(db, "INSERT INTO ai_bot.mail_pop_seen (mailbox, uid) VALUES (@key, @uid) ON CONFLICT DO NOTHING", key);
            save.Parameters.AddWithValue("uid", uids[index]);
            await save.ExecuteNonQueryAsync(ct);
        }
    }

    private async Task ImportAsync(Stream input, string source, string category, CancellationToken ct)
    {
        await using var stream = input;
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        int count;
        while ((count = await stream.ReadAsync(buffer, ct)) != 0)
        {
            if (output.Length + count > MailSorting.MaxMailBytes)
            {
                logger.LogWarning("已跳过超出大小限制的邮件。");
                return;
            }
            output.Write(buffer, 0, count);
        }
        await using var scope = scopes.CreateAsyncScope();
        var sorting = scope.ServiceProvider.GetRequiredService<IMailSorting>();
        try { await sorting.ImportAsync(output.ToArray(), source, "自动收件", category, ct); }
        catch (InvalidMailException) { logger.LogWarning("已跳过格式无效的邮件。"); }
    }

    private static async Task SavePositionAsync(NpgsqlConnection db, string key, long validity, long lastUid, CancellationToken ct)
    {
        await using var command = Command(db, """
            INSERT INTO ai_bot.mail_sync (mailbox, validity, last_uid) VALUES (@key, @validity, @uid)
            ON CONFLICT (mailbox) DO UPDATE SET validity = excluded.validity, last_uid = excluded.last_uid
            """, key);
        command.Parameters.AddWithValue("validity", validity);
        command.Parameters.AddWithValue("uid", lastUid);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static NpgsqlCommand Command(NpgsqlConnection db, string sql, string key)
    {
        var command = new NpgsqlCommand(sql, db);
        command.Parameters.AddWithValue("key", key);
        return command;
    }
}
