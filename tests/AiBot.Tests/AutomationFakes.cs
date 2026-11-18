using AiBot.Application;
using AiBot.Domain;

namespace AiBot.Tests;

public sealed class MemoryAutomationRepository(MemoryRepository robots) : IAutomationRepository
{
    public List<KnowledgeEntry> Entries { get; } = [];
    public List<AiModelEndpoint> Models { get; } = [];
    public List<AutoReplyLog> Replies { get; } = [];
    public Task<IReadOnlyList<KnowledgeEntry>> KnowledgeAsync(string? search, CancellationToken ct) => Task.FromResult<IReadOnlyList<KnowledgeEntry>>(Entries.Where(x => string.IsNullOrEmpty(search) || x.Question.Contains(search) || x.Keywords.Contains(search) || x.Answer.Contains(search)).ToList());
    public Task<KnowledgeEntry?> KnowledgeByIdAsync(Guid id, CancellationToken ct) => Task.FromResult(Entries.SingleOrDefault(x => x.Id == id));
    public Task<IReadOnlyList<AiModelEndpoint>> ModelsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<AiModelEndpoint>>(Models.ToList());
    public Task<AiModelEndpoint?> ModelByIdAsync(Guid id, CancellationToken ct) => Task.FromResult(Models.SingleOrDefault(x => x.Id == id));
    public Task<Robot?> RobotAsync(Guid id, CancellationToken ct) => Task.FromResult(robots.Robots.SingleOrDefault(x => x.Id == id));
    public Task<bool> ModelInUseAsync(Guid id, CancellationToken ct) => Task.FromResult(robots.Robots.Any(x => x.GetModelPriority().Contains(id)));
    public void Add(KnowledgeEntry entry) => Entries.Add(entry); public void Remove(KnowledgeEntry entry) => Entries.Remove(entry);
    public void Add(AiModelEndpoint model) => Models.Add(model); public void Remove(AiModelEndpoint model) => Models.Remove(model);
    public Task SaveAsync(CancellationToken ct) => Task.CompletedTask;
    public Task<bool> ClaimAsync(AutoReplyLog log, CancellationToken ct)
    { lock (Replies) { if (Replies.Any(x => x.ReceivedMessageId == log.ReceivedMessageId)) return Task.FromResult(false); Replies.Add(log); return Task.FromResult(true); } }
    public Task CompleteAsync(AutoReplyLog log, CancellationToken ct) => Task.CompletedTask;
    public Task<AutoReplyLog?> ReplyForMessageAsync(Guid id, CancellationToken ct) => Task.FromResult(Replies.SingleOrDefault(x => x.ReceivedMessageId == id));
}
public sealed class FakeAiClient : IAiCompletionClient
{
    public List<Guid> Calls { get; } = [];
    public Func<AiModelEndpoint, string> Generate { get; set; } = _ => "这是一条模拟 AI 回复。";
    public Task<string> CompleteAsync(AiModelEndpoint model, string prompt, string message, CancellationToken ct)
    { Calls.Add(model.Id); return Task.FromResult(Generate(model)); }
}
