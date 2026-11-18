using AiBot.Application;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AiBot.Web.Controllers;

[Authorize]
public sealed class AiModelsController(IAutomationSettingsService settings) : Controller
{
    public async Task<IActionResult> Index(CancellationToken ct) => View(await settings.ModelsAsync(ct));
    [HttpGet]
    public async Task<IActionResult> Edit(Guid? id, CancellationToken ct)
    { var input = id is { } key ? await settings.FindModelAsync(key, ct) : new AiModelInput(); return input is null ? NotFound() : View(input); }
    [HttpPost]
    public async Task<IActionResult> Edit(AiModelInput input, CancellationToken ct)
    {
        if (!ModelState.IsValid) return View(input);
        try { await settings.SaveModelAsync(input, ct); }
        catch (ArgumentException ex) { ModelState.AddModelError("", ex.Message); return View(input); }
        TempData["Success"] = "模型接口已保存。保存配置不会调用模型。"; return RedirectToAction(nameof(Index));
    }
    [HttpPost]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        try { await settings.DeleteModelAsync(id, ct); TempData["Success"] = "模型接口已删除。"; }
        catch (ArgumentException ex) { TempData["Error"] = ex.Message; }
        return RedirectToAction(nameof(Index));
    }
}
