using AiBot.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AiBot.Web;

public sealed class TrackingPollWorker(
    IServiceScopeFactory scopes, DatabaseState state, IOptions<TrackingOptions> options, ILogger<TrackingPollWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        while (await timer.WaitForNextTickAsync(ct))
        {
            if (!state.Ready) continue;
            try { await PollAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) { logger.LogWarning("轨迹推送记录轮询异常（{Type}）。", ex.GetType().Name); }
        }
    }

    private async Task PollAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var backend = scope.ServiceProvider.GetRequiredService<TrackingBackendClient>();
        if (!backend.Configured || !backend.CanQuery) return;

        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
        var cutoff = DateTimeOffset.UtcNow.AddSeconds(-Math.Clamp(options.Value.PollSeconds, 30, 86400));
        var active = db.TrackingSubscriptions.Where(x => x.Status == "active" || x.Status == "cancel_unknown")
            .Select(x => x.Awb).Distinct();
        var due = await (from awb in active
            join refresh in db.TrackingRefreshes on awb equals refresh.Awb into refreshes
            from refresh in refreshes.DefaultIfEmpty()
            where refresh == null || refresh.QueriedAt <= cutoff
            orderby (DateTimeOffset?)refresh.QueriedAt ?? DateTimeOffset.MinValue, awb
            select awb).Take(60).ToArrayAsync(ct);

        var history = scope.ServiceProvider.GetRequiredService<TrackingHistory>();
        foreach (var batch in due.Chunk(20))
            await history.RefreshAsync(batch, ct);
    }
}
