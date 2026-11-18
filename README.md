# Li · AI 机器人中控管理系统

基于 **.NET 10 / ASP.NET Core MVC / PostgreSQL / EF Core** 的中文机器人管理后台，采用 DDD 与清洁架构。数据库为 `li`，业务 schema 为 `ai_bot`。前端为服务端 Razor MVC，使用本地 CSS、JavaScript 和 SVG 图标，无需 Node.js、前端构建或外部 CDN。

## 本地启动

分发源码时请保留根目录的 `Directory.Build.props`，所有项目通过它统一配置 `net10.0`、隐式 using 和可空引用类型。缺少该文件会导致构建时报“无效的框架标识符‘’”。可以清理 `bin`、`obj`、`artifacts` 等构建产物；给其他用户的全新部署包不要包含 `appsettings.Local.json`、`App_Data` 密钥目录或实际数据库数据，保留 `appsettings.Local.example.json` 供部署者填写。已有系统升级时应保留其原有配置和密钥目录。

1. 确保 .NET 10 SDK 和本地 PostgreSQL 已安装，PostgreSQL 正在运行。
2. 将 `src/AiBot.Web/appsettings.Local.example.json` 复制为同目录的 `appsettings.Local.json`。
3. 填写 PostgreSQL 用户名、密码和管理员初始密码（12–128 位）。默认地址为 `localhost:5432`，数据库名 `li`。该本地文件已加入 `.gitignore`。
4. 运行：

```powershell
dotnet build AiBot.sln
dotnet run --project src/AiBot.Web
```

打开 <http://localhost:5088>，使用配置的管理员用户名和密码登录。

如需通过 Navicat / psql 手动初始化，按 [docs/README.md](docs/README.md) 依次执行建表脚本和初始管理员脚本；这种方式无需再配置 `BootstrapAdmin:Password`。

首次启动会应用版本化 EF 迁移，在 `ai_bot` schema 下创建 `robots`、`message_logs`、`received_messages`、`admin_users` 和迁移历史表，并初始化管理员。若数据库不存在且连接用户拥有 `CREATEDB` 权限，EF 会创建 `li`；否则请先在 PostgreSQL / Navicat 中创建 `li`。连接用户需要该数据库的 CONNECT 权限及 schema / 表创建权限。已有其他 schema 的表不会通过 EnsureCreated 被忽略，也不会被本系统改动。

数据库或管理员配置不完整时，登录页显示初始化提示，`/health` 返回 HTTP 503。后台每 30 秒重试；修改 JSON 配置后需重启应用。成功后 `/health` 返回 HTTP 200。该健康接口表示本次进程是否已完成初始化，不持续探测数据库连通性。

管理员只在用户表为空时创建；后续修改 `BootstrapAdmin` 不会覆盖现有密码。首次登录后可在“账号安全”中修改密码。遗忘密码需通过管理员维护流程重置数据库中的密码哈希，不能将明文直接写入 `PasswordHash`。

也可使用环境变量覆盖 JSON：

```text
ConnectionStrings__Postgres=Host=localhost;Port=5432;Database=li;Username=postgres;Password=你的密码
BootstrapAdmin__Username=admin
BootstrapAdmin__Password=你的管理员初始密码
```

## 使用流程

1. 打开“企微机器人”，添加名称、Bot ID、Secret、备注和启用状态。编辑 Secret 留空会保留原有凭据。Bot ID 不允许重复。
2. 在企微后台开启智能机器人的 API 长连接模式，确认接收人在机器人可见范围内。
3. 打开“发送消息”，选择已启用的机器人和发送对象类型。单聊填写实际企微 User ID；群聊填写该机器人收到的群聊消息回调中的 `body.chatid`，并确认机器人已加入群聊。然后填写 Markdown 内容。
4. 发送后自动进入“消息记录”。点击消息内容，弹窗查看完整原文、发送对象、发送时间、操作人、回执说明、错误码和 request ID。

消息最多 **4096 个 UTF-8 字节**，按字节计数；每次支持一个账号或一个群，不支持多个 ID、`@all`、姓名或昵称替代 userid、群名称替代 chatid。发送成功仅表示企业微信已接受请求，并不表示已读。界面显示原文预览，Markdown 的实际渲染以企微客户端为准。历史消息保留为单聊，新增群聊记录保存 Chat ID；记录页支持按账号或 Chat ID 搜索。

发送沿用参考项目的企微 WebSocket 协议，现已改为每个启用的机器人维护一个持久连接。连接 `wss://openws.work.weixin.qq.com`，通过 `aibot_subscribe` 认证，主动发送使用 `aibot_send_msg` 并复用已有连接。回执按 `headers.req_id` 分发，支持分片并校验 `errcode`；认证、发送和心跳回执等待上限为 10 秒。每个机器人的业务发送串行处理，心跳和接收持续运行。

## 接收消息

“接收消息”页面显示机器人连接状态、最近心跳和收到的消息，支持按机器人、单聊/群聊、类型、发送人、Chat ID 或内容筛选。列表每 15 秒局部刷新，可取消自动刷新；无需一直打开页面，接收由后台服务执行。详情显示完整消息文本及脱敏回调，可跳转“向此会话发送”预填目标，仍需手动点击发送。

后台每 3 秒同步机器人配置。启用后连接，停用或删除后断开，修改名称、Bot ID 或 Secret 后重新建立连接。每 30 秒发送心跳，连接或心跳异常按 1、2、4、8、16、30 秒退避重连；稳定连接一分钟后重置退避。认证失败、密钥不可读或收到 `disconnected_event`（其他客户端接管）时暂停，排除原因后可点击“重新连接”，也可重新保存机器人配置。自动回复默认关闭，需在机器人编辑页单独开启；不会自动重试未确认的发送。

消息通过 `aibot_msg_callback` 接收，以 `(RobotId, MessageId)` 唯一索引去重，保存发送人、会话类型、群 Chat ID、消息类型、内容、企微时间（回调提供时）和本地接收时间。文本、语音转写和图文中的文字可直接查看；图片、文件和视频保留类型占位和脱敏元数据，不自动下载附件，不保存媒体解密密钥、临时回复地址或媒体下载地址。页面使用 Razor 转义展示消息，不执行回调内容。

接收缓存为每个机器人最多 64 帧、每帧最大 1 MiB；存储失败最多尝试 3 次。会话关闭时给已经收到的回调最多 10 秒写入数据库。该缓存不具备持久队列的保证：进程异常退出、数据库长时间不可用或离线期间的消息可能无法保存，企微没有在本系统使用的机器人协议中提供历史补拉接口。删除机器人保留已接收记录。

多实例通过 PostgreSQL 会话级 advisory lock 对同一 Bot ID 选定唯一连接持有者；锁使用独立数据库连接，每次心跳验证数据库连接。其他实例显示等待接管，主动发送应路由到持有连接的实例。多个实例仍需共享同一密钥环和服务身份；不在同一数据库中的其他机器人客户端无法通过该锁协调，请避免重复接入同一 Bot ID。

协议依据：[企业微信官方 SDK](https://github.com/WecomTeam/aibot-node-sdk)。

发送前先保存待回执记录，收到回执后更新状态。每次表单附带唯一 operation ID，数据库主键约束阻止重复提交触发二次发送。超时或断线后的发送结果标为“结果未确认”，不自动重试。如果进程退出或数据库在写回时不可用，记录可能停留在“等待回执 / 待核实”；应先检查企微会话，避免重复触达。删除机器人保留历史记录。

## 自动回复、知识库与模型配置

1. 在“知识库问答”维护问题、换行分隔的匹配词及答案，选择精确或包含匹配，可限定机器人或共享使用。优先级数字越小越优先；同优先级优先使用机器人专属条目，然后使用匹配词更长的条目。匹配前统一全角、大小写及空白，不提供向量检索。未匹配则不回复。
2. 在“AI 模型接口”为各具体模型创建配置。支持 OpenAI Responses 和兼容 Chat Completions 的接口，提供 OpenAI、DeepSeek、智谱的地址示例；填写完整 API URL、接口实际支持的模型 ID 及 API Key。展示名称可自定义，不代表提供方存在同名模型。密钥加密保存，编辑时留空保留，页面不回显。保存不发起外部请求。
3. 编辑机器人，开启“收到消息后自动回复”，选择知识库或 AI。AI 可设置最多 8 个不同模型的优先级及自定义回复要求，按顺序尝试已启用的模型。单模型超时可设 5–30 秒，每次生成与发送总预算 60 秒；接口报错、超时、无有效文本时尝试下一模型，全部失败则不发送。

单聊，以及企微通过机器人回调投递的群聊中发给／@机器人的消息适用；群聊可单独关闭。忽略机器人自身或明确标记为机器人的消息。支持文本、语音转写及图文中的文字，不下载附件。AI 仅发送当前消息文本和系统提示，不携带历史会话、知识库或工具。启用 AI 回复后，文本会发送至所选的模型提供方。Chat 兼容接口需支持 `max_tokens`，Responses 使用 `max_output_tokens`。

仅处理新入库的回调，不补发历史消息。每条接收记录由数据库唯一约束占用一次自动回复资格；生成完成后再次检查机器人是否仍启用。回复通过现有长连接的主动发送协议发到原 User ID／Chat ID，最终文本限制为 4096 UTF-8 字节。结果未知或进程中断不会自动重发，记录可能保留为生成中／待核实，应先检查企微会话。自动回复记录可在接收消息详情查看，已发出的回复同时进入“消息记录”。

数据库升级脚本为 `docs/005_add_auto_reply.sql`；应用启动自动执行同一迁移，已有机器人默认关闭自动回复。真实模型联调需自行填入可用接口及密钥；自动检查使用模拟 HTTP 和模拟企微连接。

## 架构

```text
src/
  AiBot.Domain/          机器人聚合、消息记录、管理员实体
  AiBot.Application/     用例服务、仓储与外部服务接口、输入输出模型
  AiBot.Infrastructure/ PostgreSQL 仓储、EF 迁移、凭据加密、登录实现、企微 WebSocket
  AiBot.Web/            MVC 控制器、Razor 视图、认证与中间件、静态资源、依赖注入
tests/
  AiBot.Tests/          可执行自动检查与隔离界面预览
```

依赖方向：Domain 无外部依赖；Application 依赖 Domain；Infrastructure 实现 Application 接口；Web 负责组合依赖。业务规则放在实体和应用用例中。

## 验证

```powershell
dotnet build AiBot.sln
dotnet run --project tests/AiBot.Tests --no-build
```

使用本地 PostgreSQL 验证存储与连接生命周期：`dotnet run --project tests/AiBot.Tests --no-build -- --postgres`。它使用本地配置的凭据创建随机命名的临时测试数据库并在结束时清理，不修改 `li`，企微仍为可控模拟连接，不向真实用户发消息；数据库用户需有创建和删除测试数据库的权限。

自动检查通过 MVC 集成请求验证匿名跳转、CSRF、登录、防外部重定向、机器人配置、Secret 加密和不回显、单账号和字节限制、重复提交、发送回执、删除后保留记录；使用可控 WebSocket 验证认证失败、分片、请求关联、异常回执和错误信息脱敏；校验 EF 迁移快照与模型一致。使用内存仓储和模拟企微，不连接真实 PostgreSQL，不发送真实消息。

可以单独运行隔离界面预览：

```powershell
dotnet run --project tests/AiBot.Tests --no-build -- --preview
```

打开 <http://localhost:5099>，账号 `reviewer`，密码 `PreviewOnly!2026`。这是测试工程专用的内存数据与模拟发送，关闭即消失；正式 Web 工程没有演示账号或绕过登录的开关。

## 部署与数据保护

使用 `dotnet publish src/AiBot.Web -c Release -o artifacts/publish` 发布。配置正式数据库连接、域名 `AllowedHosts` 和 HTTPS。正式环境 Cookie 要求 HTTPS；如经反向代理终止 TLS，需按实际可信代理配置 ASP.NET Core Forwarded Headers，再启用 HTTPS 跳转，不应信任任意来源代理头。

机器人 Secret 通过 ASP.NET Core Data Protection 加密。密钥保存在 `src/AiBot.Web/App_Data/keys`（发布后为应用目录下 `App_Data/keys`），Windows 使用当前服务账号的 DPAPI 加密新密钥。应持久化并限制密钥目录权限，备份数据库时同时备份密钥；丢失密钥或更换 Windows 服务身份可能导致现有 Secret 无法解密，需重新配置。Linux 部署应使用适当证书或外部密钥服务保护密钥文件。多实例需共享同一密钥环和应用名，并将主动发送请求路由到持有机器人连接的实例。

密码使用 ASP.NET Core PasswordHasher 哈希；登录失败 5 次锁定 15 分钟；登录和改密入口按 IP 限流；所有 MVC 写操作校验 CSRF；登录会话为 8 小时滑动有效；页面禁止缓存，并配置 CSP、禁止嵌入与 MIME 嗅探保护。消息原文和账号会保存在审计记录中，应限制数据库访问并按组织需要设置保留策略。

参考官方说明：[Cookie 认证](https://learn.microsoft.com/aspnet/core/security/authentication/cookie?view=aspnetcore-10.0)、[CSRF 防护](https://learn.microsoft.com/aspnet/core/security/anti-request-forgery?view=aspnetcore-10.0)、[Npgsql EF Core 10](https://www.npgsql.org/efcore/release-notes/10.0.html)。
