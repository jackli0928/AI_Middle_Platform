using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using AiBot.Application;
using AiBot.Domain;
using AiBot.Infrastructure;
using AiBot.Web;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace AiBot.Tests;

internal static class PostgresTests
{
    private static async Task UntilAsync(Func<Task<bool>> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!await condition()) await Task.Delay(50, timeout.Token);
    }
    public static async Task RunAsync(Action<bool,string> check)
    {
        var configPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/AiBot.Web/appsettings.Local.json"));
        using var config = JsonDocument.Parse(await File.ReadAllTextAsync(configPath));
        var settings = new NpgsqlConnectionStringBuilder(config.RootElement.GetProperty("ConnectionStrings").GetProperty("Postgres").GetString()!) { Database = "postgres", Pooling = false };
        await using var admin = new NpgsqlConnection(settings.ConnectionString); await admin.OpenAsync();
        var database = "aibot_verify_" + Guid.NewGuid().ToString("N");
        await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{database}\"", admin)) await create.ExecuteNonQueryAsync();
        settings.Database = database;
        var connection = settings.ConnectionString;
        var options = new DbContextOptionsBuilder<BotDbContext>().UseNpgsql(connection, p => p.MigrationsHistoryTable("__EFMigrationsHistory", "ai_bot")).Options;
        try
        {
            await using var db = new BotDbContext(options); await db.Database.MigrateAsync();
            check((await db.Database.GetAppliedMigrationsAsync()).Count() == 8, "真实 PostgreSQL 空库完整应用全部迁移");
            await MailSortingTests.RunPostgresAsync(db, options, check);
            var autoRobot = Robot.Create("自动回复持久化验证", "auto-test", "protected-test", "", false);
            autoRobot.ConfigureAutoReply(false, "ai", false, "测试提示", [Guid.NewGuid(), Guid.NewGuid()]);
            db.Robots.Add(autoRobot); await db.SaveChangesAsync(); db.ChangeTracker.Clear();
            var persisted = await db.Robots.SingleAsync(x => x.Id == autoRobot.Id);
            check(!persisted.AutoReplyEnabled && !persisted.ReplyToGroups && persisted.GetModelPriority().SequenceEqual(autoRobot.GetModelPriority()), "真实 PostgreSQL 保留关闭状态、群聊 false 和模型优先级顺序");
            var automation = new AutomationRepository(db);
            var claim = new AutoReplyLog(Guid.NewGuid(), autoRobot.Id, "knowledge");
            check(await automation.ClaimAsync(claim, default), "真实 PostgreSQL 成功占用自动回复消息标识");
            await using (var duplicateDb = new BotDbContext(options))
                check(!await new AutomationRepository(duplicateDb).ClaimAsync(new AutoReplyLog(claim.ReceivedMessageId, autoRobot.Id, "knowledge"), default), "真实 PostgreSQL 跨上下文唯一约束阻止重复自动回复");
            db.Robots.Remove(persisted); await db.SaveChangesAsync();
            var repository = new InboxRepository(db);
            var id = Guid.NewGuid();
            using var frame = JsonDocument.Parse(InboxTests.Frame("duplicate", "group"));
            var message = IncomingMessageParser.Parse(id, "集成验证机器人", frame.RootElement)!;
            check(await repository.InsertIfNewAsync(message, default) && !await repository.InsertIfNewAsync(message, default), "真实 PostgreSQL 唯一约束使重复回调只入库一次");
            var anotherRobot = IncomingMessageParser.Parse(Guid.NewGuid(), "另一个机器人", frame.RootElement)!;
            check(await repository.InsertIfNewAsync(anotherRobot, default), "相同消息 ID 在不同机器人下独立去重");
            var results = await repository.ListAsync(new InboxFilter { ChatType = "group", Search = "wr-incoming-group", RobotId = id }, default);
            check(results.Total == 1 && results.Items[0].MessageId == "duplicate", "真实 PostgreSQL 支持机器人、群聊和 Chat ID 联合筛选");
            await using (var lease = await RobotConnectionLease.TryAcquireAsync(connection, "lease-test", default))
            {
                await using var second = await RobotConnectionLease.TryAcquireAsync(connection, "lease-test", default);
                check(lease is not null && second is null, "同一机器人的 PostgreSQL 连接租约只能由一个实例持有");
                await lease!.CheckAsync(default);
            }
            await using (var next = await RobotConnectionLease.TryAcquireAsync(connection, "lease-test", default)) check(next is not null, "连接租约释放后可以重新接管");

            var secrets = new SecretProtector(new EphemeralDataProtectionProvider());
            var robot = Robot.Create("持续连接验证", "test-bot", secrets.Protect("test-secret"), "", true);
            db.Robots.Add(robot); await db.SaveChangesAsync();
            robot.ConfigureAutoReply(true, "quote", false, "", [Guid.NewGuid()]); await db.SaveChangesAsync();
            var quoteMessage = new ReceivedMessage(robot.Id, robot.Name, "quote-once", "req-quote", "sales-user", "", "single", "text", "10件200kg2方到LAX", "{}", null);
            check(await repository.InsertIfNewAsync(quoteMessage, default) && !await repository.InsertIfNewAsync(quoteMessage, default)
                && await db.AutoReplies.CountAsync(x => x.ReceivedMessageId == quoteMessage.Id && x.Status == "queued") == 1,
                "真实 PostgreSQL 将报价单聊和唯一后台任务一起入库");
            robot.ConfigureAutoReply(true, "quote", true, "", []); await db.SaveChangesAsync();
            var groupQuote = new ReceivedMessage(robot.Id, robot.Name, "group-quote-once", "req-group-quote", "sales-user",
                "wr-quote-group", "group", "text", "目的地 LAX，10件，200kg，2方", "{}", null);
            check(await repository.InsertIfNewAsync(groupQuote, default) && !await repository.InsertIfNewAsync(groupQuote, default)
                && await db.AutoReplies.CountAsync(x => x.ReceivedMessageId == groupQuote.Id && x.Status == "queued") == 1,
                "真实 PostgreSQL 在启用群聊回复时将群聊报价唯一入队");
            robot.ConfigureAutoReply(true, "quote", false, "", []); await db.SaveChangesAsync();
            var disabledGroupQuote = new ReceivedMessage(robot.Id, robot.Name, "group-quote-disabled", "req-disabled", "sales-user",
                "wr-quote-group", "group", "text", "目的地 LAX，10件，200kg，2方", "{}", null);
            check(await repository.InsertIfNewAsync(disabledGroupQuote, default)
                && !await db.AutoReplies.AnyAsync(x => x.ReceivedMessageId == disabledGroupQuote.Id),
                "真实 PostgreSQL 在关闭群聊回复时仅保存群聊消息，不触发报价");
            await db.AutoReplies.Where(x => x.ReceivedMessageId == quoteMessage.Id)
                .ExecuteUpdateAsync(x => x.SetProperty(y => y.Status, "skipped"));
            robot.ConfigureAutoReply(true, "quote", true, "", []); await db.SaveChangesAsync();
            var factory = new TestSocketFactory();
            var services = new ServiceCollection(); services.AddLogging();
            var quoteConfig = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>
            {
                ["ConnectionStrings:Postgres"] = connection, ["QuoteMcp:Transport"] = "http",
                ["QuoteMcp:Url"] = "https://quote.example.invalid/mcp", ["QuoteMcp:ToolName"] = "get_air_quote"
            }).Build();
            services.AddSingleton<IConfiguration>(quoteConfig);
            var quoteStub = new QuoteStubHandler();
            using var quoteHttp = new HttpClient(quoteStub);
            services.AddSingleton(new QuoteMcpClient(quoteHttp, quoteConfig));
            services.AddDbContext<BotDbContext>(o => o.UseNpgsql(connection));
            services.AddSingleton<ISecretProtector>(secrets); services.AddSingleton<IWeComSocketFactory>(factory);
            services.AddScoped<IInboxRepository, InboxRepository>(); services.AddScoped<IInboxService, InboxService>(); services.AddSingleton<WeComGateway>();
            services.AddScoped<IAutomationRepository, AutomationRepository>(); services.AddScoped<IRobotRepository, RobotRepository>();
            services.AddScoped<IAutoReplyService, AutoReplyService>();
            var fakeAi = new FakeAiClient(); services.AddSingleton<IAiCompletionClient>(fakeAi);
            services.AddSingleton<IWeComGateway>(p => p.GetRequiredService<WeComGateway>());
            await using var provider = services.BuildServiceProvider();
            var gateway = provider.GetRequiredService<WeComGateway>();
            await gateway.StartAsync(default);
            try
            {
                await UntilAsync(() => Task.FromResult(gateway.GetStates().Any(x => x.RobotId == robot.Id && x.State == "online")));
                check(factory.Sockets.Count == 1, "后台自动连接已启用的机器人");
                var socket = factory.Sockets.Last();
                var quoteWorker = new QuoteWorker(provider.GetRequiredService<IServiceScopeFactory>(), gateway,
                    provider.GetRequiredService<QuoteMcpClient>(), NullLogger<QuoteWorker>.Instance);
                await quoteWorker.StartAsync(default);
                try
                {
                    await UntilAsync(async () => await db.AutoReplies.AsNoTracking()
                        .AnyAsync(x => x.ReceivedMessageId == groupQuote.Id && x.Status == "accepted"));
                    var groupReply = await db.AutoReplies.AsNoTracking().SingleAsync(x => x.ReceivedMessageId == groupQuote.Id);
                    check(socket.SentChatId == "wr-quote-group"
                        && await db.Messages.AsNoTracking().AnyAsync(x => x.Id == groupReply.Id
                            && x.TargetType == "group" && x.ChatId == "wr-quote-group" && x.Operator == "自动回复·报价"),
                        "群聊报价任务经 MCP 后发送到原群 Chat ID，并保存群聊发送审计");
                    var activeSends = socket.Commands.Count(x => x == "aibot_send_msg");
                    socket.Publish(InboxTests.Frame("group-quote-stream", "group", content: "目的地 LAX，10件，200kg，2方"));
                    await UntilAsync(async () => await (from reply in db.AutoReplies.AsNoTracking()
                        join incoming in db.ReceivedMessages.AsNoTracking() on reply.ReceivedMessageId equals incoming.Id
                        where incoming.MessageId == "group-quote-stream" && reply.Status == "accepted"
                        select reply.Id).AnyAsync());
                    var streamFrames = socket.Streams.Where(x => x.RequestId == "callback-1").ToArray();
                    check(streamFrames.Length >= 2 && !streamFrames[0].Finish && streamFrames[^1].Finish
                        && streamFrames.All(x => x.StreamId == streamFrames[0].StreamId)
                        && streamFrames[^1].Content == "群聊测试报价"
                        && socket.Commands.Count(x => x == "aibot_send_msg") == activeSends,
                        "群聊回调显示进度并在原消息结束报价，不额外主动发送");
                    socket.Publish(InboxTests.Frame("group-quote-dimensions", "group", content: "目的地 LAX，2件80kg，每件尺寸60×40×50cm"));
                    await UntilAsync(async () => await (from reply in db.AutoReplies.AsNoTracking()
                        join incoming in db.ReceivedMessages.AsNoTracking() on reply.ReceivedMessageId equals incoming.Id
                        where incoming.MessageId == "group-quote-dimensions" && reply.Status == "accepted"
                        select reply.Id).AnyAsync());
                    check(quoteStub.Arguments.Any(x => x.GetProperty("piece").GetInt32() == 2
                        && Math.Abs(x.GetProperty("volume").GetDouble() - 0.24) < 0.000001
                        && x.GetProperty("all_content").GetString() == "目的地 LAX，2件80kg，每件尺寸60×40×50cm"),
                        "完整尺寸经群聊报价任务换算为 m³ 并传给 MCP");
                    var toolCalls = quoteStub.Arguments.Count;
                    socket.Publish(InboxTests.Frame("group-quote-incomplete", "group", content: "目的地 LAX，2件80kg，尺寸60×40×50cm"));
                    await UntilAsync(async () => await (from reply in db.AutoReplies.AsNoTracking()
                        join incoming in db.ReceivedMessages.AsNoTracking() on reply.ReceivedMessageId equals incoming.Id
                        where incoming.MessageId == "group-quote-incomplete" && reply.Status == "accepted"
                        select reply.Id).AnyAsync());
                    check(quoteStub.Arguments.Count == toolCalls && await (from reply in db.AutoReplies.AsNoTracking()
                        join incoming in db.ReceivedMessages.AsNoTracking() on reply.ReceivedMessageId equals incoming.Id
                        where incoming.MessageId == "group-quote-incomplete" && reply.ReplyContent.Contains("每组尺寸对应的件数")
                        select reply.Id).AnyAsync(),
                        "尺寸组数不明确时先在原群请求补充，不调用 MCP");
                    socket.Publish(InboxTests.Frame("group-quote-other-sender", "group", content: "0.8方")
                        .Replace("\"userid\":\"receiver-test\"", "\"userid\":\"another-user\"", StringComparison.Ordinal));
                    await UntilAsync(async () => await (from reply in db.AutoReplies.AsNoTracking()
                        join incoming in db.ReceivedMessages.AsNoTracking() on reply.ReceivedMessageId equals incoming.Id
                        where incoming.MessageId == "group-quote-other-sender" && reply.Status == "accepted"
                        select reply.Id).AnyAsync());
                    check(quoteStub.Arguments.Count == toolCalls && await (from reply in db.AutoReplies.AsNoTracking()
                        join incoming in db.ReceivedMessages.AsNoTracking() on reply.ReceivedMessageId equals incoming.Id
                        where incoming.MessageId == "group-quote-other-sender" && reply.ReplyContent.Contains("目的地机场")
                        select reply.Id).AnyAsync(), "同群其他发送人的体积不会补入当前业务员的询价");
                    socket.Publish(InboxTests.Frame("group-quote-supplement", "group", content: "每件尺寸60×40×50cm"));
                    await UntilAsync(async () => await (from reply in db.AutoReplies.AsNoTracking()
                        join incoming in db.ReceivedMessages.AsNoTracking() on reply.ReceivedMessageId equals incoming.Id
                        where incoming.MessageId == "group-quote-supplement" && reply.Status == "accepted"
                        select reply.Id).AnyAsync());
                    check(quoteStub.Arguments.Any(x => x.GetProperty("all_content").GetString()
                        == "目的地 LAX，2件80kg，尺寸60×40×50cm\n每件尺寸60×40×50cm"
                        && Math.Abs(x.GetProperty("volume").GetDouble() - 0.24) < 0.000001)
                        && await db.QuoteDrafts.AsNoTracking().AnyAsync(x => x.RobotId == robot.Id && x.Status == "quoted"),
                        "真实群聊跨消息补齐尺寸后引用本票原文查价，并持久化完成状态");
                    var afterSupplement = quoteStub.Arguments.Count;
                    socket.Publish(InboxTests.Frame("group-quote-new-partial", "group", content: "目的地 SFO，3件90kg"));
                    await UntilAsync(async () => await (from reply in db.AutoReplies.AsNoTracking()
                        join incoming in db.ReceivedMessages.AsNoTracking() on reply.ReceivedMessageId equals incoming.Id
                        where incoming.MessageId == "group-quote-new-partial" && reply.Status == "accepted"
                        select reply.Id).AnyAsync());
                    check(quoteStub.Arguments.Count == afterSupplement, "新票缺体积时追问，不复用上一票体积");
                    socket.Publish(InboxTests.Frame("group-quote-new-volume", "group", content: "总体积0.5方"));
                    await UntilAsync(async () => await (from reply in db.AutoReplies.AsNoTracking()
                        join incoming in db.ReceivedMessages.AsNoTracking() on reply.ReceivedMessageId equals incoming.Id
                        where incoming.MessageId == "group-quote-new-volume" && reply.Status == "accepted"
                        select reply.Id).AnyAsync());
                    check(quoteStub.Arguments.Any(x => x.GetProperty("destination").GetString() == "SFO"
                        && x.GetProperty("piece").GetInt32() == 3 && x.GetProperty("volume").GetDouble() == 0.5
                        && x.GetProperty("all_content").GetString() == "目的地 SFO，3件90kg\n总体积0.5方"),
                        "新票增量补充体积后查价，MCP 原文不混入上一票");
                    var quoteModel = new AiModelEndpoint();
                    quoteModel.Update("字段模型", "测试", "https://model.example.invalid/chat/completions", "test-model", "chat",
                        secrets.Protect("test-key"), true, 5, 512);
                    db.AiModels.Add(quoteModel);
                    robot.ConfigureAutoReply(true, "quote", true, "", [quoteModel.Id]); await db.SaveChangesAsync();
                    fakeAi.Generate = _ => throw new AiCompletionException("模型生成未完成，切换下一模型。");
                    var shorthand = "2pallets/589.19kgs/120x100x106cm+120x100x102cm，货在东莞，可深圳/广州出，飞HYD，货好了";
                    var beforeTwoRoutes = socket.Commands.Count(x => x == "aibot_send_msg");
                    socket.Publish(InboxTests.Frame("group-quote-model-fallback", "group", content: shorthand));
                    await UntilAsync(async () => await (from reply in db.AutoReplies.AsNoTracking()
                        join incoming in db.ReceivedMessages.AsNoTracking() on reply.ReceivedMessageId equals incoming.Id
                        where incoming.MessageId == "group-quote-model-fallback" && reply.Status == "accepted"
                        select reply.Id).AnyAsync());
                    check(socket.Commands.Count(x => x == "aibot_send_msg") == beforeTwoRoutes + 1
                        && quoteStub.Arguments.Count(x => x.GetProperty("all_content").GetString() == shorthand) == 2
                        && quoteStub.Arguments.Any(x => x.GetProperty("destination").GetString() == "HYD"
                            && Math.Abs(x.GetProperty("volume").GetDouble() - 2.496) < 0.000001)
                        && await (from reply in db.AutoReplies.AsNoTracking()
                            join incoming in db.ReceivedMessages.AsNoTracking() on reply.ReceivedMessageId equals incoming.Id
                            where incoming.MessageId == "group-quote-model-fallback" && reply.Attempts.Contains("模型生成未完成")
                            select reply.Id).AnyAsync(),
                        "模型响应不可用时记录原因，明确的英文简写仍可经两个起运地查价");
                    fakeAi.Generate = _ => "{\"fields\":{\"destination\":{\"value\":\"LAX\",\"evidence\":\"洛杉矶\"}}}";
                    socket.Publish(InboxTests.Frame("group-quote-model-extract", "group", content: "发洛杉矶，2件80kg，0.3方"));
                    await UntilAsync(async () => await (from reply in db.AutoReplies.AsNoTracking()
                        join incoming in db.ReceivedMessages.AsNoTracking() on reply.ReceivedMessageId equals incoming.Id
                        where incoming.MessageId == "group-quote-model-extract" && reply.Status == "accepted"
                        select reply.Id).AnyAsync());
                    check(quoteStub.Arguments.Any(x => x.GetProperty("all_content").GetString() == "发洛杉矶，2件80kg，0.3方"
                        && x.GetProperty("destination").GetString() == "LAX"),
                        "模型凭当前客户原文证据补全自然语言目的地后调用 MCP");
                    var longQuote = string.Concat(Enumerable.Repeat("报价😀\n", 1500));
                    quoteStub.CustomerReply = longQuote;
                    var streamBeforeLong = socket.Streams.Count;
                    var sendsBeforeLong = socket.SentContents.Count;
                    socket.Publish(InboxTests.Frame("group-quote-long-reply", "group", content: "发洛杉矶，2件80kg，0.3方"));
                    await UntilAsync(async () => await (from reply in db.AutoReplies.AsNoTracking()
                        join incoming in db.ReceivedMessages.AsNoTracking() on reply.ReceivedMessageId equals incoming.Id
                        where incoming.MessageId == "group-quote-long-reply" && reply.Status == "accepted"
                        select reply.Id).AnyAsync());
                    var finalChunks = socket.Streams.Skip(streamBeforeLong).Where(x => x.Finish).Select(x => x.Content).ToArray();
                    var proactiveChunks = socket.SentContents.Skip(sendsBeforeLong).ToArray();
                    check(finalChunks.Length == 1 && proactiveChunks.Length > 0
                        && finalChunks[0] + string.Concat(proactiveChunks) == longQuote,
                        "超长 MCP 报价在企微分条发送完整内容，不按 4096 字节截断");
                }
                finally { using var quoteShutdown = new CancellationTokenSource(TimeSpan.FromSeconds(10)); await quoteWorker.StopAsync(quoteShutdown.Token); }
                robot.ConfigureAutoReply(false, "knowledge", true, "", []); await db.SaveChangesAsync();
                socket.Publish(InboxTests.Frame("persistent-incoming", "group")); socket.Publish(InboxTests.Frame("persistent-incoming", "group"));
                await UntilAsync(async () => await db.ReceivedMessages.CountAsync(x => x.RobotId == robot.Id && x.MessageId == "persistent-incoming") == 1);
                check(await db.ReceivedMessages.CountAsync(x => x.RobotId == robot.Id && x.MessageId == "persistent-incoming") == 1, "持续连接收到的消息经过生产存储路径入库并去重");
                var receipt = await gateway.SendAsync(robot.BotId, "test-secret", "group", "wr-incoming-group", "simulated only", default);
                check(receipt.Ok && factory.Sockets.Count == 1, "主动发送复用接收连接，不另开 WebSocket");
                var knowledge = new KnowledgeEntry(); knowledge.Update(robot.Id, "自动回复集成测试", "", "自动回复测试答案", "exact", 10, true);
                db.KnowledgeEntries.Add(knowledge); robot.ConfigureAutoReply(true, "knowledge", true, "", []); await db.SaveChangesAsync();
                var callback = InboxTests.Frame("auto-integration", "group", content: "自动回复集成测试");
                var sendCount = socket.Commands.Count(x => x == "aibot_send_msg"); socket.Publish(callback); socket.Publish(callback);
                await UntilAsync(async () => await db.Messages.AnyAsync(x => x.RobotId == robot.Id && x.Operator == "自动回复·知识库" && x.Status == DeliveryStatus.Accepted));
                check(socket.Commands.Count(x => x == "aibot_send_msg") == sendCount + 1 && socket.SentChatId == "wr-incoming-group", "真实 PostgreSQL 回调入库、知识匹配、复用连接回复及重复回调去重完整链路");
                check(await db.Messages.AnyAsync(x => x.RobotId == robot.Id && x.Operator == "自动回复·知识库"), "自动回复同时写入生产发送审计记录");
                socket.Abort();
                await UntilAsync(() => Task.FromResult(factory.Sockets.Count == 2 && gateway.GetStates().Any(x => x.State == "online")));
                check(factory.Sockets.Count == 2, "网络断开后后台退避重连并重新认证");
                factory.Sockets.Last().Publish("""{"cmd":"aibot_event_callback","body":{"event":{"eventtype":"disconnected_event"}}}""");
                await UntilAsync(() => Task.FromResult(gateway.GetStates().Any(x => x.State == "conflict")));
                gateway.Reconnect(robot.Id);
                await UntilAsync(() => Task.FromResult(factory.Sockets.Count == 3 && gateway.GetStates().Any(x => x.State == "online")));
                check(factory.Sockets.Count == 3, "被其他客户端接管后暂停，管理员可手动重新连接");
                robot.Update(robot.Name, robot.BotId, null, "", false); await db.SaveChangesAsync();
                await UntilAsync(() => Task.FromResult(gateway.GetStates().Any(x => x.State == "disabled")));
                check(factory.Sockets.Last().State != WebSocketState.Open, "停用机器人自动关闭连接");
                robot.Update(robot.Name, robot.BotId, secrets.Protect("new-secret"), "", true); await db.SaveChangesAsync();
                await UntilAsync(() => Task.FromResult(factory.Sockets.Count == 4 && gateway.GetStates().Any(x => x.State == "online")));
                check((await gateway.SendAsync(robot.BotId, "new-secret", "single", "user", "simulated", default)).Ok, "启用和更换 Secret 后自动建立新连接");
                db.Robots.Remove(robot); await db.SaveChangesAsync();
                await UntilAsync(() => Task.FromResult(gateway.GetStates().Count == 0));
                check(await db.ReceivedMessages.CountAsync(x => x.RobotId == robot.Id) == 15, "删除机器人关闭连接并保留历史接收记录");
            }
            finally { using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(15)); await gateway.StopAsync(shutdown.Token); }
        }
        finally
        {
            // Only the uniquely named disposable database created by this run is removed.
            if (!database.StartsWith("aibot_verify_", StringComparison.Ordinal) || database.Length != 45) throw new InvalidOperationException("Unexpected verification database name.");
            await using var drop = new NpgsqlCommand($"DROP DATABASE \"{database}\" WITH (FORCE)", admin); await drop.ExecuteNonQueryAsync();
        }
    }
    private sealed class TestSocketFactory : IWeComSocketFactory
    {
        public ConcurrentQueue<PersistentSocket> Sockets { get; } = new();
        public Task<WebSocket> ConnectAsync(CancellationToken ct) { var socket = new PersistentSocket(); Sockets.Enqueue(socket); return Task.FromResult<WebSocket>(socket); }
    }
    private sealed class QuoteStubHandler : HttpMessageHandler
    {
        public ConcurrentQueue<JsonElement> Arguments { get; } = new();
        public string CustomerReply { get; set; } = "群聊测试报价";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var method = json.RootElement.GetProperty("method").GetString();
            if (method == "tools/call") Arguments.Enqueue(json.RootElement.GetProperty("params").GetProperty("arguments").Clone());
            var body = method switch
            {
                "initialize" => "{\"result\":{\"protocolVersion\":\"2025-03-26\",\"capabilities\":{}}}",
                "tools/call" => JsonSerializer.Serialize(new { result = new { structuredContent = new { status = "quoted", customerReply = CustomerReply } } }),
                _ => "{}"
            };
            return new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
}
