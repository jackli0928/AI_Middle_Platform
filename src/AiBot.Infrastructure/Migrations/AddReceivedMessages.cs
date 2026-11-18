using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AiBot.Infrastructure.Migrations;

[DbContext(typeof(BotDbContext))]
[Migration("20261116000300_AddReceivedMessages")]
public sealed class AddReceivedMessages : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE TABLE ai_bot.received_messages (
            "Id" uuid PRIMARY KEY, "RobotId" uuid NOT NULL, "RobotName" varchar(80) NOT NULL,
            "MessageId" varchar(256) NOT NULL, "RequestId" varchar(256) NOT NULL,
            "SenderUserId" varchar(256) NOT NULL, "ChatId" varchar(256) NOT NULL, "ChatType" varchar(10) NOT NULL,
            "MessageType" varchar(64) NOT NULL, "Content" text NOT NULL, "PayloadJson" text NOT NULL,
            "SentAt" timestamptz, "ReceivedAt" timestamptz NOT NULL
        );
        CREATE UNIQUE INDEX "IX_received_messages_RobotId_MessageId" ON ai_bot.received_messages ("RobotId", "MessageId");
        CREATE INDEX "IX_received_messages_ReceivedAt" ON ai_bot.received_messages ("ReceivedAt");
        CREATE INDEX "IX_received_messages_ChatId" ON ai_bot.received_messages ("ChatId");
        CREATE INDEX "IX_received_messages_SenderUserId" ON ai_bot.received_messages ("SenderUserId");
        """);
    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropTable("received_messages", "ai_bot");
}
