using AiBot.Application;
using AiBot.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AiBot.Web.Controllers;

[Authorize]
public sealed class RobotsController(IRobotService service, IAutomationSettingsService settings) : Controller
{
    public async Task<IActionResult> Index(string? search, CancellationToken ct)
    { ViewBag.Search = search; return View(await service.ListAsync(search, ct)); }
    [HttpGet]
    public async Task<IActionResult> Edit(Guid? id, CancellationToken ct)
    {
        if (id is null) return await EditView(new RobotInput(), ct);
        var robot = await service.FindAsync(id.Value, ct);
        return robot is null ? NotFound() : await EditView(new RobotInput { Id = robot.Id, Name = robot.Name, BotId = robot.BotId, Description = robot.Description, Enabled = robot.Enabled, AutoReplyEnabled = robot.AutoReplyEnabled, ReplyMode = robot.ReplyMode, ReplyToGroups = robot.ReplyToGroups, ReplyPrompt = robot.ReplyPrompt, ModelPriority = robot.ModelPriority.ToList() }, ct);
    }
    private async Task<IActionResult> EditView(RobotInput input, CancellationToken ct)
    { ViewBag.AiModels = await settings.ModelsAsync(ct); return View("Edit", input); }
    [HttpPost]
    public async Task<IActionResult> Edit(RobotInput input, CancellationToken ct)
    {
        if (!ModelState.IsValid) return await EditView(input, ct);
        try { await service.SaveAsync(input, ct); }
        catch (ArgumentException ex) { ModelState.AddModelError("", ex.Message); return await EditView(input, ct); }
        TempData["Success"] = input.Id is null ? "机器人已添加。" : "机器人配置已更新。";
        return RedirectToAction(nameof(Index));
    }
    [HttpPost]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        try { await service.DeleteAsync(id, ct); TempData["Success"] = "机器人已删除，历史消息记录已保留。"; }
        catch (ArgumentException ex) { TempData["Error"] = ex.Message; }
        return RedirectToAction(nameof(Index));
    }
    [HttpGet]
    public async Task<IActionResult> Send(Guid? id, string? targetType, string? targetId, CancellationToken ct)
    {
        var robots = await service.ListAsync(null, ct);
        var group = targetType == "group";
        var target = targetId?.Length <= 256 ? targetId : null;
        return View(new SendViewModel(new SendInput { RobotId = id ?? Guid.Empty, TargetType = group ? "group" : "single", UserId = group ? null : target, ChatId = group ? target : null }, robots));
    }
    [HttpPost, RequestSizeLimit(32768)]
    public async Task<IActionResult> Send([Bind(Prefix = "Input")] SendInput input, CancellationToken ct)
    {
        if (!ModelState.IsValid) return View(new SendViewModel(input, await service.ListAsync(null, ct)));
        try
        {
            var result = await service.SendAsync(input, User.Identity!.Name!, ct);
            TempData[result.Ok ? "Success" : "Error"] = result.Message + (result.Errcode is { } code ? $"（错误码：{code}）" : "");
            return RedirectToAction("Index", "Messages");
        }
        catch (ArgumentException ex) { ModelState.AddModelError("", ex.Message); return View(new SendViewModel(input, await service.ListAsync(null, ct))); }
    }
}
