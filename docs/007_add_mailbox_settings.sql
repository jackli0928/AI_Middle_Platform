-- 收件邮箱页面配置迁移。请先应用 006。
START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM ai_bot."__EFMigrationsHistory" WHERE "MigrationId" = '20261118000600_AddMailboxSettings') THEN
        CREATE TABLE ai_bot.mailbox_settings (
            id uuid PRIMARY KEY,
            mailbox text UNIQUE NOT NULL,
            settings jsonb NOT NULL
        );
        INSERT INTO ai_bot."__EFMigrationsHistory" ("MigrationId", "ProductVersion")
        VALUES ('20261118000600_AddMailboxSettings', '10.0.0');
    END IF;
END $EF$;

COMMIT;
