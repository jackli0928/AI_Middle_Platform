using AiBot.Domain;
using Microsoft.EntityFrameworkCore;

namespace AiBot.Infrastructure;

public sealed class TrackingHistory(BotDbContext db, TrackingBackendClient backend)
{
    public async Task<bool> RefreshAsync(string[] awbs, CancellationToken ct)
    {
        var queriedAt = DateTimeOffset.UtcNow;
        IReadOnlyList<TrackingPush> records;
        try
        {
            records = await backend.QueryAsync(awbs, ct);
        }
        catch (TrackingBackendException ex)
        {
            foreach (var awb in awbs) await RecordRefreshAsync(awb, queriedAt, ex.Message, ct);
            return false;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await SavePushesAsync(records, ct);
        foreach (var awb in awbs)
            await RecordRefreshAsync(awb, queriedAt, $"查询成功，返回 {records.Count(x => x.Awb == awb)} 条节点。", ct);
        await transaction.CommitAsync(ct);
        return true;
    }

    public async Task SavePushesAsync(IEnumerable<TrackingPush> records, CancellationToken ct)
    {
        foreach (var item in records.OrderBy(x => x.Awb, StringComparer.Ordinal)
            .ThenBy(x => x.PushTime, StringComparer.Ordinal).ThenBy(x => x.Node, StringComparer.Ordinal))
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO ai_bot.tracking_pushes ("Awb", "Node", "PushTime", "QueryAddress", "DetectedAt")
                VALUES ({item.Awb}, {item.Node}, {item.PushTime}, {item.QueryAddress}, {item.DetectedAt})
                ON CONFLICT ("Awb", "PushTime", "Node") DO UPDATE SET "QueryAddress" = EXCLUDED."QueryAddress"
                    WHERE tracking_pushes."QueryAddress" IS NULL AND EXCLUDED."QueryAddress" IS NOT NULL
                """, ct);
        }
    }

    private Task RecordRefreshAsync(string awb, DateTimeOffset time, string result, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO ai_bot.tracking_refreshes ("Awb", "QueriedAt", "Result") VALUES ({awb}, {time}, {result})
            ON CONFLICT ("Awb") DO UPDATE SET "QueriedAt" = EXCLUDED."QueriedAt", "Result" = EXCLUDED."Result"
                WHERE tracking_refreshes."QueriedAt" <= EXCLUDED."QueriedAt"
            """, ct);
}
