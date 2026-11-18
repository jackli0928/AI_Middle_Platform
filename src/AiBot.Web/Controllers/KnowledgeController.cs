using AiBot.Application;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AiBot.Web.Controllers;

[Authorize]
public sealed class KnowledgeController(IAutomationSettingsService settings, IRobotService robots) : Controller
{
    public async Task<IActionResult> Index(string? search, CancellationToken ct)
    { ViewBag.Search = search; ViewBag.Robots = await robots.ListAsync(null, ct); return View(await settings.KnowledgeAsync(search, ct)); }
    [HttpGet]
    public async Task<IActionResult> Edit(Guid? id, CancellationToken ct)
    {
        var input = id is { } key ? await settings.FindKnowledgeAsync(key, ct) : new KnowledgeInput();
        if (input is null) return NotFound();
        return await EditView(input, ct);
    }
    private async Task<IActionResult> EditView(KnowledgeInput input, CancellationToken ct)
    { ViewBag.Robots = await robots.ListAsync(null, ct); return View("Edit", input); }
    [HttpPost]
    public async Task<IActionResult> Edit(KnowledgeInput input, CancellationToken ct)
    {
        if (!ModelState.IsValid) return await EditView(input, ct);
        try { await settings.SaveKnowledgeAsync(input, ct); }
        catch (ArgumentException ex) { ModelState.AddModelError("", ex.Message); return await EditView(input, ct); }
        TempData["Success"] = "知识条目已保存。"; return RedirectToAction(nameof(Index));
    }
    [HttpPost]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        try { await settings.DeleteKnowledgeAsync(id, ct); TempData["Success"] = "知识条目已删除。"; }
        catch (ArgumentException ex) { TempData["Error"] = ex.Message; }
        return RedirectToAction(nameof(Index));
    }
}
