namespace AiBot.Domain;

public sealed class AutoReplyLog
{
    public Guid Id { get; private set; } = Guid.NewGuid();
    public Guid ReceivedMessageId { get; private set; }
    public Guid RobotId { get; private set; }
    public string Mode { get; private set; } = "";
    public string Status { get; private set; } = "generating";
    public string ModelName { get; private set; } = "";
    public string ReplyContent { get; private set; } = "";
    public string Result { get; private set; } = "处理中；若进程中断则不会自动重发。";
    public string Attempts { get; private set; } = "";
    public DateTimeOffset CreatedAt { get; private set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; private set; }
    private AutoReplyLog() { }
    public AutoReplyLog(Guid receivedMessageId, Guid robotId, string mode) { ReceivedMessageId = receivedMessageId; RobotId = robotId; Mode = mode; }
    public void Complete(string status, string modelName, string replyContent, string result, string attempts)
    { Status = status; ModelName = modelName; ReplyContent = replyContent; Result = result; Attempts = attempts; CompletedAt = DateTimeOffset.UtcNow; }
}
