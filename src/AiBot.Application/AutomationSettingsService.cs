using AiBot.Domain;

namespace AiBot.Application;

public sealed class AutomationSettingsService(IAutomationRepository repository, ISecretProtector secrets) : IAutomationSettingsService
{
    public Task<IReadOnlyList<KnowledgeEntry>> KnowledgeAsync(string? search, CancellationToken ct) => repository.KnowledgeAsync(search, ct);
    public async Task<KnowledgeInput?> FindKnowledgeAsync(Guid id, CancellationToken ct)
    {
        var x = await repository.KnowledgeByIdAsync(id, ct);
        return x is null ? null : new() { Id = x.Id, RobotId = x.RobotId, Question = x.Question, Keywords = x.Keywords, Answer = x.Answer, MatchMode = x.MatchMode, Priority = x.Priority, Enabled = x.Enabled };
    }
    public async Task SaveKnowledgeAsync(KnowledgeInput input, CancellationToken ct)
    {
        if (input.RobotId is { } robot && await repository.RobotAsync(robot, ct) is null) throw new ArgumentException("请选择有效的机器人。");
        var entry = input.Id is { } id ? await repository.KnowledgeByIdAsync(id, ct) ?? throw new ArgumentException("知识条目不存在。") : new KnowledgeEntry();
        entry.Update(input.RobotId, input.Question, input.Keywords, input.Answer, input.MatchMode, input.Priority, input.Enabled);
        if (input.Id is null) repository.Add(entry);
        await repository.SaveAsync(ct);
    }
    public async Task DeleteKnowledgeAsync(Guid id, CancellationToken ct)
    { var entry = await repository.KnowledgeByIdAsync(id, ct) ?? throw new ArgumentException("知识条目不存在。"); repository.Remove(entry); await repository.SaveAsync(ct); }
    private static AiModelInfo Map(AiModelEndpoint x) => new(x.Id, x.Name, x.Provider, x.Endpoint, x.ModelId, x.ApiStyle, x.ProtectedApiKey.Length > 0, x.Enabled, x.TimeoutSeconds, x.MaxOutputTokens);
    public async Task<IReadOnlyList<AiModelInfo>> ModelsAsync(CancellationToken ct) => (await repository.ModelsAsync(ct)).Select(Map).ToList();
    public async Task<AiModelInput?> FindModelAsync(Guid id, CancellationToken ct)
    {
        var x = await repository.ModelByIdAsync(id, ct);
        return x is null ? null : new() { Id = x.Id, Name = x.Name, Provider = x.Provider, Endpoint = x.Endpoint, ModelId = x.ModelId, ApiStyle = x.ApiStyle, Enabled = x.Enabled, TimeoutSeconds = x.TimeoutSeconds, MaxOutputTokens = x.MaxOutputTokens };
    }
    public async Task SaveModelAsync(AiModelInput input, CancellationToken ct)
    {
        if (input.ApiKey is { } raw && raw.Trim().Any(c => c < '!' || c > '~')) throw new ArgumentException("API Key 请使用不含空格或控制字符的 ASCII 密钥。");
        var model = input.Id is { } id ? await repository.ModelByIdAsync(id, ct) ?? throw new ArgumentException("模型接口不存在。") : new AiModelEndpoint();
        var key = string.IsNullOrWhiteSpace(input.ApiKey) ? null : secrets.Protect(input.ApiKey.Trim());
        model.Update(input.Name, input.Provider, input.Endpoint, input.ModelId, input.ApiStyle, key, input.Enabled, input.TimeoutSeconds, input.MaxOutputTokens);
        if (input.Id is null) repository.Add(model);
        await repository.SaveAsync(ct);
    }
    public async Task DeleteModelAsync(Guid id, CancellationToken ct)
    {
        if (await repository.ModelInUseAsync(id, ct)) throw new ArgumentException("该模型仍在机器人的优先级列表中，请先调整机器人配置；也可以先停用模型。");
        var model = await repository.ModelByIdAsync(id, ct) ?? throw new ArgumentException("模型接口不存在。"); repository.Remove(model); await repository.SaveAsync(ct);
    }
    public Task<AutoReplyLog?> ReplyForMessageAsync(Guid id, CancellationToken ct) => repository.ReplyForMessageAsync(id, ct);
}
