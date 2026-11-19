using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AiBot.Infrastructure.Migrations;

[DbContext(typeof(BotDbContext))]
[Migration("20261118000600_AddMailboxSettings")]
public sealed class AddMailboxSettings : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE TABLE ai_bot.mailbox_settings (
            id uuid PRIMARY KEY,
            mailbox text UNIQUE NOT NULL,
            settings jsonb NOT NULL
        );
        """);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql("DROP TABLE ai_bot.mailbox_settings;");
}
