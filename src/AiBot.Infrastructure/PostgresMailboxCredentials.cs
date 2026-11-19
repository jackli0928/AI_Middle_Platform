using AiBot.Application;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AiBot.Infrastructure;

public sealed class PostgresMailboxCredentials(BotDbContext db, ISecretProtector secrets) : IMailboxCredentials
{
    public async Task<bool> HasPasswordAsync(string mailbox, CancellationToken ct)
    {
        await db.Database.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand("""
            SELECT EXISTS (SELECT 1 FROM ai_bot.mail_credentials WHERE mailbox = @mailbox)
            """, (NpgsqlConnection)db.Database.GetDbConnection());
        command.Parameters.AddWithValue("mailbox", mailbox);
        return (bool)(await command.ExecuteScalarAsync(ct))!;
    }

    public async Task SavePasswordAsync(string mailbox, string password, CancellationToken ct)
    {
        await db.Database.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand("""
            INSERT INTO ai_bot.mail_credentials (mailbox, protected_password) VALUES (@mailbox, @password)
            ON CONFLICT (mailbox) DO UPDATE SET protected_password = excluded.protected_password
            """, (NpgsqlConnection)db.Database.GetDbConnection());
        command.Parameters.AddWithValue("mailbox", mailbox);
        command.Parameters.AddWithValue("password", secrets.Protect(password));
        await command.ExecuteNonQueryAsync(ct);
    }
}
