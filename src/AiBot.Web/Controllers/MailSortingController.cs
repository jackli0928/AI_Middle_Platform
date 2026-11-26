using AiBot.Application;
using AiBot.Domain;
using AiBot.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace AiBot.Web.Controllers;

[Authorize]
public sealed class MailSortingController(IMailSorting sorting, IMailboxCredentials credentials,
    IMailboxSettings mailboxSettings, IOptions<MailSortingOptions> options, DatabaseState database, IMailRuleSettings rules, IAutomationSettingsService automation) : Controller
{
    [HttpGet]
    public async Task<IActionResult> Rules(CancellationToken ct)
    {
        if (!database.Ready) return StatusCode(503, "数据库尚未就绪。");
        var current = await rules.ReadAsync(ct);
        ViewData["RuleVersion"] = current.Snapshot.Version;
        var editor = MailRuleEditor.From(current);
        editor.Models = await AvailableModelsAsync(ct);
        return View(editor);
    }

    [HttpPost]
    public async Task<IActionResult> Rules(MailRuleEditor editor, CancellationToken ct)
    {
        if (!database.Ready) return StatusCode(503, "数据库尚未就绪。");
        var current = await rules.ReadAsync(ct);
        ViewData["RuleVersion"] = current.Snapshot.Version;
        editor.Models = await AvailableModelsAsync(ct);
        if (editor.RecognitionModelId is { } modelId && editor.Models.All(x => x.Id != modelId))
            ModelState.AddModelError(nameof(editor.RecognitionModelId), "请选择已启用且已配置密钥的 AI 模型接口。");
        var errors = new List<string>();
        var snapshot = editor.CreateSnapshot(current.Snapshot.Version + 1, errors);
        foreach (var error in errors) ModelState.AddModelError("", error);
        if (!ModelState.IsValid) return View(editor);
        if (editor.Revision != current.Revision || !await rules.SaveAsync(snapshot, editor.Revision, ct))
        {
            ModelState.AddModelError("", "规则已被其他操作更新。请重新载入规则后提交。");
            return View(editor);
        }
        TempData["Success"] = "邮件识别规则已保存，并用于后续识别。";
        return RedirectToAction(nameof(Rules));
    }

    private async Task<IReadOnlyList<AiModelInfo>> AvailableModelsAsync(CancellationToken ct) =>
        (await automation.ModelsAsync(ct)).Where(x => x.Enabled && x.HasApiKey).ToArray();

    private string Actor => User.Identity?.Name ?? "管理员";

    [HttpGet]
    public async Task<IActionResult> Index(string? search, string? category, string? status, int page = 1, CancellationToken ct = default)
    {
        if (!database.Ready) return StatusCode(503, "数据库尚未就绪。");
        search = (search ?? "").Trim();
        if (search.Length > 200) search = search[..200];
        category = MailSorting.Categories.ContainsKey(category ?? "") ? category : "";
        status = status is "true" or "false" ? status : "";
        page = Math.Clamp(page, 1, 10000);
        var rows = await sorting.ListAsync(search, category!, status, page, ct);
        return View(new MailSortingList(rows.Take(MailSorting.PageSize).ToArray(), search, category!, status, page,
            rows.Count > MailSorting.PageSize && page < 10000));
    }

    [HttpGet]
    public async Task<IActionResult> Details(Guid id, CancellationToken ct)
    {
        if (!database.Ready) return StatusCode(503, "数据库尚未就绪。");
        if (await sorting.FindAsync(id, ct) is not { } mail) return NotFound();
        var modelId = (await rules.ReadAsync(ct)).Snapshot.RecognitionModelId;
        var model = modelId is null ? null : (await AvailableModelsAsync(ct)).FirstOrDefault(x => x.Id == modelId);
        return View(new MailSortingDetails(mail, await sorting.AuditAsync(id, ct), model?.Name));
    }

    [HttpPost, RequestSizeLimit(MailSorting.MaxMailBytes + 65536)]
    [RequestFormLimits(MultipartBodyLengthLimit = MailSorting.MaxMailBytes)]
    public async Task<IActionResult> Import(IFormFile? file, CancellationToken ct)
    {
        if (!database.Ready) return StatusCode(503, "数据库尚未就绪。");
        if (file is null || file.Length is 0 or > MailSorting.MaxMailBytes ||
            !Path.GetExtension(file.FileName).Equals(".eml", StringComparison.OrdinalIgnoreCase))
        {
            TempData["Error"] = "请选择 12 MB 以内的 .eml 文件。";
            return RedirectToAction(nameof(Index));
        }
        try
        {
            await using var stream = file.OpenReadStream();
            using var bytes = new MemoryStream((int)file.Length);
            await stream.CopyToAsync(bytes, ct);
            var id = await sorting.ImportAsync(bytes.ToArray(), "手动导入", Actor, null, ct);
            return RedirectToAction(nameof(Details), new { id });
        }
        catch (InvalidMailException ex)
        {
            TempData["Error"] = ex.Message;
            return RedirectToAction(nameof(Index));
        }
    }

    [HttpPost]
    public async Task<IActionResult> Save(Guid id, int revision, string? category, Dictionary<string, string>? fields,
        bool confirmed, CancellationToken ct)
    {
        if (!database.Ready) return StatusCode(503, "数据库尚未就绪。");
        if (await sorting.FindAsync(id, ct) is not { } mail) return NotFound();
        if (mail.Revision != revision) return Changed(id);
        if (!ModelState.IsValid || !MailSorting.Categories.ContainsKey(category ?? ""))
            return ReviewError(id, "请选择有效分类。");
        var edits = new Dictionary<string, string>();
        foreach (var field in mail.Analysis.Fields)
        {
            if (field.Key == "service" || fields is null || !fields.TryGetValue(field.Key, out var submitted)) continue;
            var value = submitted?.Trim();
            if (string.IsNullOrEmpty(value)) continue;
            if (value.Length > 1500) return ReviewError(id, $"“{field.Label}”最多填写 1500 个字。");
            edits.Add(field.Key, value);
        }
        if (confirmed && (category == "review" || mail.Analysis.Fields.Any(f =>
            f.HasConflict && !edits.ContainsKey(f.Key))))
            return ReviewError(id, "确认前请选择业务分类，并填写有冲突字段的确认值。");
        if (!await sorting.ReviewAsync(mail, new(category!, edits, confirmed), Actor, ct)) return Changed(id);
        TempData["Success"] = confirmed ? "复核已确认。" : "复核已保存。";
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost]
    public async Task<IActionResult> Reanalyze(Guid id, int revision, CancellationToken ct, bool useConfiguredModel = false)
    {
        if (!database.Ready) return StatusCode(503, "数据库尚未就绪。");
        if (await sorting.FindAsync(id, ct) is not { } mail) return NotFound();
        try
        {
            if (mail.Revision != revision || !await sorting.ReanalyzeAsync(mail, Actor, ct, useConfiguredModel)) return Changed(id);
        }
        catch (InvalidMailException ex) { return ReviewError(id, ex.Message); }
        TempData["Success"] = useConfiguredModel ? "识别已更新，请查看识别引擎和字段来源。" : "识别已更新，人工录入已保留。";
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpGet]
    public async Task<IActionResult> Original(Guid id, CancellationToken ct)
    {
        if (!database.Ready) return StatusCode(503, "数据库尚未就绪。");
        return await sorting.OriginalAsync(id, ct) is { } raw
            ? File(raw, "application/octet-stream", $"email-{id:N}.eml") : NotFound();
    }

    [HttpGet]
    public async Task<IActionResult> Attachment(Guid id, int index, CancellationToken ct)
    {
        if (!database.Ready) return StatusCode(503, "数据库尚未就绪。");
        return await sorting.AttachmentAsync(id, index, ct) is { } part
            ? File(part.Data, "application/octet-stream", part.Name) : NotFound();
    }

    [HttpGet]
    public async Task<IActionResult> Mailboxes(CancellationToken ct)
    {
        if (!database.Ready) return StatusCode(503, "数据库尚未就绪。");
        var configured = await mailboxSettings.ListAsync(ct);
        var managed = configured.Select(x => SortingMailboxIdentity.Key(x.ToMailbox())).ToHashSet();
        var items = new List<MailSortingMailbox>();
        var accounts = options.Value.Mailboxes;
        for (var i = 0; i < accounts.Count; i++)
        {
            var account = accounts[i];
            if (managed.Contains(SortingMailboxIdentity.Key(account))) continue;
            items.Add(new(i, account.Name, account.Host, account.Username, account.Protocol, account.Enabled,
                await credentials.HasPasswordAsync(SortingMailboxIdentity.Key(account), ct)));
        }
        return View(new MailSortingMailboxes(configured, items));
    }

    [HttpPost]
    public async Task<IActionResult> SaveMailboxPassword(int mailbox, string? password, CancellationToken ct)
    {
        if (!database.Ready) return StatusCode(503, "数据库尚未就绪。");
        var accounts = options.Value.Mailboxes;
        if (mailbox < 0 || mailbox >= accounts.Count) return NotFound();
        if (string.IsNullOrWhiteSpace(password) || password.Length > 4096)
            TempData["Error"] = "请输入有效的邮箱授权码。";
        else
        {
            await credentials.SavePasswordAsync(SortingMailboxIdentity.Key(accounts[mailbox]), password, ct);
            TempData["Success"] = "邮箱授权码已保存。";
        }
        return RedirectToAction(nameof(Mailboxes));
    }

    [HttpGet]
    public async Task<IActionResult> EditMailbox(Guid? id, CancellationToken ct)
    {
        if (!database.Ready) return StatusCode(503, "数据库尚未就绪。");
        if (id is null) return View(new MailboxSettings());
        var item = (await mailboxSettings.ListAsync(ct)).FirstOrDefault(x => x.Id == id);
        return item is null ? NotFound() : View(item);
    }

    [HttpPost]
    public async Task<IActionResult> EditMailbox(MailboxSettings input, CancellationToken ct)
    {
        if (!database.Ready) return StatusCode(503, "数据库尚未就绪。");
        if (Uri.CheckHostName((input.Host ?? "").Trim()) == UriHostNameType.Unknown)
            ModelState.AddModelError(nameof(input.Host), "请填写邮箱服务器主机名或 IP，不要填写 URL。");
        if (input.Protocol == "imap" && string.IsNullOrWhiteSpace(input.Folder))
            ModelState.AddModelError(nameof(input.Folder), "请填写 IMAP 文件夹，例如 INBOX。");
        if (!string.IsNullOrEmpty(input.Category) &&
            (!MailSorting.Categories.ContainsKey(input.Category) || input.Category == "review"))
            ModelState.AddModelError(nameof(input.Category), "请选择有效分类。");
        if (ModelState.IsValid)
        {
            try
            {
                await mailboxSettings.SaveAsync(input, ct);
                TempData["Success"] = "收件邮箱已保存，下一轮检查将使用新配置。";
                return RedirectToAction(nameof(Mailboxes));
            }
            catch (InvalidMailException ex) { ModelState.AddModelError("", ex.Message); }
        }
        input.Password = null;
        return View(input);
    }

    private IActionResult Changed(Guid id) => ReviewError(id, "待办已在其他页面更新，请核对当前内容后重新提交。");

    private IActionResult ReviewError(Guid id, string message)
    {
        TempData["Error"] = message;
        return RedirectToAction(nameof(Details), new { id });
    }
}

public sealed record MailSortingList(IReadOnlyList<MailListItem> Rows, string Search, string Category, string Status,
    int Page, bool HasNext);
public sealed record MailSortingDetails(SortedMail Mail, IReadOnlyList<MailAudit> Audit, string? RecognitionModelName);
public sealed record MailSortingMailbox(int Index, string Name, string Host, string Username, string Protocol,
    bool Enabled, bool HasPassword);

public sealed record MailSortingMailboxes(IReadOnlyList<MailboxSettings> Managed, IReadOnlyList<MailSortingMailbox> Deployment);
