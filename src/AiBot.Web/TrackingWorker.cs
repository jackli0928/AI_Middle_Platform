using AiBot.Application;
using AiBot.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AiBot.Web;

public sealed class TrackingWorker(
    IServiceScopeFactory scopes, WeComGateway gateway, DatabaseState state, ILogger<TrackingWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (await timer.WaitForNextTickAsync(ct))
        {
            if (!state.Ready) continue;
            try { await ProcessQueueAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) { logger.LogWarning("轨迹任务队列暂时不可用（{Type}）。", ex.GetType().Name); }
        }
    }

    private async Task TryReplyFailureAsync(IServiceProvider services, BotDbContext db, Guid id, string content, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(12));
            var message = await db.ReceivedMessages.AsNoTracking().SingleAsync(x => x.Id == id, timeout.Token);
            await services.GetRequiredService<IRobotService>().ReplyAsync(message, content, "轨迹业务", timeout.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogInformation("轨迹任务 {Id} 的异常提示未完成（{Type}）。", id, ex.GetType().Name);
        }
    }

    private async Task ProcessQueueAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
        var now = DateTimeOffset.UtcNow;
        var stale = now.AddMinutes(-10);
        await db.TrackingCommands.Where(x => x.Status == "processing" && x.StartedAt < stale)
            .ExecuteUpdateAsync(update => update.SetProperty(x => x.Status, "unknown")
                .SetProperty(x => x.Result, "任务意外中断，执行结果待核实。").SetProperty(x => x.CompletedAt, now), ct);
        await db.TrackingCommands.Where(x => x.Status == "queued"
                && !db.Robots.Any(r => r.Id == x.RobotId && r.Enabled && r.TrackingModelId != null))
            .ExecuteUpdateAsync(update => update.SetProperty(x => x.Status, "skipped")
                .SetProperty(x => x.Result, "机器人已停用、已删除或已关闭轨迹能力。").SetProperty(x => x.CompletedAt, now), ct);

        var online = gateway.GetStates().Where(x => x.State == "online").Select(x => x.RobotId).ToArray();
        if (online.Length == 0) return;
        var candidates = await db.TrackingCommands.AsNoTracking().Where(x => x.Status == "queued" && online.Contains(x.RobotId))
            .OrderBy(x => x.CreatedAt).ThenBy(x => x.Id).Select(x => x.Id).Take(12).ToArrayAsync(ct);

        foreach (var id in candidates)
        {
            var started = DateTimeOffset.UtcNow;
            var claimed = await db.TrackingCommands.Where(x => x.Id == id && x.Status == "queued")
                .ExecuteUpdateAsync(update => update.SetProperty(x => x.Status, "processing").SetProperty(x => x.StartedAt, started), ct);
            if (claimed == 0) continue;
            using var processingScope = scopes.CreateScope();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromMinutes(3));
            try
            {
                await processingScope.ServiceProvider.GetRequiredService<TrackingCommandProcessor>().ProcessAsync(id, deadline.Token);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                logger.LogWarning("轨迹任务 {Id} 处理异常（{Type}）。", id, ex.GetType().Name);
                const string message = "任务处理异常，执行结果待核实。";
                var changed = await db.TrackingCommands.Where(x => x.Id == id && x.Status == "processing")
                    .ExecuteUpdateAsync(update => update.SetProperty(x => x.Status, "unknown")
                        .SetProperty(x => x.Result, message).SetProperty(x => x.CompletedAt, DateTimeOffset.UtcNow), ct);
                if (changed > 0)
                    await TryReplyFailureAsync(scope.ServiceProvider, db, id, message, ct);
            }
        }
    }
}
