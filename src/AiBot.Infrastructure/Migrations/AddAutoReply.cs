using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AiBot.Infrastructure.Migrations;

[DbContext(typeof(BotDbContext))]
[Migration("20261116000400_AddAutoReply")]
public sealed class AddAutoReply : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        ALTER TABLE ai_bot.robots ADD "AutoReplyEnabled" boolean NOT NULL DEFAULT false;
        ALTER TABLE ai_bot.robots ADD "ReplyMode" varchar(10) NOT NULL DEFAULT 'knowledge';
        ALTER TABLE ai_bot.robots ADD "ReplyToGroups" boolean NOT NULL DEFAULT true;
        ALTER TABLE ai_bot.robots ADD "ReplyPrompt" varchar(2000) NOT NULL DEFAULT '';
        ALTER TABLE ai_bot.robots ADD "ModelPriorityJson" text NOT NULL DEFAULT '[]';
        CREATE TABLE ai_bot.knowledge_entries (
            "Id" uuid PRIMARY KEY, "RobotId" uuid, "Question" varchar(300) NOT NULL, "Keywords" varchar(2000) NOT NULL,
            "Answer" varchar(4096) NOT NULL, "MatchMode" varchar(10) NOT NULL, "Priority" integer NOT NULL,
            "Enabled" boolean NOT NULL, "UpdatedAt" timestamptz NOT NULL
        );
        CREATE INDEX "IX_knowledge_entries_RobotId_Enabled_Priority" ON ai_bot.knowledge_entries ("RobotId", "Enabled", "Priority");
        CREATE TABLE ai_bot.ai_models (
            "Id" uuid PRIMARY KEY, "Name" varchar(80) NOT NULL, "Provider" varchar(80) NOT NULL,
            "Endpoint" varchar(1024) NOT NULL, "ModelId" varchar(256) NOT NULL, "ApiStyle" varchar(20) NOT NULL,
            "ProtectedApiKey" varchar(8192) NOT NULL, "Enabled" boolean NOT NULL,
            "TimeoutSeconds" integer NOT NULL, "MaxOutputTokens" integer NOT NULL, "UpdatedAt" timestamptz NOT NULL
        );
        CREATE TABLE ai_bot.auto_reply_logs (
            "Id" uuid PRIMARY KEY, "ReceivedMessageId" uuid NOT NULL, "RobotId" uuid NOT NULL,
            "Mode" varchar(10) NOT NULL, "Status" varchar(20) NOT NULL, "ModelName" varchar(150) NOT NULL,
            "ReplyContent" varchar(4096) NOT NULL, "Result" varchar(2048) NOT NULL, "Attempts" varchar(4000) NOT NULL,
            "CreatedAt" timestamptz NOT NULL, "CompletedAt" timestamptz
        );
        CREATE UNIQUE INDEX "IX_auto_reply_logs_ReceivedMessageId" ON ai_bot.auto_reply_logs ("ReceivedMessageId");
        CREATE INDEX "IX_auto_reply_logs_RobotId" ON ai_bot.auto_reply_logs ("RobotId");
        """);
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("auto_reply_logs", "ai_bot"); migrationBuilder.DropTable("ai_models", "ai_bot"); migrationBuilder.DropTable("knowledge_entries", "ai_bot");
        foreach (var column in new[] { "AutoReplyEnabled", "ReplyMode", "ReplyToGroups", "ReplyPrompt", "ModelPriorityJson" }) migrationBuilder.DropColumn(column, "robots", "ai_bot");
    }
}
