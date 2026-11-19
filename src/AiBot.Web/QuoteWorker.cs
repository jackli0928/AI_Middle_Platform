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
                        select new { log.Id, robot.BotId, log.RobotId, message.SenderUserId }).Take(20).ToListAsync(stoppingToken);
                    foreach (var candidate in candidates)
                    {
                        if (!gateway.HasSession(candidate.BotId)) continue;
                        var conversation = candidate.RobotId + ":" + candidate.SenderUserId;
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
        var submitted = false;
        var answer = "";
        try
        {
            var message = await db.ReceivedMessages.AsNoTracking().SingleAsync(x => x.Id == log.ReceivedMessageId, ct);
            var robot = await db.Robots.AsNoTracking().SingleOrDefaultAsync(x => x.Id == log.RobotId, ct);
            if (robot is null || !robot.Enabled || !robot.AutoReplyEnabled || robot.ReplyMode != "quote")
            { log.Complete("skipped", "", "", "报价机器人已停用。", ""); return; }
            var models = await db.AiModels.AsNoTracking().Where(x => x.Enabled).ToListAsync(ct);
            var model = robot.GetModelPriority().Select(id => models.FirstOrDefault(x => x.Id == id)).FirstOrDefault(x => x is not null);
            Dictionary<string, object> fields;
            if (model is null)
            {
                fields = QuoteInquiryParser.Parse(message.Content);
            }
            else
            {
                var extraction = await scope.ServiceProvider.GetRequiredService<IAiCompletionClient>().CompleteAsync(model,
                    "从当前空运询价中提取 JSON 对象，只输出 JSON，不解释。字段仅允许 origin、destination、piece、gross_weight、volume、cargo_type、server_type、packing、customs、delivery_time。必须从原文明确取得值，不推测。piece 为整数，重量 kg 和体积 m³ 为数字。缺失字段省略。", message.Content, ct);
                var jsonText = extraction.Trim();
                if (jsonText.StartsWith("```", StringComparison.Ordinal))
                    jsonText = jsonText[(jsonText.IndexOf('\n') + 1)..jsonText.LastIndexOf("```", StringComparison.Ordinal)].Trim();
                using var parsed = JsonDocument.Parse(jsonText);
                if (parsed.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("询价字段提取结果无效。");
                fields = new Dictionary<string, object>();
                foreach (var name in new[] { "origin", "destination", "cargo_type", "server_type", "packing", "customs", "delivery_time" })
                    if (parsed.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                        && !string.IsNullOrWhiteSpace(value.GetString())) fields[name] = value.GetString()!;
                if (parsed.RootElement.TryGetProperty("piece", out var pieces) && pieces.ValueKind == JsonValueKind.Number
                    && pieces.TryGetInt32(out var piece) && piece > 0)
                    fields["piece"] = piece;
                foreach (var name in new[] { "gross_weight", "volume" })
                    if (parsed.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
                        && value.TryGetDouble(out var number) && double.IsFinite(number) && number > 0) fields[name] = number;
            }
            var missing = new[] { "destination", "piece", "gross_weight", "volume" }.Where(x => !fields.ContainsKey(x)).ToArray();
            if (missing.Length > 0)
                answer = "请补充询价信息：" + string.Join("、", missing.Select(x => x switch { "destination" => "目的地", "piece" => "件数", "gross_weight" => "总毛重（kg）", _ => "总体积（m³）" })) + "。请在下一条消息中发送完整询价。";
            else
            {
                fields["all_content"] = message.Content;
                using var arguments = JsonSerializer.SerializeToDocument(fields);
                answer = AutoReplyService.LimitUtf8(await mcp.QuoteAsync(arguments.RootElement, ct), 4096);
            }
            robot = await db.Robots.AsNoTracking().SingleOrDefaultAsync(x => x.Id == log.RobotId, ct);
            if (robot is null || !robot.Enabled || !robot.AutoReplyEnabled || robot.ReplyMode != "quote")
            { log.Complete("skipped", "", answer, "查询期间报价机器人已停用。", ""); return; }
            outgoing = new MessageLog(log.Id, robot, message.SenderUserId, answer, "自动回复·报价", "single");
            db.Messages.Add(outgoing);
            await db.SaveChangesAsync(ct);
            submitted = true;
            var secret = scope.ServiceProvider.GetRequiredService<ISecretProtector>().Unprotect(robot.ProtectedSecret);
            var result = await gateway.SendAsync(robot.BotId, secret, "single", message.SenderUserId, answer, ct);
            var status = result.Ok ? DeliveryStatus.Accepted : result.Stage is "unknown" or "send-unknown" ? DeliveryStatus.Unknown : DeliveryStatus.Failed;
            outgoing.Complete(status, result.Message, result.RequestId, result.Errcode);
            log.Complete(result.Ok ? "accepted" : status == DeliveryStatus.Unknown ? "unknown" : "failed", "报价 MCP", answer, result.Message, "");
        }
        catch (Exception ex)
        {
            logger.LogWarning("报价任务 {Id} 处理异常（{Type}）。", id, ex.GetType().Name);
            var detail = submitted ? "发送结果待核实，请检查企微会话。" : "报价查询失败，本次未发送。";
            outgoing?.Complete(DeliveryStatus.Unknown, detail, null, null);
            log.Complete(submitted ? "unknown" : "failed", "报价 MCP", answer, detail, "");
        }
        finally
        {
            using var save = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await db.SaveChangesAsync(save.Token);
        }
    }
}
