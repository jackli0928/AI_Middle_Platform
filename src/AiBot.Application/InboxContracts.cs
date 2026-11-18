using AiBot.Domain;

namespace AiBot.Application;

public sealed class InboxFilter
{
    public int Page { get; set; } = 1;
    public Guid? RobotId { get; set; }
    public string? ChatType { get; set; }
    public string? MessageType { get; set; }
    public string? Search { get; set; }
    public void Normalize()
    {
        Page = Math.Max(1, Page);
        if (ChatType is not ("single" or "group")) ChatType = null;
        MessageType = string.IsNullOrWhiteSpace(MessageType) ? null : MessageType.Trim()[..Math.Min(MessageType.Trim().Length, 64)];
        Search = string.IsNullOrWhiteSpace(Search) ? null : Search.Trim()[..Math.Min(Search.Trim().Length, 200)];
    }
}
public interface IInboxService
{
    Task<PageResult<ReceivedMessage>> ListAsync(InboxFilter filter, CancellationToken ct);
    Task<ReceivedMessage?> FindAsync(Guid id, CancellationToken ct);
    Task<bool> StoreAsync(ReceivedMessage message, CancellationToken ct);
}
public interface IInboxRepository
{
    Task<PageResult<ReceivedMessage>> ListAsync(InboxFilter filter, CancellationToken ct);
    Task<ReceivedMessage?> FindAsync(Guid id, CancellationToken ct);
    Task<bool> InsertIfNewAsync(ReceivedMessage message, CancellationToken ct);
}
public sealed class InboxService(IInboxRepository repository) : IInboxService
{
    public Task<PageResult<ReceivedMessage>> ListAsync(InboxFilter filter, CancellationToken ct)
    { filter.Normalize(); return repository.ListAsync(filter, ct); }
    public Task<ReceivedMessage?> FindAsync(Guid id, CancellationToken ct) => repository.FindAsync(id, ct);
    public Task<bool> StoreAsync(ReceivedMessage message, CancellationToken ct) => repository.InsertIfNewAsync(message, ct);
}
public sealed record RobotConnectionState(Guid RobotId, string RobotName, string BotId, string State, string Description,
    DateTimeOffset? ConnectedAt = null, DateTimeOffset? LastMessageAt = null, DateTimeOffset? LastHeartbeatAt = null);
public interface IConnectionMonitor
{
    IReadOnlyList<RobotConnectionState> GetStates();
    void Reconnect(Guid robotId);
}
