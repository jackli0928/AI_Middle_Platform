using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AiBot.Infrastructure.Migrations;

[DbContext(typeof(BotDbContext))]
[Migration("20261118000700_AddEditableMailRules")]
public sealed class AddEditableMailRules : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE TABLE ai_bot.mail_rule_settings (
            id integer PRIMARY KEY CHECK (id = 1),
            revision integer NOT NULL CHECK (revision > 0),
            settings jsonb NOT NULL
        );
        """);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql("DROP TABLE ai_bot.mail_rule_settings;");
}
