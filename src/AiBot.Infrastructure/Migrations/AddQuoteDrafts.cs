using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AiBot.Infrastructure.Migrations;

[DbContext(typeof(BotDbContext))]
[Migration("20261120000100_AddQuoteDrafts")]
public sealed class AddQuoteDrafts : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE TABLE ai_bot.quote_drafts (
            "Id" uuid PRIMARY KEY, "RobotId" uuid NOT NULL, "ChatType" varchar(10) NOT NULL,
            "ChatId" varchar(256) NOT NULL, "SenderUserId" varchar(256) NOT NULL,
            "Status" varchar(20) NOT NULL, "StateJson" text NOT NULL,
            "RuleVersion" varchar(40) NOT NULL, "UpdatedAt" timestamptz NOT NULL
        );
        CREATE INDEX "IX_quote_drafts_RobotId_ChatType_ChatId_SenderUserId_Status"
            ON ai_bot.quote_drafts ("RobotId", "ChatType", "ChatId", "SenderUserId", "Status");
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropTable("quote_drafts", "ai_bot");
}
