using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AiBot.Application;
using AiBot.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MimeKit;
using Npgsql;
using NpgsqlTypes;

namespace AiBot.Infrastructure;

public sealed class PostgresMailSorting(BotDbContext db, IOptions<MailSortingOptions> options,
    MailModelSuggestions model, LegacyMailAnalyzer? legacy = null, LegacyMailRuleSource? rules = null, IMailboxSettings? mailboxes = null, IMailRuleSettings? ruleSettings = null, ConfiguredMailModel? configuredModel = null) : IMailSorting
{
    private NpgsqlConnection Connection => (NpgsqlConnection)db.Database.GetDbConnection();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly string[] SummaryKeys = ["origin", "destination", "customer", "pieces", "gross", "volume", "commodity_cn"];
    private const string Columns = "id, fingerprint, subject, sender, source, received_at, revision, analysis::text, review::text";

    public async Task<IReadOnlyList<MailListItem>> ListAsync(string search, string category, string status, int page, CancellationToken ct)
    {
        await db.Database.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand("""
            SELECT id, subject, sender, source, coalesce(review->>'category', analysis->>'category') AS category,
                coalesce((review->>'confirmed')::boolean, false) AS confirmed, received_at,
                (SELECT jsonb_agg(field)::text
                 FROM jsonb_array_elements(analysis->'fields') AS field WHERE field->>'key' = ANY(@summary_keys)),
                (review->'fields')::text
            FROM ai_bot.sorted_mail
            WHERE (@query = '' OR strpos(lower(subject), lower(@query)) > 0 OR strpos(lower(sender), lower(@query)) > 0)
              AND (@category = '' OR coalesce(review->>'category', analysis->>'category') = @category)
              AND (@status = '' OR coalesce(review->>'confirmed', 'false') = @status)
            ORDER BY received_at DESC, id DESC LIMIT @limit OFFSET @offset
            """, Connection);
        command.Parameters.AddWithValue("query", search);
        command.Parameters.AddWithValue("summary_keys", SummaryKeys);
        command.Parameters.AddWithValue("category", category);
        command.Parameters.AddWithValue("status", status);
        command.Parameters.AddWithValue("limit", MailSorting.PageSize + 1);
        command.Parameters.AddWithValue("offset", (page - 1) * MailSorting.PageSize);
        await using var reader = await command.ExecuteReaderAsync(ct);
        List<MailListItem> list = [];
        while (await reader.ReadAsync(ct))
            list.Add(new(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.GetBoolean(5), reader.GetFieldValue<DateTimeOffset>(6))
            {
                Summary = ReadSummary(reader.IsDBNull(7) ? null : reader.GetString(7),
                    reader.IsDBNull(8) ? null : reader.GetString(8))
            });
        return list;
    }

    private static IReadOnlyDictionary<string, string> ReadSummary(string? analysis, string? review)
    {
        var summary = new Dictionary<string, string>(StringComparer.Ordinal);
        if (analysis is not null)
            foreach (var field in JsonSerializer.Deserialize<MailField[]>(analysis, Json) ?? [])
                if (!field.HasConflict && field.Candidates.FirstOrDefault() is { Value.Length: > 0 } candidate)
                    summary[field.Key] = candidate.Value;
        if (review is not null)
            foreach (var (key, value) in JsonSerializer.Deserialize<Dictionary<string, string>>(review, Json) ?? [])
                if (SummaryKeys.Contains(key) && !string.IsNullOrWhiteSpace(value))
                    summary[key] = value;
        return summary;
    }

    public async Task<SortedMail?> FindAsync(Guid id, CancellationToken ct)
    {
        await db.Database.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand("SELECT " + Columns + " FROM ai_bot.sorted_mail WHERE id = @id", Connection);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
            reader.GetFieldValue<DateTimeOffset>(5), reader.GetInt32(6), JsonSerializer.Deserialize<MailAnalysis>(reader.GetString(7), Json)!,
            reader.IsDBNull(8) ? null : JsonSerializer.Deserialize<MailReview>(reader.GetString(8), Json));
    }

    public async Task<IReadOnlyList<MailAudit>> AuditAsync(Guid id, CancellationToken ct)
    {
        await db.Database.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand("""
            SELECT action, operator, at FROM ai_bot.mail_audit WHERE mail_id = @id ORDER BY id DESC LIMIT 50
            """, Connection);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(ct);
        List<MailAudit> events = [];
        while (await reader.ReadAsync(ct))
            events.Add(new(reader.GetString(0), reader.GetString(1), reader.GetFieldValue<DateTimeOffset>(2)));
        return events;
    }

    public async Task<Guid> ImportAsync(byte[] raw, string source, string actor, string? category, CancellationToken ct)
    {
        var mime = MailMessageReader.Read(raw);
        var fingerprint = string.IsNullOrWhiteSpace(mime.MessageId)
            ? "raw:" + Convert.ToHexString(SHA256.HashData(raw)) : MessageKey(mime.MessageId);
        await db.Database.OpenConnectionAsync(ct);
        if (await FindFingerprintAsync(fingerprint, ct) is { } existing) return existing;
        var id = Guid.NewGuid();
        var snapshot = await ReadRulesAsync(ct);
        var fixedCategory = MailSorting.Categories.ContainsKey(category ?? "") ? category : await RouteAsync(mime, snapshot, ct);
        var analysis = await AnalyzeAsync(raw, mime, fixedCategory, null, snapshot, ct);
        await using var transaction = await Connection.BeginTransactionAsync(ct);
        await using var command = new NpgsqlCommand("""
            INSERT INTO ai_bot.sorted_mail (id, fingerprint, subject, sender, source, received_at, original, analysis, fixed_category)
            VALUES (@id, @fingerprint, @subject, @sender, @source, @at, @raw, @analysis, @category)
            ON CONFLICT (fingerprint) DO NOTHING
            """, Connection, transaction);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("fingerprint", fingerprint);
        var subject = MailMessageReader.Trim(mime.Subject, 1000);
        command.Parameters.AddWithValue("subject", subject.Length > 0 ? subject : "（无主题）");
        command.Parameters.AddWithValue("sender", MailMessageReader.Trim(mime.From.ToString(), 1000));
        command.Parameters.AddWithValue("source", source);
        command.Parameters.AddWithValue("at", DateTimeOffset.UtcNow);
        command.Parameters.AddWithValue("raw", raw);
        command.Parameters.AddWithValue("analysis", NpgsqlDbType.Jsonb, JsonSerializer.Serialize(analysis, Json));
        command.Parameters.AddWithValue("category", NpgsqlDbType.Text, (object?)fixedCategory ?? DBNull.Value);
        var inserted = await command.ExecuteNonQueryAsync(ct) == 1;
        if (inserted) await AddAuditAsync(transaction, id, "收件", actor, ct);
        await transaction.CommitAsync(ct);
        return inserted ? id : (await FindFingerprintAsync(fingerprint, ct))!.Value;
    }

    public Task<bool> ReviewAsync(SortedMail mail, MailReview review, string actor, CancellationToken ct) =>
        UpdateAsync(mail, "review", review, review.Confirmed ? "确认复核" : "保存复核", actor, ct);

    public async Task<bool> ReanalyzeAsync(SortedMail mail, string actor, CancellationToken ct, bool useConfiguredModel = false)
    {
        await db.Database.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand("SELECT original, fixed_category FROM ai_bot.sorted_mail WHERE id = @id", Connection);
        command.Parameters.AddWithValue("id", mail.Id);
        byte[] raw;
        string? category;
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct)) return false;
            raw = reader.GetFieldValue<byte[]>(0);
            category = mail.Review?.Category ?? (reader.IsDBNull(1) ? null : reader.GetString(1));
        }
        var snapshot = await ReadRulesAsync(ct);
        if (useConfiguredModel && (snapshot?.RecognitionModelId is null || configuredModel is null))
            throw new InvalidMailException("请先在邮件识别规则中选择可用的 AI 模型接口。");
        var mime = MailMessageReader.Read(raw);
        var analysis = await AnalyzeAsync(raw, mime, category, mail.Fingerprint, snapshot, ct, useConfiguredModel);
        if (useConfiguredModel)
            analysis = await configuredModel!.SuggestAsync(snapshot!.RecognitionModelId!.Value, mail, analysis, category, ct);
        return await UpdateAsync(mail, "analysis", analysis, useConfiguredModel ? "AI 辅助识别" : "重新识别", actor, ct);
    }

    public async Task<byte[]?> OriginalAsync(Guid id, CancellationToken ct)
    {
        await db.Database.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand("SELECT original FROM ai_bot.sorted_mail WHERE id = @id", Connection);
        command.Parameters.AddWithValue("id", id);
        return await command.ExecuteScalarAsync(ct) as byte[];
    }

    public async Task<(byte[] Data, string Name)?> AttachmentAsync(Guid id, int index, CancellationToken ct) =>
        await OriginalAsync(id, ct) is { } raw ? MailMessageReader.Download(MailMessageReader.Read(raw), index) : null;

    private async Task<Guid?> FindFingerprintAsync(string fingerprint, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("SELECT id FROM ai_bot.sorted_mail WHERE fingerprint = @fingerprint", Connection);
        command.Parameters.AddWithValue("fingerprint", fingerprint);
        return await command.ExecuteScalarAsync(ct) as Guid?;
    }

    private async Task<MailAnalysis> AnalyzeAsync(byte[] raw, MimeMessage mime, string? category, string? existing, MailRuleSnapshot? snapshot, CancellationToken ct, bool rulesOnly = false)
    {
        var references = mime.References.Reverse().Prepend(mime.InReplyTo).OfType<string>()
            .Where(x => !string.IsNullOrWhiteSpace(x)).Take(20).Select(MessageKey).Distinct().Where(x => x != existing).ToArray();
        List<MailAnalysis> related = [];
        List<byte[]> relatedOriginals = [];
        if (references.Length > 0)
        {
            await using var command = new NpgsqlCommand("""
                SELECT analysis::text,
                    CASE WHEN @include_original AND octet_length(original) <= 4 * 1024 * 1024 THEN original END
                FROM ai_bot.sorted_mail WHERE fingerprint = ANY(@refs)
                ORDER BY array_position(@refs, fingerprint) LIMIT 5
                """, Connection);
            command.Parameters.AddWithValue("refs", references);
            command.Parameters.AddWithValue("include_original", legacy is not null &&
                Path.IsPathFullyQualified(options.Value.PythonExecutable) && Path.IsPathFullyQualified(options.Value.PythonParserDirectory));
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                related.Add(JsonSerializer.Deserialize<MailAnalysis>(reader.GetString(0), Json)!);
                if (!reader.IsDBNull(1)) relatedOriginals.Add(reader.GetFieldValue<byte[]>(1));
            }
        }
        var subject = MailMessageReader.Trim(mime.Subject, 1000);
        var result = MailSortingRules.Analyze(subject, MailMessageReader.Body(mime), MailMessageReader.Previews(mime),
            references, category, related, snapshot?.VolumeDivisor ?? Math.Clamp(options.Value.VolumeDivisor, 1, 1_000_000), snapshot);
        var original = legacy is null ? null : await legacy.AnalyzeAsync(raw, result, category, ct, relatedOriginals, snapshot, allowOllama: !rulesOnly);
        var parsed = original ?? result;
        DateOnly? mailDate = mime.Headers.Contains(HeaderId.Date) && mime.Date != DateTimeOffset.MinValue
            ? DateOnly.FromDateTime(mime.Date.Date) : null;
        var enriched = BookingInstructionRules.Enrich(parsed, subject, mailDate);
        return original is not null || rulesOnly ? enriched : await model.SuggestAsync(subject, enriched, category, ct);
    }

    private async Task<MailRuleSnapshot?> ReadRulesAsync(CancellationToken ct) =>
        ruleSettings is null ? rules?.Read() : (await ruleSettings.ReadAsync(ct)).Snapshot;

    private async Task<string?> RouteAsync(MimeMessage mime, MailRuleSnapshot? snapshot, CancellationToken ct)
    {
        var routes = new Dictionary<string, string>(ruleSettings is null ? options.Value.Routes : new(),
            StringComparer.OrdinalIgnoreCase);
        if (snapshot is not null)
            foreach (var (address, category) in snapshot.Routes) routes[address] = category;
        var managed = mailboxes is null ? Array.Empty<MailboxSettings>() : await mailboxes.ListAsync(ct);
        foreach (var account in managed.Select(m => m.ToMailbox()).Concat(options.Value.Mailboxes)
            .DistinctBy(SortingMailboxIdentity.Key).Reverse())
            if (account.Address.Length > 0 &&
                (MailSorting.Categories.ContainsKey(account.Category) || !routes.ContainsKey(account.Address)))
                routes[account.Address] = account.Category;

        var delivered = mime.Headers.Where(h => h.Field.Equals("Delivered-To", StringComparison.OrdinalIgnoreCase) ||
                h.Field.Equals("X-Original-To", StringComparison.OrdinalIgnoreCase) ||
                h.Field.Equals("Envelope-To", StringComparison.OrdinalIgnoreCase))
            .SelectMany(h => InternetAddressList.TryParse(h.Value, out var addresses) ? addresses.Mailboxes : []);
        foreach (var group in new[] { delivered, mime.To.Mailboxes.Concat(mime.Cc.Mailboxes) })
        {
            var matched = group.Select(a => routes.GetValueOrDefault(a.Address)).OfType<string>().ToArray();
            if (matched.Length == 0) continue;
            var categories = matched.Where(MailSorting.Categories.ContainsKey).Distinct().ToArray();
            return categories.Length > 1 ? "review" : categories.SingleOrDefault();
        }
        return null;
    }

    private static string MessageKey(string id) =>
        "id:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id.Trim().Trim('<', '>'))));

    private async Task<bool> UpdateAsync<T>(SortedMail mail, string column, T value, string action, string actor, CancellationToken ct)
    {
        await db.Database.OpenConnectionAsync(ct);
        await using var transaction = await Connection.BeginTransactionAsync(ct);
        var sql = column == "review"
            ? "UPDATE ai_bot.sorted_mail SET review = @value, revision = revision + 1 WHERE id = @id AND revision = @revision"
            : "UPDATE ai_bot.sorted_mail SET analysis = @value, revision = revision + 1 WHERE id = @id AND revision = @revision";
        await using var command = new NpgsqlCommand(sql, Connection, transaction);
        command.Parameters.AddWithValue("value", NpgsqlDbType.Jsonb, JsonSerializer.Serialize(value, Json));
        command.Parameters.AddWithValue("id", mail.Id);
        command.Parameters.AddWithValue("revision", mail.Revision);
        if (await command.ExecuteNonQueryAsync(ct) != 1) return false;
        await AddAuditAsync(transaction, mail.Id, action, actor, ct);
        await transaction.CommitAsync(ct);
        return true;
    }

    private async Task AddAuditAsync(NpgsqlTransaction transaction, Guid id, string action, string actor, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO ai_bot.mail_audit (mail_id, action, operator) VALUES (@id, @action, @actor)
            """, Connection, transaction);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("action", action);
        command.Parameters.AddWithValue("actor", actor);
        await command.ExecuteNonQueryAsync(ct);
    }
}
