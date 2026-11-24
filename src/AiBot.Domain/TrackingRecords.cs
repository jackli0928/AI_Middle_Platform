namespace AiBot.Domain;

public sealed class TrackingCommand
{
    public Guid Id { get; set; }
    public Guid RobotId { get; set; }
    public string SenderUserId { get; set; } = "";
    public string Status { get; set; } = "queued";
    public string Result { get; set; } = "";
    public string Intent { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

public sealed class TrackingSubscription
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RobotId { get; set; }
    public string SenderUserId { get; set; } = "";
    public string Awb { get; set; } = "";
    public string Email { get; set; } = "";
    public string Type { get; set; } = "";
    public string Status { get; set; } = "";
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class TrackingPush
{
    public string Awb { get; set; } = "";
    public string Node { get; set; } = "";
    public string PushTime { get; set; } = "";
    public string? QueryAddress { get; set; }
    public DateTimeOffset DetectedAt { get; set; }
}

public sealed class TrackingRefresh
{
    public string Awb { get; set; } = "";
    public DateTimeOffset QueriedAt { get; set; }
    public string Result { get; set; } = "";
}
