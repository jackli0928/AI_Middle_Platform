using AiBot.Application;
using AiBot.Domain;
using AiBot.Infrastructure;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using System.Collections.Concurrent;

namespace AiBot.Web;

public sealed class QuoteWorker(IServiceScopeFactory scopes, WeComGateway gateway, QuoteMcpClient mcp, ILogger<QuoteWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var slots = new SemaphoreSlim(4);
        var inFlight = new ConcurrentDictionary<string, byte>();
        var active = new List<Task>();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (mcp.Configured)
                {
                    using var scope = scopes.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
                    var candidates = await (from log in db.AutoReplies.AsNoTracking()
                        join robot in db.Robots.AsNoTracking() on log.RobotId equals robot.Id
                        join message in db.ReceivedMessages.AsNoTracking() on log.ReceivedMessageId equals message.Id
                        where log.Mode == "quote" && log.Status == "queued" && robot.Enabled && robot.AutoReplyEnabled && robot.ReplyMode == "quote"
                        orderby log.CreatedAt
                        select new { log.Id, robot.BotId, log.RobotId, message.ChatType, message.ChatId, message.SenderUserId }).Take(20).ToListAsync(stoppingToken);
                    foreach (var candidate in candidates)
                    {
                        if (!gateway.HasSession(candidate.BotId)) continue;
                        var conversation = candidate.RobotId + ":" + candidate.ChatType + ":"
                            + (candidate.ChatType == "group" ? candidate.ChatId + ":" + candidate.SenderUserId : candidate.SenderUserId);
                        if (!inFlight.TryAdd(conversation, 0)) continue;
                        if (!slots.Wait(0)) { inFlight.TryRemove(conversation, out _); break; }
                        int claimed;
                        try
                        {
                            claimed = await db.AutoReplies.Where(x => x.Id == candidate.Id && x.Status == "queued")
                                .ExecuteUpdateAsync(x => x.SetProperty(y => y.Status, "generating").SetProperty(y => y.Result, "正在查询报价。"), stoppingToken);
                        }
                        catch
                        {
                            slots.Release();
                            inFlight.TryRemove(conversation, out _);
                            throw;
                        }
                        if (claimed == 0) { slots.Release(); inFlight.TryRemove(conversation, out _); continue; }
                        active.Add(Task.Run(async () =>
                        {
                            try { await ProcessAsync(candidate.Id, stoppingToken); }
                            catch (Exception ex) { logger.LogWarning("报价任务 {Id} 状态保存失败（{Type}）。", candidate.Id, ex.GetType().Name); }
                            finally { slots.Release(); inFlight.TryRemove(conversation, out _); }
                        }, CancellationToken.None));
                    }
                    active.RemoveAll(x => x.IsCompleted);
                }
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogWarning("报价队列暂时不可用（{Type}）。", ex.GetType().Name);
                try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            }
        }
        await Task.WhenAll(active);
    }

    private async Task ProcessAsync(Guid id, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
        var log = await db.AutoReplies.SingleAsync(x => x.Id == id, ct);
        MessageLog? outgoing = null;
        ReceivedMessage? message = null;
        Robot? robot = null;
        QuoteDraft? draft = null;
        var submitted = false;
        var answer = "";
        var extraAnswers = new List<string>();
        var priceResolved = false;
        var stage = "load";
        try
        {
            message = await db.ReceivedMessages.AsNoTracking().SingleAsync(x => x.Id == log.ReceivedMessageId, ct);
            robot = await db.Robots.AsNoTracking().SingleOrDefaultAsync(x => x.Id == log.RobotId, ct);
            if (robot is null || !robot.Enabled || !robot.AutoReplyEnabled || robot.ReplyMode != "quote"
                || message.ChatType == "group" && !robot.ReplyToGroups)
            { log.Complete("skipped", "", "", "报价机器人已停用或群聊回复已关闭。", ""); return; }
            stage = "extract";
            var open = await db.QuoteDrafts.Where(x => x.RobotId == robot.Id && x.ChatType == message.ChatType
                && x.ChatId == message.ChatId && x.SenderUserId == message.SenderUserId && x.Status == "collecting")
                .OrderByDescending(x => x.UpdatedAt).ToListAsync(ct);
            var previous = open.FirstOrDefault(x => DateTimeOffset.UtcNow - x.UpdatedAt < TimeSpan.FromHours(24));
            var priorState = previous is null ? null : JsonSerializer.Deserialize<QuoteDialogueState>(previous.StateJson);
            var extractionNotes = new List<string>();
            var modelPatch = new Dictionary<string, object>();
            var models = await db.AiModels.AsNoTracking().Where(x => x.Enabled).ToListAsync(ct);
            foreach (var modelId in robot.GetModelPriority())
            {
                var model = models.FirstOrDefault(x => x.Id == modelId);
                if (model is null) continue;
                try
                {
                    var extraction = await scope.ServiceProvider.GetRequiredService<IAiCompletionClient>().CompleteAsync(model,
                        "只提取当前客户消息中新给出或明确更正的空运询价字段，不能复制历史值或猜测。只输出 JSON：{\"fields\":{\"piece\":{\"value\":2,\"evidence\":\"2托\"}}}。每个字段都需原文证据，缺失就省略。允许字段 origin、destination、piece、gross_weight、volume、cargo_type、server_type、packing、customs、delivery_time。起运地和目的地优先使用能唯一确认的机场三字码，例如深圳发SVO为 SZX 到 SVO；城市有多个机场且客户未指定时不要猜测代码。数字用 kg、m³；客户未明确说总体积时省略 volume，尺寸由系统计算。", message.Content, ct);
                    modelPatch = QuoteDialogue.ParseModelPatch(extraction, message.Content);
                    break;
                }
                catch (AiCompletionException ex) { extractionNotes.Add(model.Name + "：" + ex.Message); logger.LogInformation("报价字段模型 {Model} 不可用：{Reason}", model.Name, ex.Message); }
                catch (JsonException) { extractionNotes.Add(model.Name + "：未返回有效字段 JSON。"); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { extractionNotes.Add(model.Name + "：接口超时。"); }
                catch (System.Security.Cryptography.CryptographicException) { extractionNotes.Add(model.Name + "：模型密钥不可读取。"); }
            }
            if (QuoteDialogue.StartsNewTicket(message.Content, priorState, modelPatch))
            {
                foreach (var old in open) old.Supersede();
                draft = new QuoteDraft(robot.Id, message.ChatType, message.ChatId, message.SenderUserId);
                db.QuoteDrafts.Add(draft);
                priorState = new QuoteDialogueState();
            }
            else draft = previous!;
            var step = QuoteDialogue.Apply(priorState!, message.Content, modelPatch);
            draft!.Save(JsonSerializer.Serialize(step.State));
            await db.SaveChangesAsync(ct);
            if (!step.Ready) answer = step.Question!;
            else
            {
                stage = "mcp";
                await ReportProgressAsync(robot, message, "询价条件已识别，正在查询运价…", ct);
                var results = new List<string>();
                var completedRoutes = 0;
                foreach (var call in step.Calls)
                {
                    using var arguments = JsonSerializer.SerializeToDocument(call);
                    try { results.Add(await mcp.QuoteAsync(arguments.RootElement, ct)); completedRoutes++; }
                    catch (QuoteMcpInputException ex)
                    {
                        var labels = ex.MissingFields.Select(x => x switch
                        {
                            "destination" => "目的地", "piece" => "件数", "gross_weight" => "总毛重",
                            "volume" => "总体积", _ => "本票询价原文"
                        }).ToArray();
                        results.Add("请补充或确认询价信息：" + (labels.Length > 0 ? string.Join("、", labels) : "当前报价条件")
                            + "。直接回复需要补充的信息即可。");
                    }
                    catch (Exception) when (step.Calls.Count > 1 && !ct.IsCancellationRequested)
                    { results.Add((call.TryGetValue("origin", out var airport) ? airport + "：" : "") + "报价接口暂时不可用，请稍后重试。"); }
                }
                foreach (var routeReply in results)
                    extraAnswers.AddRange(QuoteReplySegments.Split(routeReply, 4096));
                answer = extraAnswers[0];
                extraAnswers.RemoveAt(0);
                priceResolved = completedRoutes > 0;
                if (priceResolved) draft.Complete();
            }
            stage = "send";
            robot = await db.Robots.AsNoTracking().SingleOrDefaultAsync(x => x.Id == log.RobotId, ct);
            if (robot is null || !robot.Enabled || !robot.AutoReplyEnabled || robot.ReplyMode != "quote"
                || message.ChatType == "group" && !robot.ReplyToGroups)
            { log.Complete("skipped", "", answer, "查询期间报价机器人已停用或群聊回复已关闭。", ""); return; }
            var target = message.ChatType == "group" ? message.ChatId : message.SenderUserId;
            outgoing = new MessageLog(log.Id, robot, target, answer, "自动回复·报价", message.ChatType);
            db.Messages.Add(outgoing);
            await db.SaveChangesAsync(ct);
            submitted = true;
            var secret = scope.ServiceProvider.GetRequiredService<ISecretProtector>().Unprotect(robot.ProtectedSecret);
            var result = await DeliverAsync(robot, secret, message, answer, ct);
            var status = result.Ok ? DeliveryStatus.Accepted : result.Stage is "unknown" or "send-unknown" or "stream-unknown" ? DeliveryStatus.Unknown : DeliveryStatus.Failed;
            outgoing.Complete(status, result.Message, result.RequestId, result.Errcode);
            var deliveryStatuses = new List<DeliveryStatus> { status };
            foreach (var extraAnswer in extraAnswers)
            {
                var extra = new MessageLog(Guid.NewGuid(), robot, target, extraAnswer, "自动回复·报价", message.ChatType);
                try
                {
                    db.Messages.Add(extra);
                    await db.SaveChangesAsync(ct);
                    var sent = await gateway.SendAsync(robot.BotId, secret, message.ChatType, target, extraAnswer, ct);
                    var extraStatus = sent.Ok ? DeliveryStatus.Accepted : sent.Stage is "unknown" or "send-unknown" ? DeliveryStatus.Unknown : DeliveryStatus.Failed;
                    extra.Complete(extraStatus, sent.Message, sent.RequestId, sent.Errcode);
                    deliveryStatuses.Add(extraStatus);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    extra.Complete(DeliveryStatus.Unknown, "分条报价发送结果待核实。", null, null);
                    deliveryStatuses.Add(DeliveryStatus.Unknown);
                    logger.LogWarning("报价任务 {Id} 的分条消息发送异常（{Type}）。", id, ex.GetType().Name);
                }
            }
            var overallStatus = deliveryStatuses.Contains(DeliveryStatus.Unknown) ? "unknown"
                : deliveryStatuses.Contains(DeliveryStatus.Failed) ? "failed" : "accepted";
            log.Complete(overallStatus, priceResolved ? "报价 MCP" : "询价补充",
                answer, extraAnswers.Count == 0 ? result.Message : $"分 {extraAnswers.Count + 1} 条回复；发送状态：{overallStatus}。",
                string.Join("\n", extractionNotes));
        }
        catch (Exception ex)
        {
            logger.LogWarning("报价任务 {Id} 在 {Stage} 阶段异常（{Type}）。", id, stage, ex.GetType().Name);
            var detail = submitted ? "发送结果待核实，请检查企微会话。" : "报价查询失败，本次未发送。";
            outgoing?.Complete(DeliveryStatus.Unknown, detail, null, null);
            if (!submitted && outgoing is null && message is not null && robot is not null && !ct.IsCancellationRequested)
            {
                try
                {
                    var current = await db.Robots.AsNoTracking().SingleOrDefaultAsync(x => x.Id == log.RobotId, ct);
                    if (current is { Enabled: true, AutoReplyEnabled: true, ReplyMode: "quote" }
                        && (message.ChatType == "single" || current.ReplyToGroups))
                    {
                        answer = "报价查询暂时无法完成，请稍后重新发送询价。";
                        var target = message.ChatType == "group" ? message.ChatId : message.SenderUserId;
                        outgoing = new MessageLog(log.Id, current, target, answer, "自动回复·报价", message.ChatType);
                        db.Messages.Add(outgoing);
                        await db.SaveChangesAsync(ct);
                        submitted = true;
                        var secret = scope.ServiceProvider.GetRequiredService<ISecretProtector>().Unprotect(current.ProtectedSecret);
                        var notice = await DeliverAsync(current, secret, message, answer, ct);
                        var status = notice.Ok ? DeliveryStatus.Accepted : notice.Stage is "unknown" or "send-unknown" or "stream-unknown" ? DeliveryStatus.Unknown : DeliveryStatus.Failed;
                        outgoing.Complete(status, notice.Message, notice.RequestId, notice.Errcode);
                        detail = notice.Ok ? "报价查询失败，已发送提示。" : "报价查询失败，提示消息发送未完成。";
                    }
                }
                catch (Exception noticeError)
                {
                    logger.LogWarning("报价任务 {Id} 的失败提示发送异常（{Type}）。", id, noticeError.GetType().Name);
                    outgoing?.Complete(DeliveryStatus.Unknown, "提示消息结果待核实。", null, null);
                    detail = "报价查询失败，提示消息结果待核实。";
                }
            }
            log.Complete("failed", "报价 MCP", answer, detail, $"阶段：{stage}；异常：{ex.GetType().Name}");
        }
        finally
        {
            using var save = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await db.SaveChangesAsync(save.Token);
        }
    }
    private async Task ReportProgressAsync(Robot robot, ReceivedMessage message, string content, CancellationToken ct)
    {
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(2));
            using var scope = scopes.CreateScope();
            var secret = scope.ServiceProvider.GetRequiredService<ISecretProtector>().Unprotect(robot.ProtectedSecret);
            await gateway.UpdateQuoteStreamAsync(robot.BotId, secret, message.Id, content, false, deadline.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        { logger.LogInformation("报价消息 {MessageId} 的进度更新未完成（{Type}）。", message.Id, ex.GetType().Name); }
    }
    private async Task<SendResult> DeliverAsync(Robot robot, string secret, ReceivedMessage message, string answer, CancellationToken ct)
    {
        var stream = await gateway.UpdateQuoteStreamAsync(robot.BotId, secret, message.Id, answer, true, ct);
        if (stream is null)
        {
            // The callback may still be creating the initial placeholder after the quote finishes.
            var remaining = TimeSpan.FromSeconds(4) - (DateTimeOffset.UtcNow - message.ReceivedAt);
            if (remaining > TimeSpan.Zero)
            {
                await Task.Delay(remaining, ct);
                stream = await gateway.UpdateQuoteStreamAsync(robot.BotId, secret, message.Id, answer, true, ct);
            }
        }
        if (stream is { Ok: true } or { Stage: "stream-unknown" }) return stream;
        var target = message.ChatType == "group" ? message.ChatId : message.SenderUserId;
        return await gateway.SendAsync(robot.BotId, secret, message.ChatType, target, answer, ct);
    }
}
