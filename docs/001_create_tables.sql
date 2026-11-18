-- Li AI 机器人中控管理系统：PostgreSQL 建表脚本
-- 请连接数据库 li 后执行；若 li 不存在，请先单独执行 CREATE DATABASE li;
-- 来源：EF Core 版本化迁移，包含 ai_bot schema、业务表、索引和迁移历史。
-- 可重复执行；已应用的迁移会跳过，不清空已有数据。

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM pg_namespace WHERE nspname = 'ai_bot') THEN
        CREATE SCHEMA ai_bot;
    END IF;
END $EF$;
CREATE TABLE IF NOT EXISTS ai_bot."__EFMigrationsHistory" (
    "MigrationId" character varying(150) NOT NULL,
    "ProductVersion" character varying(32) NOT NULL,
    CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY ("MigrationId")
);

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM ai_bot."__EFMigrationsHistory" WHERE "MigrationId" = '202611160001_InitialCreate') THEN
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
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM ai_bot."__EFMigrationsHistory" WHERE "MigrationId" = '202611160001_InitialCreate') THEN
    INSERT INTO ai_bot."__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('202611160001_InitialCreate', '10.0.0');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM ai_bot."__EFMigrationsHistory" WHERE "MigrationId" = '20261116000200_AddGroupMessaging') THEN
    ALTER TABLE ai_bot.message_logs ADD "TargetType" character varying(10) NOT NULL DEFAULT 'single';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM ai_bot."__EFMigrationsHistory" WHERE "MigrationId" = '20261116000200_AddGroupMessaging') THEN
    ALTER TABLE ai_bot.message_logs ADD "ChatId" character varying(256) NOT NULL DEFAULT '';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM ai_bot."__EFMigrationsHistory" WHERE "MigrationId" = '20261116000200_AddGroupMessaging') THEN
    INSERT INTO ai_bot."__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20261116000200_AddGroupMessaging', '10.0.0');
    END IF;
END $EF$;
COMMIT;

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

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM ai_bot."__EFMigrationsHistory" WHERE "MigrationId" = '20261116000400_AddAutoReply') THEN
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
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM ai_bot."__EFMigrationsHistory" WHERE "MigrationId" = '20261116000400_AddAutoReply') THEN
    INSERT INTO ai_bot."__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20261116000400_AddAutoReply', '10.0.0');
    END IF;
END $EF$;
COMMIT;

