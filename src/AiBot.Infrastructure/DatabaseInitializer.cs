using AiBot.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AiBot.Infrastructure;

public static class DatabaseInitializer
{
    public static async Task InitializeAsync(BotDbContext db, string? username, string? password, CancellationToken ct)
    {
        await db.Database.MigrateAsync(ct);
        if (await db.Users.AnyAsync(ct)) return;
        if (string.IsNullOrWhiteSpace(username) || username.Length > 80 || password is null || password.Length is < 12 or > 128)
            throw new InvalidOperationException("首次启动请设置 BootstrapAdmin:Username 和至少 12 位的 BootstrapAdmin:Password。");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(817202610)", ct);
        if (!await db.Users.AnyAsync(ct))
        {
            var user = new AdminUser { Username = username.Trim(), NormalizedUsername = username.Trim().ToUpperInvariant() };
            user.PasswordHash = new PasswordHasher<AdminUser>().HashPassword(user, password);
            db.Users.Add(user); await db.SaveChangesAsync(ct);
        }
        await tx.CommitAsync(ct);
    }
}

[DbContext(typeof(BotDbContext))]
[Migration("202611160001_InitialCreate")]
public sealed class InitialCreate : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE SCHEMA IF NOT EXISTS ai_bot;
        CREATE TABLE ai_bot.robots (
            "Id" uuid PRIMARY KEY, "Name" varchar(80) NOT NULL, "BotId" varchar(256) NOT NULL,
            "ProtectedSecret" varchar(2048) NOT NULL, "Description" varchar(500) NOT NULL,
            "Enabled" boolean NOT NULL, "CreatedAt" timestamptz NOT NULL, "UpdatedAt" timestamptz NOT NULL
        );
        CREATE UNIQUE INDEX "IX_robots_BotId" ON ai_bot.robots ("BotId");
        CREATE TABLE ai_bot.message_logs (
            "Id" uuid PRIMARY KEY, "RobotId" uuid NOT NULL, "RobotName" varchar(80) NOT NULL,
            "UserId" varchar(256) NOT NULL, "Content" varchar(4096) NOT NULL, "Operator" varchar(80) NOT NULL,
            "Status" varchar(20) NOT NULL, "Result" varchar(2048) NOT NULL, "RequestId" varchar(128),
            "ErrorCode" integer, "CreatedAt" timestamptz NOT NULL
        );
        CREATE INDEX "IX_message_logs_CreatedAt" ON ai_bot.message_logs ("CreatedAt");
        CREATE TABLE ai_bot.admin_users (
            "Id" uuid PRIMARY KEY, "Username" varchar(80) NOT NULL, "NormalizedUsername" varchar(80) NOT NULL,
            "PasswordHash" varchar(512) NOT NULL, "FailedAttempts" integer NOT NULL, "LockedUntil" timestamptz
        );
        CREATE UNIQUE INDEX "IX_admin_users_NormalizedUsername" ON ai_bot.admin_users ("NormalizedUsername");
        """);
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("message_logs", "ai_bot");
        migrationBuilder.DropTable("robots", "ai_bot");
        migrationBuilder.DropTable("admin_users", "ai_bot");
    }
}
