using System.Text;
using AiBot.Domain;

namespace AiBot.Application;

public sealed class AutoReplyService(IAutomationRepository repository, IRobotRepository outbox, ISecretProtector secrets, IAiCompletionClient ai, IWeComGateway gateway) : IAutoReplyService
{
    public async Task HandleAsync(Guid robotId, string connectionBotId, ReceivedMessage message, CancellationToken ct)
    {
        var robot = await repository.RobotAsync(robotId, ct);
        if (robot is null || !robot.Enabled || !robot.AutoReplyEnabled || robot.BotId != connectionBotId || message.SenderUserId == robot.BotId || !MessageSender.IsHuman(message)
            || message.ChatType == "group" && !robot.ReplyToGroups || message.MessageType is not ("text" or "voice" or "mixed") || string.IsNullOrWhiteSpace(message.Content.Replace("[图片消息]", "", StringComparison.Ordinal))) return;
        var target = message.ChatType == "group" ? message.ChatId : message.SenderUserId;
        if (target.Length is 0 or > 256 || target == "@all" || target.Any(c => char.IsWhiteSpace(c) || c is '|' or ',' or ';')) return;
        var log = new AutoReplyLog(message.Id, robot.Id, robot.ReplyMode);
        if (robot.ReplyMode == "quote")
        {
            if (message.MessageType != "text") return;
            log.Queue();
            await repository.ClaimAsync(log, ct);
            return;
        }
        if (!await repository.ClaimAsync(log, ct)) return;
        var attempts = new List<string>(); var modelName = ""; var answer = ""; var submitted = false;
        MessageLog? outgoing = null;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(60));
            if (robot.ReplyMode == "knowledge")
            {
                var match = KnowledgeMatcher.Find(await repository.KnowledgeAsync(null, deadline.Token), robot.Id, message.Content);
                if (match is null) { log.Complete("skipped", "", "", "知识库未匹配，不自动回复。", ""); return; }
                answer = match.Answer; modelName = "知识库：" + match.Question[..Math.Min(match.Question.Length, 100)];
            }
            else
            {
                var models = await repository.ModelsAsync(deadline.Token);
                foreach (var modelId in robot.GetModelPriority())
                {
                    var model = models.FirstOrDefault(x => x.Id == modelId);
                    if (model is null || !model.Enabled || model.ProtectedApiKey.Length == 0) { attempts.Add($"{model?.Name ?? modelId.ToString()}：未启用或配置已失效，跳过。"); continue; }
                    try
                    {
                        answer = await ai.CompleteAsync(model, robot.ReplyPrompt, message.Content, deadline.Token);
                        if (string.IsNullOrWhiteSpace(answer)) throw new AiCompletionException("模型未返回可用文本。");
                        modelName = model.Name; attempts.Add($"{model.Name}：生成成功。"); break;
                    }
                    catch (OperationCanceledException) when (!deadline.IsCancellationRequested) { attempts.Add($"{model.Name}：接口超时，切换下一模型。"); }
                    catch (AiCompletionException ex) { attempts.Add($"{model.Name}：{ex.Message}"); }
                    catch (System.Security.Cryptography.CryptographicException) { attempts.Add($"{model.Name}：密钥不可读取，切换下一模型。"); }
                }
                if (string.IsNullOrWhiteSpace(answer)) { log.Complete("failed", "", "", "所有优先级模型均不可用，本次未发送回复。", string.Join("\n", attempts)); return; }
            }
            answer = LimitUtf8(answer, 4096);
            // Re-read settings after generation so disabling auto reply prevents a late send.
            var current = await repository.RobotAsync(robot.Id, deadline.Token);
            if (current is null || !current.Enabled || !current.AutoReplyEnabled || current.BotId != connectionBotId || current.ReplyMode != log.Mode || message.ChatType == "group" && !current.ReplyToGroups)
            { log.Complete("skipped", modelName, answer, "生成期间机器人配置已变更或关闭自动回复，未发送。", string.Join("\n", attempts)); return; }
            outgoing = new MessageLog(log.Id, current, target, answer, current.ReplyMode == "knowledge" ? "自动回复·知识库" : "自动回复·AI", message.ChatType);
            if (!await outbox.TryStartMessageAsync(outgoing, deadline.Token)) { log.Complete("unknown", modelName, answer, "发送标识已存在，不重复提交。", string.Join("\n", attempts)); return; }
            submitted = true;
            var result = await gateway.SendReplyAsync(current.BotId, secrets.Unprotect(current.ProtectedSecret), message.Id, message.ChatType, target, answer, deadline.Token);
            var status = result.Ok ? DeliveryStatus.Accepted : result.Stage is "unknown" or "send-unknown" or "stream-unknown" ? DeliveryStatus.Unknown : DeliveryStatus.Failed;
            outgoing.Complete(status, result.Message, result.RequestId, result.Errcode);
            log.Complete(result.Ok ? "accepted" : status == DeliveryStatus.Unknown ? "unknown" : "failed", modelName, answer, result.Message, string.Join("\n", attempts));
        }
        catch (Exception)
        {
            var result = submitted ? "自动回复结果未确认。不会自动重发，请先查看企微会话。" : "自动回复生成失败或已取消，本次未发送。";
            outgoing?.Complete(DeliveryStatus.Unknown, result, null, null);
            log.Complete(submitted ? "unknown" : "failed", modelName, answer, result, string.Join("\n", attempts));
        }
        finally
        {
            using var save = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await repository.CompleteAsync(log, save.Token);
            if (outgoing is not null) await outbox.CompleteMessageAsync(outgoing, save.Token);
        }
    }
    public static string LimitUtf8(string value, int limit)
    {
        if (Encoding.UTF8.GetByteCount(value) <= limit) return value;
        const string suffix = "\n\n（内容过长，已截断）";
        var budget = limit - Encoding.UTF8.GetByteCount(suffix); var result = new StringBuilder();
        foreach (var rune in value.EnumerateRunes()) { if (budget < rune.Utf8SequenceLength) break; result.Append(rune.ToString()); budget -= rune.Utf8SequenceLength; }
        return result.Append(suffix).ToString();
    }
}
