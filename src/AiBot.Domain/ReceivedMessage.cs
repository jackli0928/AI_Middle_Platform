namespace AiBot.Domain;

public sealed class ReceivedMessage
{
    public Guid Id { get; private set; } = Guid.NewGuid();
    public Guid RobotId { get; private set; }
    public string RobotName { get; private set; } = "";
    public string MessageId { get; private set; } = "";
    public string RequestId { get; private set; } = "";
    public string SenderUserId { get; private set; } = "";
    public string ChatId { get; private set; } = "";
    public string ChatType { get; private set; } = "single";
    public string MessageType { get; private set; } = "text";
    public string Content { get; private set; } = "";
    public string PayloadJson { get; private set; } = "{}";
    public DateTimeOffset? SentAt { get; private set; }
    public DateTimeOffset ReceivedAt { get; private set; } = DateTimeOffset.UtcNow;
    private ReceivedMessage() { }
    public ReceivedMessage(Guid robotId, string robotName, string messageId, string requestId, string senderUserId,
        string chatId, string chatType, string messageType, string content, string payloadJson, DateTimeOffset? sentAt)
    {
        if (string.IsNullOrWhiteSpace(messageId) || messageId.Length > 256) throw new ArgumentException("消息 ID 无效。");
        if (chatType is not ("single" or "group")) throw new ArgumentException("会话类型无效。");
        if (robotName.Length > 80 || requestId.Length > 256 || senderUserId.Length > 256 || chatId.Length > 256 || messageType.Length is 0 or > 64)
            throw new ArgumentException("消息字段超出范围。");
        RobotId = robotId; RobotName = robotName; MessageId = messageId; RequestId = requestId;
        SenderUserId = senderUserId; ChatId = chatId; ChatType = chatType; MessageType = messageType;
        Content = content; PayloadJson = payloadJson; SentAt = sentAt;
    }
}
