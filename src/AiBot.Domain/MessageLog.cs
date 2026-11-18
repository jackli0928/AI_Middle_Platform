namespace AiBot.Domain;

public enum DeliveryStatus { Pending, Accepted, Failed, Unknown }

public sealed class MessageLog
{
    public Guid Id { get; private set; }
    public Guid RobotId { get; private set; }
    public string RobotName { get; private set; } = "";
    public string UserId { get; private set; } = "";
    public string ChatId { get; private set; } = "";
    public string TargetType { get; private set; } = "single";
    public string TargetId => TargetType == "group" ? ChatId : UserId;
    public string Content { get; private set; } = "";
    public string Operator { get; private set; } = "";
    public DeliveryStatus Status { get; private set; }
    public string Result { get; private set; } = "等待企业微信回执";
    public string? RequestId { get; private set; }
    public int? ErrorCode { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; } = DateTimeOffset.UtcNow;
    private MessageLog() { }
    public MessageLog(Guid id, Robot robot, string targetId, string content, string actor, string targetType = "single")
    {
        if (targetType is not ("single" or "group")) throw new ArgumentException("无效的发送目标类型。");
        Id = id; RobotId = robot.Id; RobotName = robot.Name; TargetType = targetType;
        UserId = targetType == "single" ? targetId : ""; ChatId = targetType == "group" ? targetId : "";
        Content = content; Operator = actor;
    }
    public void Complete(DeliveryStatus status, string result, string? requestId, int? errorCode)
    { Status = status; Result = result; RequestId = requestId; ErrorCode = errorCode; }
}
