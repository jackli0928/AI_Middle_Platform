-- Li AI 机器人中控管理系统：创建初始管理员
-- 连接数据库 li，先执行 001_create_tables.sql，再执行本脚本。
-- 用户名：admin；默认密码见本目录 README.md。
-- PasswordHash 由 ASP.NET Core PasswordHasher<AdminUser> 生成并验证。
-- 仅当管理员表为空时创建；重复执行不会覆盖已有用户、密码或锁定状态。

BEGIN;
SELECT pg_advisory_xact_lock(817202610);

INSERT INTO ai_bot.admin_users
    ("Id", "Username", "NormalizedUsername", "PasswordHash", "FailedAttempts", "LockedUntil")
SELECT
    '3f4a2da7-c259-46a9-a45a-a42627975347'::uuid,
    'admin',
    'ADMIN',
    'AQAAAAIAAYagAAAAENiLAMLynXXANhm4qZZGk4fqJ3fLoOGsfqHvlRIJ42ay9Zw/Vse5pGJO7n1gpxbIZg==',
    0,
    NULL
WHERE NOT EXISTS (SELECT 1 FROM ai_bot.admin_users)
ON CONFLICT ("NormalizedUsername") DO NOTHING;

COMMIT;
