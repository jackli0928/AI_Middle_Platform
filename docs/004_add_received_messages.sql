-- 接收消息增量升级；连接 li 后执行。请先应用群聊迁移（003）。

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM ai_bot."__EFMigrationsHistory" WHERE "MigrationId" = '20261116000300_AddReceivedMessages') THEN
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
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM ai_bot."__EFMigrationsHistory" WHERE "MigrationId" = '20261116000300_AddReceivedMessages') THEN
    INSERT INTO ai_bot."__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20261116000300_AddReceivedMessages', '10.0.0');
    END IF;
END $EF$;
COMMIT;

