using AiBot.Application;
using AiBot.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AiBot.Web.Controllers;

[Authorize]
public sealed class InboxController(IInboxService inbox, IRobotService robots, IConnectionMonitor connections, IAutomationSettingsService settings) : Controller
{
    [HttpGet]
    public async Task<IActionResult> Index([FromQuery] InboxFilter filter, CancellationToken ct)
    {
        var messages = await inbox.ListAsync(filter, ct);
        return View(new InboxViewModel(new(filter, messages), await robots.ListAsync(null, ct), connections.GetStates()));
    }
    [HttpGet]
    public async Task<IActionResult> Rows([FromQuery] InboxFilter filter, CancellationToken ct)
    {
        Response.Headers["X-Inbox-Partial"] = "1";
        return PartialView("_ReceivedRows", new InboxListModel(filter, await inbox.ListAsync(filter, ct)));
    }
    [HttpGet]
    public async Task<IActionResult> Details(Guid id, CancellationToken ct)
    { var message = await inbox.FindAsync(id, ct); if (message is null) return NotFound(); ViewBag.AutoReply = await settings.ReplyForMessageAsync(id, ct); return View(message); }
    [HttpGet] public IActionResult Connections() => Json(connections.GetStates());
    [HttpPost]
    public async Task<IActionResult> Reconnect(Guid id, CancellationToken ct)
    {
        var robot = await robots.FindAsync(id, ct);
        if (robot is null) return NotFound();
        if (!robot.Enabled) TempData["Error"] = "请先在机器人配置中启用该机器人。";
        else { connections.Reconnect(id); TempData["Success"] = "已请求重新连接，将在几秒内更新连接状态。"; }
        return RedirectToAction(nameof(Index));
    }
}
