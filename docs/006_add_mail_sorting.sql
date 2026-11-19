-- AI 邮件自动分拣增量升级。请先应用 005。
START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM ai_bot."__EFMigrationsHistory" WHERE "MigrationId" = '20261118000500_AddMailSorting') THEN
    CREATE TABLE ai_bot.sorted_mail (
        id uuid PRIMARY KEY, fingerprint text UNIQUE NOT NULL, subject text NOT NULL,
        sender text NOT NULL, source text NOT NULL, received_at timestamptz NOT NULL,
        original bytea NOT NULL, analysis jsonb NOT NULL, review jsonb,
        fixed_category text, revision integer NOT NULL DEFAULT 0
    );
    CREATE INDEX ix_sorted_mail_received ON ai_bot.sorted_mail (received_at DESC, id DESC);
    CREATE TABLE ai_bot.mail_audit (
        id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
        mail_id uuid NOT NULL REFERENCES ai_bot.sorted_mail(id),
        action text NOT NULL, operator text NOT NULL, at timestamptz NOT NULL DEFAULT now()
    );
    CREATE INDEX ix_mail_audit_mail ON ai_bot.mail_audit (mail_id, id DESC);
    CREATE TABLE ai_bot.mail_credentials (
        mailbox text PRIMARY KEY, protected_password text NOT NULL
    );
    CREATE TABLE ai_bot.mail_sync (
        mailbox text PRIMARY KEY, validity bigint NOT NULL DEFAULT 0,
        last_uid bigint NOT NULL DEFAULT 0
    );
    CREATE TABLE ai_bot.mail_pop_seen (
        mailbox text NOT NULL REFERENCES ai_bot.mail_sync(mailbox), uid text NOT NULL,
        PRIMARY KEY (mailbox, uid)
    );
    INSERT INTO ai_bot."__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20261118000500_AddMailSorting', '10.0.0');
    END IF;
END $EF$;

COMMIT;
