using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AiBot.Infrastructure.Migrations;

[DbContext(typeof(BotDbContext))]
[Migration("20261116000200_AddGroupMessaging")]
public sealed class AddGroupMessaging : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>("TargetType", "message_logs", schema: "ai_bot", type: "character varying(10)", maxLength: 10, nullable: false, defaultValue: "single");
        migrationBuilder.AddColumn<string>("ChatId", "message_logs", schema: "ai_bot", type: "character varying(256)", maxLength: 256, nullable: false, defaultValue: "");
    }
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn("ChatId", "message_logs", "ai_bot");
        migrationBuilder.DropColumn("TargetType", "message_logs", "ai_bot");
    }
}
