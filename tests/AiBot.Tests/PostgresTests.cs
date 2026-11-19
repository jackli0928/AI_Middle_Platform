using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using AiBot.Application;
using AiBot.Domain;
using AiBot.Infrastructure;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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
            check((await db.Database.GetAppliedMigrationsAsync()).Count() == 7, "真实 PostgreSQL 空库完整应用全部迁移");
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
            var factory = new TestSocketFactory();
            var services = new ServiceCollection(); services.AddLogging();
            services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["ConnectionStrings:Postgres"] = connection }).Build());
            services.AddDbContext<BotDbContext>(o => o.UseNpgsql(connection));
            services.AddSingleton<ISecretProtector>(secrets); services.AddSingleton<IWeComSocketFactory>(factory);
            services.AddScoped<IInboxRepository, InboxRepository>(); services.AddScoped<IInboxService, InboxService>(); services.AddSingleton<WeComGateway>();
            services.AddScoped<IAutomationRepository, AutomationRepository>(); services.AddScoped<IRobotRepository, RobotRepository>();
            services.AddScoped<IAutoReplyService, AutoReplyService>(); services.AddSingleton<IAiCompletionClient, FakeAiClient>();
            services.AddSingleton<IWeComGateway>(p => p.GetRequiredService<WeComGateway>());
            await using var provider = services.BuildServiceProvider();
            var gateway = provider.GetRequiredService<WeComGateway>();
            await gateway.StartAsync(default);
            try
            {
                await UntilAsync(() => Task.FromResult(gateway.GetStates().Any(x => x.RobotId == robot.Id && x.State == "online")));
                check(factory.Sockets.Count == 1, "后台自动连接已启用的机器人");
                var socket = factory.Sockets.Last();
                socket.Publish(InboxTests.Frame("persistent-incoming", "group")); socket.Publish(InboxTests.Frame("persistent-incoming", "group"));
                await UntilAsync(async () => await db.ReceivedMessages.CountAsync(x => x.RobotId == robot.Id) == 1);
                check(await db.ReceivedMessages.CountAsync(x => x.RobotId == robot.Id) == 1, "持续连接收到的消息经过生产存储路径入库并去重");
                var receipt = await gateway.SendAsync(robot.BotId, "test-secret", "group", "wr-incoming-group", "simulated only", default);
                check(receipt.Ok && factory.Sockets.Count == 1, "主动发送复用接收连接，不另开 WebSocket");
                var knowledge = new KnowledgeEntry(); knowledge.Update(robot.Id, "自动回复集成测试", "", "自动回复测试答案", "exact", 10, true);
                db.KnowledgeEntries.Add(knowledge); robot.ConfigureAutoReply(true, "knowledge", true, "", []); await db.SaveChangesAsync();
                var callback = InboxTests.Frame("auto-integration", "group", content: "自动回复集成测试");
                var sendCount = socket.Commands.Count(x => x == "aibot_send_msg"); socket.Publish(callback); socket.Publish(callback);
                await UntilAsync(async () => await db.AutoReplies.AnyAsync(x => x.RobotId == robot.Id && x.Status == "accepted"));
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
                check(await db.ReceivedMessages.CountAsync(x => x.RobotId == robot.Id) == 2, "删除机器人关闭连接并保留历史接收记录");
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
}
