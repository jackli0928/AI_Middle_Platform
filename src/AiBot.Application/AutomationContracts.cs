using System.ComponentModel.DataAnnotations;
using AiBot.Domain;

namespace AiBot.Application;

public sealed class KnowledgeInput
{
    public Guid? Id { get; set; }
    public Guid? RobotId { get; set; }
    [Required, StringLength(300)] public string Question { get; set; } = "";
    [StringLength(2000), DisplayFormat(ConvertEmptyStringToNull = false)] public string Keywords { get; set; } = "";
    [Required, StringLength(4096)] public string Answer { get; set; } = "";
    [Required, RegularExpression("^(exact|contains)$")] public string MatchMode { get; set; } = "exact";
    [Range(0,9999)] public int Priority { get; set; } = 100;
    public bool Enabled { get; set; } = true;
}
public sealed class AiModelInput
{
    public Guid? Id { get; set; }
    [Required, StringLength(80)] public string Name { get; set; } = "";
    [Required, StringLength(80)] public string Provider { get; set; } = "";
    [Required, StringLength(1024)] public string Endpoint { get; set; } = "";
    [Required, StringLength(256)] public string ModelId { get; set; } = "";
    [Required, RegularExpression("^(responses|chat)$")] public string ApiStyle { get; set; } = "responses";
    [StringLength(4096)] public string? ApiKey { get; set; }
    public bool Enabled { get; set; }
    [Range(5,30)] public int TimeoutSeconds { get; set; } = 15;
    [Range(64,4096)] public int MaxOutputTokens { get; set; } = 1024;
}
public sealed record AiModelInfo(Guid Id, string Name, string Provider, string Endpoint, string ModelId, string ApiStyle, bool HasApiKey, bool Enabled, int TimeoutSeconds, int MaxOutputTokens);
public interface IAutomationRepository
{
    Task<IReadOnlyList<KnowledgeEntry>> KnowledgeAsync(string? search, CancellationToken ct);
    Task<KnowledgeEntry?> KnowledgeByIdAsync(Guid id, CancellationToken ct);
    Task<IReadOnlyList<AiModelEndpoint>> ModelsAsync(CancellationToken ct);
    Task<AiModelEndpoint?> ModelByIdAsync(Guid id, CancellationToken ct);
    Task<Robot?> RobotAsync(Guid id, CancellationToken ct);
    Task<bool> ModelInUseAsync(Guid id, CancellationToken ct);
    void Add(KnowledgeEntry entry); void Remove(KnowledgeEntry entry);
    void Add(AiModelEndpoint model); void Remove(AiModelEndpoint model);
    Task SaveAsync(CancellationToken ct);
    Task<bool> ClaimAsync(AutoReplyLog log, CancellationToken ct);
    Task CompleteAsync(AutoReplyLog log, CancellationToken ct);
    Task<AutoReplyLog?> ReplyForMessageAsync(Guid messageId, CancellationToken ct);
}
public interface IAutomationSettingsService
{
    Task<IReadOnlyList<KnowledgeEntry>> KnowledgeAsync(string? search, CancellationToken ct);
    Task<KnowledgeInput?> FindKnowledgeAsync(Guid id, CancellationToken ct);
    Task SaveKnowledgeAsync(KnowledgeInput input, CancellationToken ct);
    Task DeleteKnowledgeAsync(Guid id, CancellationToken ct);
    Task<IReadOnlyList<AiModelInfo>> ModelsAsync(CancellationToken ct);
    Task<AiModelInput?> FindModelAsync(Guid id, CancellationToken ct);
    Task SaveModelAsync(AiModelInput input, CancellationToken ct);
    Task DeleteModelAsync(Guid id, CancellationToken ct);
    Task<AutoReplyLog?> ReplyForMessageAsync(Guid id, CancellationToken ct);
}
public interface IMailAiCompletionClient
{
    Task<string> CompleteMailAsync(AiModelEndpoint model, string prompt, string data, CancellationToken ct);
}
public interface IAiCompletionClient { Task<string> CompleteAsync(AiModelEndpoint model, string prompt, string message, CancellationToken ct); }
public interface IAutoReplyService { Task HandleAsync(Guid robotId, string connectionBotId, ReceivedMessage message, CancellationToken ct); }
public sealed class AiCompletionException(string message) : Exception(message);
