namespace AiBot.Domain;

public sealed class QuoteDraft
{
    public Guid Id { get; private set; } = Guid.NewGuid();
    public Guid RobotId { get; private set; }
    public string ChatType { get; private set; } = "";
    public string ChatId { get; private set; } = "";
    public string SenderUserId { get; private set; } = "";
    public string Status { get; private set; } = "collecting";
    public string StateJson { get; private set; } = "{}";
    public string RuleVersion { get; private set; } = "air-quote-1.0.9";
    public DateTimeOffset UpdatedAt { get; private set; } = DateTimeOffset.UtcNow;

    private QuoteDraft() { }

    public QuoteDraft(Guid robotId, string chatType, string chatId, string senderUserId)
    {
        RobotId = robotId;
        ChatType = chatType;
        ChatId = chatId;
        SenderUserId = senderUserId;
    }

    public void Save(string stateJson)
    {
        if (Status != "collecting" || stateJson.Length > 32768) throw new InvalidOperationException("询价状态无效或过长。");
        StateJson = stateJson;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public void Complete()
    {
        Status = "quoted";
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public void Supersede()
    {
        Status = "superseded";
        UpdatedAt = DateTimeOffset.UtcNow;
    }
}
