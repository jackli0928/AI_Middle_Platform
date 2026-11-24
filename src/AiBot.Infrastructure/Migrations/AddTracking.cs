using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AiBot.Infrastructure.Migrations;

[DbContext(typeof(BotDbContext))]
[Migration("20261123000100_AddTracking")]
public sealed class AddTracking : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        ALTER TABLE ai_bot.robots ADD "TrackingModelId" uuid NULL;
        CREATE TABLE ai_bot.tracking_commands (
            "Id" uuid PRIMARY KEY, "RobotId" uuid NOT NULL, "SenderUserId" text NOT NULL,
            "Status" text NOT NULL, "Result" text NOT NULL, "Intent" text NOT NULL,
            "CreatedAt" timestamptz NOT NULL, "StartedAt" timestamptz, "CompletedAt" timestamptz
        );
        CREATE INDEX "IX_tracking_commands_Status_CreatedAt"
            ON ai_bot.tracking_commands ("Status", "CreatedAt");
        CREATE INDEX "IX_tracking_commands_RobotId_SenderUserId_CreatedAt"
            ON ai_bot.tracking_commands ("RobotId", "SenderUserId", "CreatedAt");

        CREATE TABLE ai_bot.tracking_subscriptions (
            "Id" uuid PRIMARY KEY, "RobotId" uuid NOT NULL, "SenderUserId" text NOT NULL,
            "Awb" text NOT NULL, "Email" text NOT NULL, "Type" text NOT NULL,
            "Status" text NOT NULL, "UpdatedAt" timestamptz NOT NULL
        );
        CREATE UNIQUE INDEX "IX_tracking_subscriptions_Awb_Email_Type"
            ON ai_bot.tracking_subscriptions ("Awb", "Email", "Type");
        CREATE INDEX "IX_tracking_subscriptions_Status_Awb"
            ON ai_bot.tracking_subscriptions ("Status", "Awb");

        CREATE TABLE ai_bot.tracking_pushes (
            "Awb" text NOT NULL, "Node" text NOT NULL, "PushTime" text NOT NULL, "QueryAddress" text,
            "DetectedAt" timestamptz NOT NULL, PRIMARY KEY ("Awb", "PushTime", "Node")
        );
        CREATE TABLE ai_bot.tracking_refreshes (
            "Awb" text PRIMARY KEY, "QueriedAt" timestamptz NOT NULL, "Result" text NOT NULL
        );
        """);

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("tracking_refreshes", "ai_bot");
        migrationBuilder.DropTable("tracking_pushes", "ai_bot");
        migrationBuilder.DropTable("tracking_subscriptions", "ai_bot");
        migrationBuilder.DropTable("tracking_commands", "ai_bot");
        migrationBuilder.DropColumn("TrackingModelId", "robots", "ai_bot");
    }
}
