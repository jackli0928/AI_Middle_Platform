using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AiBot.Application;
using AiBot.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace AiBot.Infrastructure;

public sealed class WeComBackgroundWorker(WeComGateway gateway) : IHostedService
{
    public Task StartAsync(CancellationToken ct) => gateway.StartAsync(ct);
    public Task StopAsync(CancellationToken ct) => gateway.StopAsync(ct);
}

public sealed class WeComGateway(IServiceScopeFactory scopes, ISecretProtector secrets, IConfiguration configuration, ILogger<WeComGateway> logger, IWeComSocketFactory sockets)
    : BackgroundService, IWeComGateway, IConnectionMonitor
{
    private readonly ConcurrentDictionary<Guid, Worker> workers = new();
    private readonly ConcurrentDictionary<Guid, RobotConnectionState> states = new();
    private readonly ConcurrentDictionary<Guid, byte> restarts = new();
    public IReadOnlyList<RobotConnectionState> GetStates() => states.Values.OrderBy(x => x.RobotName).ToList();
    public bool HasSession(string botId) => workers.Values.Any(x => x.Robot.BotId == botId && Volatile.Read(ref x.Session) is not null);
    public void Reconnect(Guid robotId) => restarts[robotId] = 0;
    public Task<SendResult> SendAsync(string botId, string secret, string targetType, string targetId, string content, CancellationToken ct)
    {
        if (targetType is not ("single" or "group")) throw new ArgumentException("无效的发送目标类型。");
        var session = workers.Values.Where(x => x.Robot.BotId == botId).Select(x => Volatile.Read(ref x.Session)).FirstOrDefault(x => x is not null && x.Matches(botId, secret));
        return session is null ? Task.FromResult(new SendResult(false, "offline", "机器人长连接尚未就绪，请在接收消息页面查看连接状态；刚保存配置时请稍等片刻。"))
            : session.SendAsync(targetType, targetId, content, ct);
    }
    public Task<SendResult?> UpdateQuoteStreamAsync(string botId, string secret, Guid messageId, string content, bool finish, CancellationToken ct)
    {
        var session = workers.Values.Where(x => x.Robot.BotId == botId).Select(x => Volatile.Read(ref x.Session))
            .FirstOrDefault(x => x is not null && x.Matches(botId, secret));
        return session is null ? Task.FromResult<SendResult?>(null) : session.UpdateQuoteStreamAsync(messageId, content, finish, ct);
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try { await ReconcileAsync(stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception ex) { logger.LogWarning("读取机器人配置失败（{Type}），将稍后重试。", ex.GetType().Name); }
                await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally { await Task.WhenAll(workers.Values.Select(StopWorkerAsync)); workers.Clear(); states.Clear(); }
    }
    private async Task ReconcileAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
        var robots = await db.Robots.AsNoTracking().ToListAsync(ct);
        foreach (var entry in workers.ToArray())
        {
            var current = robots.Find(x => x.Id == entry.Key);
            var old = entry.Value.Robot;
            var restart = restarts.TryRemove(entry.Key, out _);
            if (current is null || !current.Enabled || current.BotId != old.BotId || current.ProtectedSecret != old.ProtectedSecret || current.Name != old.Name || restart)
            { workers.TryRemove(entry.Key, out _); await StopWorkerAsync(entry.Value); }
        }
        foreach (var robot in robots)
        {
            if (!robot.Enabled) { states[robot.Id] = new(robot.Id, robot.Name, robot.BotId, "disabled", "已停用，不建立连接。"); continue; }
            if (workers.ContainsKey(robot.Id)) continue;
            restarts.TryRemove(robot.Id, out _);
            var worker = new Worker(robot, CancellationTokenSource.CreateLinkedTokenSource(ct));
            workers[robot.Id] = worker;
            states[robot.Id] = new(robot.Id, robot.Name, robot.BotId, "connecting", "正在建立企微连接…");
            worker.Task = RunWorkerAsync(worker);
        }
        var ids = robots.Select(x => x.Id).ToHashSet();
        foreach (var id in states.Keys) if (!ids.Contains(id)) { states.TryRemove(id, out _); restarts.TryRemove(id, out _); }
    }
    private async Task RunWorkerAsync(Worker worker)
    {
        var robot = worker.Robot; var ct = worker.Cancellation.Token;
        var attempt = 0;
        DateTimeOffset? authenticatedAt = null;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                authenticatedAt = null;
                try
                {
                    SetState(robot.Id, "connecting", "正在连接企业微信…");
                    await using var lease = await RobotConnectionLease.TryAcquireAsync(configuration.GetConnectionString("Postgres")!, robot.BotId, ct);
                    if (lease is null)
                    {
                        SetState(robot.Id, "standby", "另一个系统实例正在维护此机器人连接，本实例等待接管。");
                        await Task.Delay(TimeSpan.FromSeconds(30), ct); continue;
                    }
                    var secret = secrets.Unprotect(robot.ProtectedSecret);
                    using var connectDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    connectDeadline.CancelAfter(TimeSpan.FromSeconds(10));
                    using var socket = await sockets.ConnectAsync(connectDeadline.Token);
                    SetState(robot.Id, "authenticating", "已连接，正在认证机器人…");
                    using var session = new WeComSession(socket, robot.BotId, secret,
                        (frame, token) => StoreMessageAsync(robot, frame, token),
                        () => { authenticatedAt = DateTimeOffset.UtcNow; logger.LogInformation("机器人 {RobotId} 企微认证成功，持续接收已启动。", robot.Id); states.AddOrUpdate(robot.Id, _ => new(robot.Id, robot.Name, robot.BotId, "online", "已连接，正在接收消息。", DateTimeOffset.UtcNow), (_, old) => old with { State = "online", Description = "已连接，正在接收消息。", ConnectedAt = DateTimeOffset.UtcNow, LastHeartbeatAt = null }); },
                        () => states.AddOrUpdate(robot.Id, _ => new(robot.Id, robot.Name, robot.BotId, "online", "已连接，正在接收消息。", LastHeartbeatAt: DateTimeOffset.UtcNow), (_, old) => old with { LastHeartbeatAt = DateTimeOffset.UtcNow }),
                        lease.CheckAsync);
                    Volatile.Write(ref worker.Session, session);
                    try { await session.RunAsync(ct); }
                    finally { Volatile.Write(ref worker.Session, null); }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (ConnectionTakenOverException)
                {
                    SetState(robot.Id, "conflict", "企微通知此连接被其他客户端接管。已暂停重连，请关闭其他客户端后点击重新连接。");
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                }
                catch (AuthenticationRejectedException ex)
                {
                    SetState(robot.Id, "error", $"机器人认证失败（错误码 {ex.Code}），请检查 Bot ID、Secret、长连接模式后重新连接。");
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                }
                catch (CryptographicException)
                {
                    SetState(robot.Id, "error", "无法读取机器人加密凭据，请检查运行身份和密钥文件，或重新保存 Secret。");
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                }
                catch (Exception ex)
                {
                    if (authenticatedAt is { } since && DateTimeOffset.UtcNow - since >= TimeSpan.FromMinutes(1)) attempt = 0;
                    var delay = Math.Min(30, 1 << Math.Min(attempt++, 5));
                    SetState(robot.Id, "reconnecting", $"连接或消息存储暂时异常，{delay} 秒后自动重试。请检查服务器网络、数据库和企微配置。");
                    logger.LogWarning("机器人 {RobotId} 连接暂时异常（{Type}），{Delay} 秒后重试。", robot.Id, ex.GetType().Name, delay);
                    await Task.Delay(TimeSpan.FromSeconds(delay), ct);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }
    private async Task StoreMessageAsync(Robot robot, JsonElement frame, CancellationToken ct)
    {
        var message = IncomingMessageParser.Parse(robot.Id, robot.Name, frame);
        if (message is null) return;
        var isNew = false;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var scope = scopes.CreateScope();
                isNew = await scope.ServiceProvider.GetRequiredService<IInboxService>().StoreAsync(message, ct);
                states.AddOrUpdate(robot.Id, _ => new(robot.Id, robot.Name, robot.BotId, "online", "已连接，正在接收消息。", LastMessageAt: DateTimeOffset.UtcNow),
                    (_, old) => old with { LastMessageAt = DateTimeOffset.UtcNow });
                break;
            }
            catch (Exception) when (attempt < 2 && !ct.IsCancellationRequested) { await Task.Delay(TimeSpan.FromSeconds(attempt + 1), ct); }
        }
        if (!isNew) return;
        if (message.MessageType == "text" && DateTimeOffset.UtcNow - message.ReceivedAt < TimeSpan.FromSeconds(4))
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
                if (scope.ServiceProvider.GetService<QuoteMcpClient>()?.Configured == true
                    && await db.AutoReplies.AsNoTracking().AnyAsync(x => x.ReceivedMessageId == message.Id && x.Mode == "quote", ct))
                {
                    var session = workers.Values.Where(x => x.Robot.Id == robot.Id).Select(x => Volatile.Read(ref x.Session)).FirstOrDefault(x => x is not null);
                    if (session is not null && DateTimeOffset.UtcNow - message.ReceivedAt < TimeSpan.FromSeconds(4))
                    {
                        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        deadline.CancelAfter(TimeSpan.FromSeconds(2));
                        var result = await session.StartQuoteStreamAsync(message.Id, message.RequestId, deadline.Token);
                        if (result is { Ok: false }) logger.LogInformation("报价消息 {MessageId} 的企微流式占位未确认（{Stage}）。", message.Id, result.Stage);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            { logger.LogWarning("报价消息 {MessageId} 的流式占位失败（{Type}），保留原主动发送流程。", message.Id, ex.GetType().Name); }
        }
        try
        {
            using var scope = scopes.CreateScope();
            var automation = scope.ServiceProvider.GetService<IAutoReplyService>();
            // Quote messages are queued atomically with the inbox insert. Do not register the same task twice.
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
            var quoteQueued = await db.AutoReplies.AsNoTracking()
                .AnyAsync(x => x.ReceivedMessageId == message.Id && x.Mode == "quote", ct);
            if (automation is not null && !quoteQueued) await automation.HandleAsync(robot.Id, robot.BotId, message, ct);
        }
        catch (Exception ex) { logger.LogWarning("机器人 {RobotId} 自动回复处理异常（{Type}），收到的消息已保存，不自动重发。", robot.Id, ex.GetType().Name); }
    }
    private void SetState(Guid id, string state, string description)
    {
        if (states.TryGetValue(id, out var old)) states[id] = old with { State = state, Description = description };
    }
    private static async Task StopWorkerAsync(Worker worker)
    { await worker.Cancellation.CancelAsync(); await worker.Task; worker.Cancellation.Dispose(); }
    private sealed class Worker(Robot robot, CancellationTokenSource cancellation)
    {
        public Robot Robot { get; } = robot;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public Task Task { get; set; } = Task.CompletedTask;
        public WeComSession? Session;
    }
}

internal sealed class RobotConnectionLease(NpgsqlConnection connection) : IAsyncDisposable
{
    public static async Task<RobotConnectionLease?> TryAcquireAsync(string connectionString, string botId, CancellationToken ct)
    {
        var settings = new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false, Timeout = 5, CommandTimeout = 5, KeepAlive = 15 };
        var connection = new NpgsqlConnection(settings.ConnectionString);
        try
        {
            await connection.OpenAsync(ct);
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes("Li.AiBot.Connection." + botId));
            var key = BinaryPrimitives.ReadInt64LittleEndian(hash);
            await using var command = new NpgsqlCommand("SELECT pg_try_advisory_lock(@key)", connection);
            command.Parameters.AddWithValue("key", key);
            if (await command.ExecuteScalarAsync(ct) is true) return new(connection);
            await connection.DisposeAsync(); return null;
        }
        catch { await connection.DisposeAsync(); throw; }
    }
    public async Task CheckAsync(CancellationToken ct)
    { await using var command = new NpgsqlCommand("SELECT 1", connection); await command.ExecuteScalarAsync(ct); }
    public ValueTask DisposeAsync() => connection.DisposeAsync();
}
