# 数据库初始化脚本

数据库：`li`；schema：`ai_bot`；本地连接默认使用 `localhost:5432`、用户名 `postgres`。数据库密码已配置在被 Git 忽略的 `src/AiBot.Web/appsettings.Local.json` 中，SQL 文件无需填写数据库密码。

## 执行顺序

1. 在 Navicat 或 psql 中连接本地 PostgreSQL。如果 `li` 不存在，先在其他维护数据库（例如 `postgres`）中单独执行 `CREATE DATABASE li;`，不要放在事务中。
2. 切换连接到数据库 **li**，执行 `001_create_tables.sql`。
3. 在同一个数据库执行 `002_create_initial_admin.sql`。
4. 重启应用，打开 <http://localhost:5088> 登录。

初始管理账号：

| 项目 | 值 |
| --- | --- |
| 用户名 | `admin` |
| 初始密码 | `AiBot@2026Admin!` |

这是系统登录密码，与 PostgreSQL 的数据库连接密码不同。登录后可在“账号安全”中修改。SQL 中只保存 ASP.NET Core PasswordHasher 生成的带盐哈希，不能用普通 SHA256、MD5 或 PostgreSQL `crypt()` 替换。

`001` 直接从当前 EF Core 迁移生成，包含 `ai_bot.robots`、`ai_bot.message_logs`、`ai_bot.admin_users`、唯一索引、消息时间索引及 `ai_bot.__EFMigrationsHistory`。脚本按迁移历史判断是否执行；之后应用的 `MigrateAsync` 不会再次创建这些表。请勿将其他手工建表脚本与此脚本混用，或仅执行一部分 SQL 而遗漏迁移历史。

`002` 使用与应用初始化相同的事务锁，仅当管理员表为空时插入一名管理员。重复执行不会重置已有密码，也不会额外创建账号；如果表中已有用户，请使用现有账号。机器人 Secret 仍由应用加密，不能手工往 `ProtectedSecret` 字段填入明文。

两个脚本均支持重复执行，不删除已有数据。当前仅生成文件，尚未对实际数据库执行这些脚本。

## 群聊支持升级

已使用旧版本建表脚本的数据库可执行 `003_add_group_messaging.sql`，增加消息记录的 `TargetType` 和 `ChatId` 字段。历史消息默认归类为单聊，已有 User ID 和内容保持不变。新安装直接使用最新的 `001_create_tables.sql` 即包含这些字段。

应用启动时也会自动执行同一 EF 迁移，已执行的脚本会依据迁移历史跳过。群聊填写机器人收到的群聊消息回调中的 `body.chatid`，机器人需已加入目标群；不能填写群名称。消息记录支持按 Chat ID 搜索。

## 接收消息支持升级

已有系统执行 `004_add_received_messages.sql`（先应用 `003`）即可增加 `ai_bot.received_messages` 表及按机器人与消息 ID 去重的唯一索引。应用启动也会自动应用同一迁移。新安装直接执行最新的 `001`，该文件已包含全部迁移。接收记录独立于主动发送记录，不修改现有机器人、管理员或历史消息。

## 自动回复支持升级

已有数据库在 `004` 后执行 `005_add_auto_reply.sql`，增加机器人自动回复配置及 `knowledge_entries`、`ai_models`、`auto_reply_logs` 三张表。应用启动会自动应用同一迁移；新安装使用最新的 `001` 即包含全部结构。已有机器人默认关闭自动回复，原有凭据、用户和消息保持不变。

使用步骤：在“知识库问答”添加共享或机器人专属问答；或者在“AI 模型接口”配置完整 URL、实际模型 ID 和 API Key。再到机器人编辑页开启自动回复，选择知识库或 AI，AI 模式可按顺序配置最多 8 个模型。配置保存不会请求模型接口。API Key 与机器人 Secret 使用同一应用密钥环加密，请一并备份密钥目录。

## 重新生成数据库脚本

在项目根目录运行：

```powershell
dotnet run --project tools/AiBot.DatabaseScripts -- docs
```

生成器不连接数据库。可在运行前设置环境变量 `AIBOT_INITIAL_ADMIN_PASSWORD`，生成使用自定义初始密码（12–128 位）的管理员脚本；设置自定义密码后，以上默认密码不再适用。重新生成会覆盖 SQL 文件，且密码哈希因随机盐而改变。只更新建表与增量脚本、保留已有初始管理员脚本可运行 `dotnet run --project tools/AiBot.DatabaseScripts -- docs --schema-only`。
