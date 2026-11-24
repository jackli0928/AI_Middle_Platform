using System.Text;
using AiBot.Domain;

namespace AiBot.Application;

public sealed class RobotService(IRobotRepository repository, ISecretProtector secrets, IWeComGateway gateway, IAutomationRepository automation) : IRobotService
{
    private static RobotInfo Map(Robot r) => new(r.Id, r.Name, r.BotId, r.Description, r.Enabled, r.UpdatedAt) { TrackingModelId = r.TrackingModelId, AutoReplyEnabled = r.AutoReplyEnabled, ReplyMode = r.ReplyMode, ReplyToGroups = r.ReplyToGroups, ReplyPrompt = r.ReplyPrompt, ModelPriority = r.GetModelPriority() };
    public async Task<IReadOnlyList<RobotInfo>> ListAsync(string? search, CancellationToken ct) => (await repository.ListAsync(search, ct)).Select(Map).ToList();
    public async Task<RobotInfo?> FindAsync(Guid id, CancellationToken ct) => await repository.FindAsync(id, ct) is { } r ? Map(r) : null;
    public async Task SaveAsync(RobotInput input, CancellationToken ct)
    {
        if (await repository.BotIdExistsAsync(input.BotId.Trim(), input.Id, ct)) throw new ArgumentException("该 Bot ID 已存在，请勿重复添加。");
        if (input.Secret?.Length > 512) throw new ArgumentException("Secret 不能超过 512 字。");
        var protectedSecret = string.IsNullOrWhiteSpace(input.Secret) ? null : secrets.Protect(input.Secret.Trim());
        var priorities = input.ModelPriority.Where(x => x != Guid.Empty).ToList();
        var models = await automation.ModelsAsync(ct);
        if (priorities.Any(id => models.All(x => x.Id != id))) throw new ArgumentException("优先级列表中包含不存在的模型，请重新选择。");
        if (input.AutoReplyEnabled && input.ReplyMode == "ai" && !models.Any(x => priorities.Contains(x.Id) && x.Enabled)) throw new ArgumentException("请至少选择一个已启用且已配置密钥的 AI 模型。");
        if (input.AutoReplyEnabled && input.ReplyMode == "quote" && priorities.Count > 0 && !models.Any(x => priorities.Contains(x.Id) && x.Enabled)) throw new ArgumentException("所选报价字段提取模型不可用。");
        if (input.TrackingModelId is { } trackingModelId &&
            !models.Any(x => x.Id == trackingModelId && x.Enabled && x.ProtectedApiKey.Length > 0))
            throw new ArgumentException("请选择已启用且已配置密钥的轨迹意图模型。");
        if (input.Id is { } id)
        {
            var robot = await repository.FindAsync(id, ct) ?? throw new ArgumentException("机器人不存在或已被删除。");
            robot.Update(input.Name, input.BotId, protectedSecret, input.Description, input.Enabled);
            robot.ConfigureTracking(input.TrackingModelId);
            robot.ConfigureAutoReply(input.AutoReplyEnabled, input.ReplyMode, input.ReplyToGroups, input.ReplyPrompt, priorities);
        }
        else
        {
            var robot = Robot.Create(input.Name, input.BotId, protectedSecret ?? "", input.Description, input.Enabled);
            robot.ConfigureTracking(input.TrackingModelId);
            robot.ConfigureAutoReply(input.AutoReplyEnabled, input.ReplyMode, input.ReplyToGroups, input.ReplyPrompt, priorities); repository.Add(robot);
        }
        await repository.SaveAsync(ct);
    }
    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        var robot = await repository.FindAsync(id, ct) ?? throw new ArgumentException("机器人不存在或已被删除。");
        repository.Remove(robot); await repository.SaveAsync(ct);
    }
    public Task<SendResult> SendAsync(SendInput input, string actor, CancellationToken ct) =>
        SendCoreAsync(input, actor, null, ct);

    public Task<SendResult> ReplyAsync(ReceivedMessage message, string content, string actor, CancellationToken ct) =>
        SendCoreAsync(new SendInput
        {
            OperationId = message.Id,
            RobotId = message.RobotId,
            TargetType = message.ChatType,
            UserId = message.SenderUserId,
            ChatId = message.ChatId,
            Content = content
        }, actor, message.Id, ct);

    private async Task<SendResult> SendCoreAsync(SendInput input, string actor, Guid? replyTo, CancellationToken ct)
    {
        if (input.TargetType is not ("single" or "group")) throw new ArgumentException("请选择单聊或群聊。");
        var target = input.GetTargetId();
        if (target.Length is 0 or > 256 || target == "@all" || target.Any(c => char.IsWhiteSpace(c) || c is '|' or ',' or ';' or '，' or '；'))
            throw new ArgumentException(input.TargetType == "group" ? "请填写一个群聊的 Chat ID，不支持群名称、多个 ID 或 @all。" : "请填写一个接收人的企微 User ID，不支持多个接收人或 @all。");
        if (string.IsNullOrWhiteSpace(input.Content) || Encoding.UTF8.GetByteCount(input.Content) > 4096)
            throw new ArgumentException("消息不能为空，且不能超过 4096 个 UTF-8 字节。");
        if (input.OperationId == Guid.Empty) throw new ArgumentException("发送标识无效，请刷新页面后重试。");
        var robot = await repository.FindAsync(input.RobotId, ct) ?? throw new ArgumentException("请选择有效的机器人。");
        if (!robot.Enabled) throw new ArgumentException("该机器人已停用，无法发送消息。");
        var secret = secrets.Unprotect(robot.ProtectedSecret);
        var log = new MessageLog(input.OperationId, robot, target, input.Content, actor, input.TargetType);
        if (!await repository.TryStartMessageAsync(log, ct))
            return new(false, "duplicate", "这次发送已提交，请查看消息记录，避免重复发送。");
        SendResult result;
        try
        {
            result = replyTo is { } messageId
                ? await gateway.SendReplyAsync(robot.BotId, secret, messageId, input.TargetType, target, input.Content, ct)
                : await gateway.SendAsync(robot.BotId, secret, input.TargetType, target, input.Content, ct);
        }
        catch (OperationCanceledException)
        { result = new(false, "unknown", "请求已取消，发送结果未确认。请查看企微会话，避免重复发送。"); }
        catch
        { result = new(false, "unknown", "发送结果未确认。请检查企微会话后再操作。"); }
        var status = result.Ok ? DeliveryStatus.Accepted : result.Stage is "unknown" or "send-unknown" or "stream-unknown" ? DeliveryStatus.Unknown : DeliveryStatus.Failed;
        log.Complete(status, result.Message, result.RequestId, result.Errcode);
        // Persist the receipt even if the browser disconnected. A pending log must not imply failure.
        using var saveDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await repository.CompleteMessageAsync(log, saveDeadline.Token);
        return result;
    }
    public Task<Dashboard> DashboardAsync(CancellationToken ct)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Shanghai");
        var local = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone);
        var today = new DateTimeOffset(local.Date, local.Offset).ToUniversalTime();
        return repository.DashboardAsync(today, ct);
    }
    public Task<PageResult<MessageLog>> LogsAsync(int page, string? search, CancellationToken ct) => repository.LogsAsync(Math.Max(1, page), search, ct);
}
