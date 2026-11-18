using AiBot.Application;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AiBot.Web.Controllers;

[Authorize]
public sealed class MessagesController(IRobotService service) : Controller
{
    public async Task<IActionResult> Index(int page = 1, string? search = null, CancellationToken ct = default)
    { ViewBag.Search = search; return View(await service.LogsAsync(page, search, ct)); }
}
