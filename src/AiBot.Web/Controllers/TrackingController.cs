using AiBot.Application;
using AiBot.Domain;
using AiBot.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AiBot.Web.Controllers;

[Authorize]
public sealed class TrackingController(BotDbContext db, TrackingHistory history, TrackingBackendClient backend) : Controller
{
    private const int PageSize = 30;

    public async Task<IActionResult> Index(string? awb, int page, CancellationToken ct)
    {
        awb = awb?.Trim();
        var query = db.TrackingSubscriptions.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(awb)) query = query.Where(x => x.Awb == awb);
        var result = await ReadPageAsync(query.OrderByDescending(x => x.UpdatedAt).ThenBy(x => x.Id), page, ct);
        var awbs = result.Items.Select(x => x.Awb).Distinct().ToArray();
        var refreshes = await db.TrackingRefreshes.AsNoTracking()
            .Where(x => awbs.Contains(x.Awb)).ToDictionaryAsync(x => x.Awb, ct);
        return View(new TrackingSubscriptionPage(result, refreshes, awb, backend.Configured && backend.CanQuery));
    }

    [HttpGet]
    public async Task<IActionResult> Nodes(string awb, DateOnly? from, DateOnly? to, int page, bool cached, CancellationToken ct)
    {
        if (!KnownFormat(awb) || !await KnownAwbAsync(awb, ct)) return NotFound();
        var canRefresh = backend.Configured && backend.CanQuery;
        if (!cached)
        {
            if (!canRefresh)
                ViewBag.SyncNotice = "查询接口尚未配置，显示已保存的推送记录。";
            else if (!await history.RefreshAsync([awb], ct))
                ViewBag.SyncNotice = "本次查询失败，显示已保存的推送记录。";
        }

        var query = db.TrackingPushes.AsNoTracking().Where(x => x.Awb == awb);
        if (from is { } earliest)
        {
            var start = $"{earliest:yyyy-MM-dd} 00:00:00";
            query = query.Where(x => string.Compare(x.PushTime, start) >= 0);
        }
        if (to is { } latest)
        {
            var end = $"{latest:yyyy-MM-dd} 23:59:59";
            query = query.Where(x => string.Compare(x.PushTime, end) <= 0);
        }

        ViewBag.Awb = awb;
        ViewBag.From = from?.ToString("yyyy-MM-dd");
        ViewBag.To = to?.ToString("yyyy-MM-dd");
        ViewBag.Refresh = await db.TrackingRefreshes.AsNoTracking().SingleOrDefaultAsync(x => x.Awb == awb, ct);
        ViewBag.CanRefresh = canRefresh;
        var result = await ReadPageAsync(query.OrderByDescending(x => x.PushTime).ThenBy(x => x.Node), page, ct);
        return View(result);
    }

    [HttpPost]
    public async Task<IActionResult> Refresh(string awb, CancellationToken ct)
    {
        if (!KnownFormat(awb) || !await KnownAwbAsync(awb, ct)) return NotFound();
        if (!backend.Configured || !backend.CanQuery)
            TempData["Error"] = "轨迹查询接口尚未完成配置。";
        else if (await history.RefreshAsync([awb], ct))
            TempData["Success"] = "主单推送记录已刷新。";
        else
            TempData["Error"] = "刷新失败，请查看最近查询结果。";
        return RedirectToAction(nameof(Nodes), new { awb, cached = true });
    }

    [HttpGet]
    public async Task<IActionResult> Operations(int page, CancellationToken ct)
    {
        var query = from command in db.TrackingCommands.AsNoTracking()
            join message in db.ReceivedMessages.AsNoTracking() on command.Id equals message.Id
            where command.Status != "skipped"
            orderby command.CreatedAt descending, command.Id
            select new TrackingOperation(command, message.Content, message.RobotName);
        return View(await ReadPageAsync(query, page, ct));
    }

    private async Task<bool> KnownAwbAsync(string awb, CancellationToken ct) =>
        await db.TrackingSubscriptions.AnyAsync(x => x.Awb == awb, ct)
        || await db.TrackingPushes.AnyAsync(x => x.Awb == awb, ct);

    private static bool KnownFormat(string? awb) => awb is not null && TrackingIntent.IsAwb(awb);

    private static async Task<PageResult<T>> ReadPageAsync<T>(IQueryable<T> query, int page, CancellationToken ct)
    {
        var total = await query.CountAsync(ct);
        page = Math.Clamp(page, 1, Math.Max(1, (total + PageSize - 1) / PageSize));
        return new(await query.Skip((page - 1) * PageSize).Take(PageSize).ToListAsync(ct), page, PageSize, total);
    }
}

public sealed record TrackingSubscriptionPage(PageResult<TrackingSubscription> Subscriptions,
    IReadOnlyDictionary<string, TrackingRefresh> Refreshes, string? Awb, bool CanRefresh);

public sealed record TrackingOperation(TrackingCommand Command, string Text, string RobotName);
