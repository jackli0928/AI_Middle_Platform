using System.Text.Json;
using AiBot.Application;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace AiBot.Infrastructure;

public sealed class PostgresMailboxSettings(BotDbContext db, ISecretProtector secrets) : IMailboxSettings
{
    private NpgsqlConnection Connection => (NpgsqlConnection)db.Database.GetDbConnection();

    public async Task<IReadOnlyList<MailboxSettings>> ListAsync(CancellationToken ct)
    {
        await db.Database.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand("""
            SELECT id, settings::text, protected_password IS NOT NULL
            FROM ai_bot.mailbox_settings s LEFT JOIN ai_bot.mail_credentials c USING (mailbox)
            ORDER BY settings->>'Name', id
            """, Connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        List<MailboxSettings> items = [];
        while (await reader.ReadAsync(ct))
        {
            var account = JsonSerializer.Deserialize<SortingMailbox>(reader.GetString(1))!;
            items.Add(new MailboxSettings
            {
                Id = reader.GetGuid(0), Name = account.Name, Host = account.Host, Port = account.Port,
                Protocol = account.Protocol, Username = account.Username, Address = account.Address, Folder = account.Folder,
                Category = account.Category, Enabled = account.Enabled, ImportRecent = account.ImportRecent,
                HasPassword = reader.GetBoolean(2)
            });
        }
        return items;
    }

    public async Task SaveAsync(MailboxSettings input, CancellationToken ct)
    {
        var account = input.ToMailbox();
        var key = SortingMailboxIdentity.Key(account);
        await db.Database.OpenConnectionAsync(ct);
        await using var transaction = await Connection.BeginTransactionAsync(ct);
        await using var save = new NpgsqlCommand(input.Id is null
            ? "INSERT INTO ai_bot.mailbox_settings (id, mailbox, settings) VALUES (@id, @key, @settings)"
            : "UPDATE ai_bot.mailbox_settings SET mailbox = @key, settings = @settings WHERE id = @id", Connection, transaction);
        save.Parameters.AddWithValue("id", input.Id ?? Guid.NewGuid());
        save.Parameters.AddWithValue("key", key);
        save.Parameters.AddWithValue("settings", NpgsqlDbType.Jsonb, JsonSerializer.Serialize(account));
        try
        {
            if (await save.ExecuteNonQueryAsync(ct) != 1)
                throw new InvalidMailException("邮箱配置已不存在。");
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new InvalidMailException("同一协议、服务器、账号和文件夹已配置。");
        }

        if (!string.IsNullOrWhiteSpace(input.Password))
        {
            await using var password = new NpgsqlCommand("""
                INSERT INTO ai_bot.mail_credentials (mailbox, protected_password) VALUES (@key, @secret)
                ON CONFLICT (mailbox) DO UPDATE SET protected_password = excluded.protected_password
                """, Connection, transaction);
            password.Parameters.AddWithValue("key", key);
            password.Parameters.AddWithValue("secret", secrets.Protect(input.Password));
            await password.ExecuteNonQueryAsync(ct);
        }
        else if (account.Enabled)
        {
            await using var exists = new NpgsqlCommand(
                "SELECT EXISTS (SELECT 1 FROM ai_bot.mail_credentials WHERE mailbox = @key)", Connection, transaction);
            exists.Parameters.AddWithValue("key", key);
            if (await exists.ExecuteScalarAsync(ct) is not true)
                throw new InvalidMailException("启用邮箱前请填写授权码。");
        }
        await transaction.CommitAsync(ct);
    }
}
