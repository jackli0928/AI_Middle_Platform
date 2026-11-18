using AiBot.Application;
using AiBot.Domain;

namespace AiBot.Tests;

public sealed class FakeConnections : IConnectionMonitor
{
    public List<RobotConnectionState> States { get; } = [];
    public Guid? Reconnected { get; private set; }
    public IReadOnlyList<RobotConnectionState> GetStates() => States;
    public void Reconnect(Guid id) => Reconnected = id;
}
public sealed class MemoryInboxRepository : IInboxRepository
{
    public List<ReceivedMessage> Items { get; } = [];
    public Task<bool> InsertIfNewAsync(ReceivedMessage message, CancellationToken ct)
    {
        if (Items.Any(x => x.RobotId == message.RobotId && x.MessageId == message.MessageId)) return Task.FromResult(false);
        Items.Add(message); return Task.FromResult(true);
    }
    public Task<ReceivedMessage?> FindAsync(Guid id, CancellationToken ct) => Task.FromResult(Items.SingleOrDefault(x => x.Id == id));
    public Task<PageResult<ReceivedMessage>> ListAsync(InboxFilter f, CancellationToken ct)
    {
        var all = Items.Where(x => (f.RobotId is null || x.RobotId == f.RobotId) && (f.ChatType is null || x.ChatType == f.ChatType)
            && (f.MessageType is null || x.MessageType == f.MessageType) && (f.Search is null || x.Content.Contains(f.Search) || x.ChatId.Contains(f.Search) || x.SenderUserId.Contains(f.Search) || x.RobotName.Contains(f.Search))).ToList();
        var page = Math.Clamp(f.Page, 1, Math.Max(1, (all.Count + 19) / 20));
        return Task.FromResult(new PageResult<ReceivedMessage>(all.Skip((page - 1) * 20).Take(20).ToList(), all.Count, page, 20));
    }
}
