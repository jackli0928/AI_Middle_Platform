-- 邮件识别规则编辑迁移。请先应用 007。
START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM ai_bot."__EFMigrationsHistory" WHERE "MigrationId" = '20261118000700_AddEditableMailRules') THEN
        CREATE TABLE ai_bot.mail_rule_settings (
            id integer PRIMARY KEY CHECK (id = 1),
            revision integer NOT NULL CHECK (revision > 0),
            settings jsonb NOT NULL
        );
        INSERT INTO ai_bot."__EFMigrationsHistory" ("MigrationId", "ProductVersion")
        VALUES ('20261118000700_AddEditableMailRules', '10.0.0');
    END IF;
END $EF$;

COMMIT;
