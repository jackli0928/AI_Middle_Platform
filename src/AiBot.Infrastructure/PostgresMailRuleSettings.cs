using System.Text.Json;
using AiBot.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;

namespace AiBot.Infrastructure;

public sealed class PostgresMailRuleSettings(BotDbContext db, LegacyMailRuleSource imported, IOptions<MailSortingOptions> options)
    : IMailRuleSettings
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private NpgsqlConnection Connection => (NpgsqlConnection)db.Database.GetDbConnection();

    public async Task<MailRuleSettings> ReadAsync(CancellationToken ct)
    {
        await db.Database.OpenConnectionAsync(ct);
        await using (var command = new NpgsqlCommand("SELECT revision, settings::text FROM ai_bot.mail_rule_settings WHERE id = 1", Connection))
        await using (var reader = await command.ExecuteReaderAsync(ct))
            if (await reader.ReadAsync(ct))
                return new MailRuleSettings(JsonSerializer.Deserialize<MailRuleSnapshot>(reader.GetString(1), Json)!, reader.GetInt32(0));
        var installed = imported.Read();
        var routes = new Dictionary<string, string>(options.Value.Routes, StringComparer.OrdinalIgnoreCase);
        if (installed is not null)
            foreach (var (address, category) in installed.Routes) routes[address] = category;
        var defaults = installed ?? MailRuleSnapshot.BuiltIn(options.Value.VolumeDivisor);
        return new MailRuleSettings(defaults with { Routes = routes }, 0);
    }

    public async Task<bool> SaveAsync(MailRuleSnapshot snapshot, int expectedRevision, CancellationToken ct)
    {
        await db.Database.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand(expectedRevision == 0
            ? "INSERT INTO ai_bot.mail_rule_settings (id, revision, settings) VALUES (1, 1, @settings) ON CONFLICT (id) DO NOTHING"
            : "UPDATE ai_bot.mail_rule_settings SET revision = revision + 1, settings = @settings WHERE id = 1 AND revision = @expected", Connection);
        command.Parameters.AddWithValue("settings", NpgsqlDbType.Jsonb, JsonSerializer.Serialize(snapshot, Json));
        if (expectedRevision != 0) command.Parameters.AddWithValue("expected", expectedRevision);
        return await command.ExecuteNonQueryAsync(ct) == 1;
    }
}
