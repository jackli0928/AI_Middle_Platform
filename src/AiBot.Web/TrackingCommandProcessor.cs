using System.Text.Json;
using System.Text;
using System.Text.RegularExpressions;
using AiBot.Application;
using AiBot.Domain;
using AiBot.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AiBot.Web;

public sealed class TrackingCommandProcessor(
    BotDbContext db, TrackingBackendClient backend, TrackingHistory history,
    IAiCompletionClient ai, IAutoReplyService autoReply, IRobotService robots,
    IWeComGateway gateway, ISecretProtector secrets, ILogger<TrackingCommandProcessor> logger)
{
    private const string Help = "可以说「帮我订阅 545-98910626 的轨迹，发到 jack@example.com」、"
        + "「这票发生异常时也发邮件」或「取消这票的轨迹订阅」。也可以问「这票推送过哪些节点？」";

    public async Task ProcessAsync(Guid id, CancellationToken ct)
    {
        var command = await db.TrackingCommands.SingleAsync(x => x.Id == id, ct);
        var robot = await db.Robots.AsNoTracking().SingleOrDefaultAsync(x => x.Id == command.RobotId, ct);
        var message = await db.ReceivedMessages.AsNoTracking().SingleAsync(x => x.Id == id, ct);
        if (robot is not { Enabled: true, TrackingModelId: not null } || message.SenderUserId == robot.BotId)
        {
            await CompleteAsync(command, "skipped", "机器人配置已变更。", ct);
            return;
        }

        var model = await db.AiModels.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == robot.TrackingModelId && x.Enabled && x.ProtectedApiKey != "", ct);
        if (model is null)
        {
            await ReplyAsync(command, message, "failed", "轨迹意图模型未启用或缺少凭证，请联系管理员。", ct);
            return;
        }

        var previous = await GetContextAsync(command, ct);
        TrackingIntent intent;
        try
        {
            var input = message.Content.Normalize(NormalizationForm.FormKC);
            if (input.Length > 4000)
            {
                await ReplyAsync(command, message, "clarify", "请把轨迹操作简化为主单号、邮箱和要执行的指令。", ct);
                return;
            }
            var modelInput = JsonSerializer.Serialize(new { current = input, context = previous });
            intent = TrackingIntent.Parse(await ai.CompleteAsync(model, TrackingIntent.Prompt, modelInput, ct));
            intent = ValidateSources(intent, input, previous);
        }
        catch (Exception ex) when (ex is JsonException or AiCompletionException or KeyNotFoundException
            or InvalidOperationException or ArgumentException or System.Security.Cryptography.CryptographicException || ex is OperationCanceledException && !ct.IsCancellationRequested)
        {
            await ReplyAsync(command, message, "clarify", "这次没有识别出可靠的指令。请明确说明主单号、要执行的操作和邮箱。", ct);
            return;
        }

        if (intent.Action == "other")
        {
            await FallBackAsync(command, robot, message, ct);
            return;
        }

        command.Intent = JsonSerializer.Serialize(intent);
        var (status, result) = await ExecuteAsync(command, intent, robot, message, ct);
        await ReplyAsync(command, message, status, result, ct);
    }

    private async Task<(string Status, string Result)> ExecuteAsync(
        TrackingCommand command, TrackingIntent intent, Robot robot, ReceivedMessage message, CancellationToken ct)
    {
        if (intent.Action == "help") return ("help", Help);
        if (intent.Action == "unclear") return ("clarify", "你想订阅、取消，还是查看推送记录？");
        if (intent.Awbs.Length == 0) return ("clarify", "请补充主单号，例如 545-98910626。");
        var routes = intent.Routes ?? [];
        if (routes.Length == 0 && intent.Emails.Length > 1 && (intent.Awbs.Length > 1 || intent.Types.Length > 1))
            return ("clarify", "请说明每个邮箱对应哪些主单和订阅类型。");

        var owned = await db.TrackingSubscriptions.AsNoTracking()
            .Where(x => x.RobotId == command.RobotId && x.SenderUserId == command.SenderUserId && intent.Awbs.Contains(x.Awb))
            .ToListAsync(ct);
        if (intent.Action == "history")
            return await ReadHistoryAsync(intent, owned, ct);

        var cancel = intent.Action == "cancel";
        if (!backend.Configured) return ("failed", "轨迹后台接口尚未配置，请联系管理员。");
        if (!cancel && intent.Types.Contains(TrackingIntent.All)) return ("clarify", "请说明是轨迹跟踪、异常监控，还是两项都开启。");

        List<(string Awb, string Email, string Type)> targets = [];
        if (routes.Length > 0)
        {
            foreach (var route in routes)
                foreach (var awb in route.Awbs.Length == 0 ? intent.Awbs : route.Awbs)
                    targets.Add((awb, route.Email, route.Type));

            if (intent.Awbs.Any(awb => !targets.Any(x => x.Awb == awb)) ||
                intent.Emails.Any(email => !targets.Any(x => x.Email == email)) ||
                intent.Types.Any(type => !targets.Any(x => x.Type == type)))
                return ("clarify", "还有主单、类型或邮箱的对应关系未明确，请确认要执行的订阅。");
        }
        foreach (var awb in routes.Length == 0 ? intent.Awbs : [])
        {
            if (!cancel)
            {
                if (intent.Emails.Length == 0) return ("clarify", "请补充收件邮箱。");
                if (intent.Types.Length == 0) return ("clarify", "请说明要订阅轨迹跟踪还是异常监控。");
                foreach (var email in intent.Emails)
                    foreach (var type in intent.Types) targets.Add((awb, email, type));
                continue;
            }

            var matching = owned.Where(x => x.Awb == awb && x.Status != "cancelled"
                && (intent.Emails.Length == 0 || intent.Emails.Contains(x.Email, StringComparer.OrdinalIgnoreCase))
                && (intent.Types.Length == 0 || intent.Types.Contains(TrackingIntent.All) || intent.Types.Contains(x.Type)))
                .ToArray();
            if (matching.Length == 0) return ("failed", $"{awb} 没有找到你可以取消的订阅。");
            if (intent.Emails.Length == 0 && matching.Select(x => x.Email).Distinct().Count() > 1)
                return ("clarify", $"{awb} 对应多个邮箱，请指定要取消的邮箱。");
            if (intent.Types.Length == 0 && matching.Select(x => x.Type).Distinct().Count() > 1)
                return ("clarify", $"{awb} 存在轨迹跟踪和异常监控，请说明取消哪一类，或明确取消全部。");
            targets.AddRange(matching.Select(x => (x.Awb, x.Email, x.Type)));
        }

        targets = targets.Distinct().ToList();
        if (targets.Count > 5) return ("clarify", "请将操作拆分为每次最多五项订阅。");
        if (targets.Count == 1)
        {
            var target = targets[0];
            command.Intent = JsonSerializer.Serialize(intent with { Awbs = [target.Awb], Emails = [target.Email], Types = [target.Type] });
        }
        await db.SaveChangesAsync(ct);

        List<string> replies = [];
        List<string> statuses = [];
        var groups = targets.GroupBy(x => (x.Email, x.Type)).ToArray();
        var operation = cancel ? "取消订阅" : "提交订阅";
        for (var index = 0; index < groups.Length; index++)
        {
            var group = groups[index];
            var progress = groups.Length == 1 ? $"已识别轨迹指令，正在{operation}…" : $"已识别轨迹指令，正在{operation}（第 {index + 1}/{groups.Length} 组）…";
            await UpdateProgressAsync(robot, message.Id, progress, false, ct);
            var (email, type) = group.Key;
            var outcomes = await ChangeBatchAsync(command, group.Select(x => x.Awb).ToArray(), email, type, cancel, ct);
            foreach (var target in group)
            {
                var outcome = outcomes[target.Awb];
                statuses.Add(outcome.Status);
                replies.Add($"{target.Awb} · {type} · {Mask(email)}：{outcome.Description}");
            }
        }
        var overall = statuses.All(x => x == "succeeded") ? "succeeded"
            : statuses.Contains("unknown") ? "unknown" : statuses.Contains("succeeded") ? "partial" : "failed";
        return (overall, string.Join("\n\n", replies));
    }

    private sealed record ChangeOutcome(string Status, string Description);
    private sealed record Reservation(Guid Id, string Awb, string? PreviousStatus);

    private async Task<Dictionary<string, ChangeOutcome>> ChangeBatchAsync(
        TrackingCommand command, string[] awbs, string email, string type, bool cancel, CancellationToken ct)
    {
        var uncertain = cancel ? "cancel_unknown" : "subscribe_unknown";
        var outcomes = new Dictionary<string, ChangeOutcome>(StringComparer.Ordinal);
        var records = await db.TrackingSubscriptions.AsNoTracking().Where(x => awbs.Contains(x.Awb) && x.Email == email && x.Type == type)
            .ToDictionaryAsync(x => x.Awb, ct);
        List<Reservation> reservations = [];

        // Persist each reservation before sending the grouped write. An interrupted request must not be sent again.
        await using (var transaction = await db.Database.BeginTransactionAsync(ct))
        {
            foreach (var awb in awbs.Order(StringComparer.Ordinal))
            {
                var record = records.GetValueOrDefault(awb);
                if (CheckExisting(command, record, cancel) is { } known)
                {
                    outcomes.Add(awb, known);
                    continue;
                }

                var id = record?.Id ?? Guid.NewGuid();
                var now = DateTimeOffset.UtcNow;
                var changed = record is null
                    ? await db.Database.ExecuteSqlInterpolatedAsync($"""
                        INSERT INTO ai_bot.tracking_subscriptions ("Id", "RobotId", "SenderUserId", "Awb", "Email", "Type", "Status", "UpdatedAt")
                        VALUES ({id}, {command.RobotId}, {command.SenderUserId}, {awb}, {email}, {type}, {uncertain}, {now})
                        ON CONFLICT ("Awb", "Email", "Type") DO NOTHING
                        """, ct)
                    : await db.TrackingSubscriptions.Where(x => x.Id == id && x.Status == record.Status && x.UpdatedAt == record.UpdatedAt)
                        .ExecuteUpdateAsync(update => update.SetProperty(x => x.Status, uncertain).SetProperty(x => x.UpdatedAt, now), ct);
                if (changed == 0) outcomes.Add(awb, new("unknown", "订阅状态正在变更，请稍后核实。"));
                else reservations.Add(new(id, awb, record?.Status));
            }
            await transaction.CommitAsync(ct);
        }

        if (reservations.Count == 0) return outcomes;
        var reservedAwbs = reservations.Select(x => x.Awb).ToHashSet(StringComparer.Ordinal);
        TrackingChangeResult result;
        try
        {
            result = await backend.ChangeAsync(awbs.Where(reservedAwbs.Contains).ToArray(), email, type, cancel, ct);
        }
        catch (TrackingBackendException ex)
        {
            if (!ex.OutcomeUnknown) await RevertBatchAsync(reservations, uncertain, ct);
            var outcome = ex.OutcomeUnknown ? new ChangeOutcome("unknown", $"后台执行结果待核实。{ex.Message}")
                : new ChangeOutcome("failed", ex.Message);
            foreach (var item in reservations) outcomes.Add(item.Awb, outcome);
            return outcomes;
        }

        var confirmed = result.AcknowledgedAwbs.ToHashSet(StringComparer.Ordinal);
        var confirmedIds = reservations.Where(x => confirmed.Contains(x.Awb)).Select(x => x.Id).ToArray();
        if (confirmedIds.Length > 0)
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            await db.TrackingSubscriptions.Where(x => confirmedIds.Contains(x.Id) && x.Status == uncertain)
                .ExecuteUpdateAsync(update => update.SetProperty(x => x.Status, cancel ? "cancelled" : "active")
                    .SetProperty(x => x.UpdatedAt, DateTimeOffset.UtcNow), ct);
            await history.SavePushesAsync(result.Nodes, ct);
            await transaction.CommitAsync(ct);
        }

        var description = cancel ? "取消成功。" : type == TrackingIntent.Exception ? "异常监控已开启。" : "订阅成功。";
        foreach (var item in reservations)
            outcomes.Add(item.Awb, confirmed.Contains(item.Awb) ? new("succeeded", description)
                : new("unknown", "后台批量回执未确认此主单，请核实。"));
        return outcomes;
    }

    private static ChangeOutcome? CheckExisting(TrackingCommand command, TrackingSubscription? record, bool cancel)
    {
        if (record is not null && (record.RobotId != command.RobotId || record.SenderUserId != command.SenderUserId))
            return new("failed", "此订阅组合已由其他会话登记，请联系管理员。");
        if (record?.Status is "subscribe_unknown" or "cancel_unknown")
            return new("unknown", "上一次操作结果待核实，请联系管理员。");
        if (!cancel && record?.Status == "active") return new("succeeded", "已订阅。");
        if (cancel && record?.Status != "active") return new("failed", "没有可取消的订阅。");
        return null;
    }

    private async Task RevertBatchAsync(List<Reservation> reservations, string uncertain, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        foreach (var item in reservations)
        {
            var row = db.TrackingSubscriptions.Where(x => x.Id == item.Id && x.Status == uncertain);
            if (item.PreviousStatus is null) await row.ExecuteDeleteAsync(ct);
            else await row.ExecuteUpdateAsync(update => update.SetProperty(x => x.Status, item.PreviousStatus)
                .SetProperty(x => x.UpdatedAt, DateTimeOffset.UtcNow), ct);
        }
        await transaction.CommitAsync(ct);
    }

    private async Task<(string Status, string Result)> ReadHistoryAsync(TrackingIntent intent, List<TrackingSubscription> owned, CancellationToken ct)
    {
        var awbs = owned.Where(x => x.Status != "subscribe_unknown").Select(x => x.Awb).Distinct().ToArray();
        if (awbs.Length != intent.Awbs.Length)
            return ("failed", "只能查询你通过此机器人登记过的主单推送记录。");
        var refreshed = backend.Configured && backend.CanQuery && await history.RefreshAsync(awbs, ct);
        var records = await db.TrackingPushes.AsNoTracking().Where(x => awbs.Contains(x.Awb))
            .OrderByDescending(x => x.PushTime).ThenBy(x => x.Awb).ThenBy(x => x.Node).Take(10).ToListAsync(ct);
        var intro = refreshed ? "最近的主单邮件推送记录" : "当前已保存的主单邮件推送记录（本次未能刷新）";
        return ("succeeded", records.Count == 0 ? $"{intro}：暂无记录。"
            : intro + "\n\n" + string.Join("\n\n", records.Select(x => $"{x.Awb} · {x.PushTime} · {EscapeMarkdown(x.Node)}")));
    }

    private async Task<TrackingIntent?> GetContextAsync(TrackingCommand command, CancellationToken ct)
    {
        var cutoff = command.CreatedAt.AddMinutes(-15);
        var previous = await db.TrackingCommands.AsNoTracking()
            .Where(x => x.RobotId == command.RobotId && x.SenderUserId == command.SenderUserId
                && x.CreatedAt >= cutoff && x.CreatedAt < command.CreatedAt && x.Intent != ""
                && (x.Status == "clarify" || x.Status == "succeeded"))
            .OrderByDescending(x => x.CreatedAt).FirstOrDefaultAsync(ct);
        if (previous is null) return null;
        var intent = JsonSerializer.Deserialize<TrackingIntent>(previous.Intent);
        if (intent is null) return null;
        var contextAction = previous.Status == "clarify" ? intent.Action : "context";
        var emails = intent.Emails.Concat(intent.Routes?.Select(x => x.Email) ?? []).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var types = intent.Types.Concat(intent.Routes?.Select(x => x.Type) ?? []).Distinct().ToArray();
        return new(contextAction, intent.Awbs.Length == 1 ? intent.Awbs : [], emails.Length == 1 ? emails : [], types);
    }

    private static TrackingIntent ValidateSources(TrackingIntent intent, string input, TrackingIntent? previous)
    {
        var normalized = input.Replace('–', '-').Replace('—', '-').Replace('－', '-');
        var suppliedAwbs = Regex.Matches(normalized, @"(?<![0-9])([0-9]{3})[\s-]*([0-9]{8})(?![0-9])")
            .Select(match => match.Groups[1].Value + "-" + match.Groups[2].Value).ToHashSet(StringComparer.Ordinal);
        var routes = intent.Routes ?? [];
        var awbs = intent.Awbs.Concat(routes.SelectMany(x => x.Awbs)).Distinct(StringComparer.Ordinal).ToArray();
        var emails = intent.Emails.Concat(routes.Select(x => x.Email)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (routes.Any(x => x.Awbs.Length == 0) && intent.Awbs.Length == 0)
            throw new JsonException("收件规则缺少明确的主单范围。");
        if (awbs.Length > 5 || emails.Length > 5 || awbs.Any(x => !TrackingIntent.IsAwb(x)
                || !(suppliedAwbs.Contains(x) || suppliedAwbs.Count == 0 && previous?.Awbs.Contains(x) == true))
            || emails.Any(x => !TrackingIntent.IsEmail(x) ||
                !(Regex.IsMatch(input, $@"(?<![A-Z0-9._%+\-]){Regex.Escape(x)}(?![A-Z0-9_\-]|\.[A-Z0-9])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
                    || !input.Contains('@') && previous?.Emails.Contains(x, StringComparer.OrdinalIgnoreCase) == true)))
            throw new JsonException("操作参数无法追溯到会话。");
        return intent with
        {
            Awbs = awbs,
            Emails = intent.Emails.Select(x => x.ToLowerInvariant()).ToArray(),
            Routes = routes.Select(x => x with
            {
                Awbs = x.Awbs.Length == 0 ? intent.Awbs : x.Awbs,
                Email = x.Email.ToLowerInvariant()
            }).ToArray()
        };
    }

    private async Task FallBackAsync(TrackingCommand command, Robot robot, ReceivedMessage message, CancellationToken ct)
    {
        if (robot.AutoReplyEnabled && robot.ReplyMode == "quote")
        {
            var log = new AutoReplyLog(message.Id, robot.Id, "quote");
            log.Queue();
            db.AutoReplies.Add(log);
            await CompleteAsync(command, "skipped", "交给现有报价队列。", ct);
        }
        else
        {
            await CompleteAsync(command, "skipped", "交给普通问答。", ct);
            await autoReply.HandleAsync(robot.Id, robot.BotId, message, ct);
            // Close the receipt only if ordinary auto-reply did not already finish it.
            await UpdateProgressAsync(robot, message.Id, "这条消息没有匹配到可用的回复。", true, ct);
        }
    }

    private async Task UpdateProgressAsync(Robot robot, Guid messageId, string content, bool finish, CancellationToken ct)
    {
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(2));
            var secret = secrets.Unprotect(robot.ProtectedSecret);
            await gateway.UpdateReplyStreamAsync(robot.BotId, secret, messageId, content, finish, deadline.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogInformation("轨迹消息 {MessageId} 的进度更新未完成（{Type}）。", messageId, ex.GetType().Name);
        }
    }

    private async Task ReplyAsync(TrackingCommand command, ReceivedMessage message, string status, string result, CancellationToken ct)
    {
        await CompleteAsync(command, status, result, ct);
        await robots.ReplyAsync(message, AutoReplyService.LimitUtf8(result, 4096), "轨迹业务", ct);
    }

    private Task CompleteAsync(TrackingCommand command, string status, string result, CancellationToken ct)
    {
        command.Status = status;
        command.Result = result;
        command.CompletedAt = DateTimeOffset.UtcNow;
        return db.SaveChangesAsync(ct);
    }

    private static string Mask(string email) => EscapeMarkdown(email[..1] + "***" + email[email.LastIndexOf('@')..]);
    private static string EscapeMarkdown(string text) => text.Replace("\\", "\\\\").Replace("<", "&lt;")
        .Replace(">", "&gt;").Replace("[", "\\[").Replace("]", "\\]").Replace("*", "\\*").Replace("_", "\\_");
}
