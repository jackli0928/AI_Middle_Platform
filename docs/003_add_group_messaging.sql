-- 群聊发送增量升级；连接数据库 li 后执行。
-- 用于已执行过初始建表脚本的数据库；旧消息自动保留为 single。
-- 不修改机器人配置、消息内容或管理员密码。

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

