using AiBot.Application;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AiBot.Web.Controllers;

[Authorize]
public sealed class HomeController(IRobotService robots) : Controller
{
    public async Task<IActionResult> Index(CancellationToken ct) => View(await robots.DashboardAsync(ct));
    [AllowAnonymous, IgnoreAntiforgeryToken]
    public IActionResult Error() { Response.StatusCode = 500; return View(); }
}
