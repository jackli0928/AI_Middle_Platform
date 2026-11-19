using System.Text.Json;

namespace AiBot.Domain;

public sealed class Robot
{
    public Guid Id { get; private set; } = Guid.NewGuid();
    public string Name { get; private set; } = "";
    public string BotId { get; private set; } = "";
    public string ProtectedSecret { get; private set; } = "";
    public string Description { get; private set; } = "";
    public bool Enabled { get; private set; } = true;
    public bool AutoReplyEnabled { get; private set; }
    public string ReplyMode { get; private set; } = "knowledge";
    public bool ReplyToGroups { get; private set; } = true;
    public string ReplyPrompt { get; private set; } = "";
    public string ModelPriorityJson { get; private set; } = "[]";
    public DateTimeOffset CreatedAt { get; private set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; private set; } = DateTimeOffset.UtcNow;
    private Robot() { }
    public IReadOnlyList<Guid> GetModelPriority() => JsonSerializer.Deserialize<List<Guid>>(ModelPriorityJson) ?? [];
    public void ConfigureAutoReply(bool enabled, string mode, bool replyToGroups, string prompt, IEnumerable<Guid> models)
    {
        if (mode is not ("knowledge" or "ai" or "quote")) throw new ArgumentException("请选择知识库匹配、AI 回复或 AI 报价。");
        if (prompt.Length > 2000) throw new ArgumentException("AI 提示词不能超过 2000 字。");
        var ids = models.Where(x => x != Guid.Empty).ToList();
        if (ids.Count > 8 || ids.Distinct().Count() != ids.Count) throw new ArgumentException("最多设置 8 个模型，且不能重复。");
        if (enabled && (mode is "ai" or "quote") && ids.Count == 0) throw new ArgumentException("启用 AI 回复或报价时请至少选择一个模型。");
        AutoReplyEnabled = enabled; ReplyMode = mode; ReplyToGroups = replyToGroups; ReplyPrompt = prompt.Trim();
        ModelPriorityJson = JsonSerializer.Serialize(ids); UpdatedAt = DateTimeOffset.UtcNow;
    }
    public static Robot Create(string name, string botId, string protectedSecret, string description, bool enabled)
    {
        var robot = new Robot();
        robot.Update(name, botId, protectedSecret, description, enabled);
        return robot;
    }
    public void Update(string name, string botId, string? protectedSecret, string description, bool enabled)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 80)
            throw new ArgumentException("机器人名称不能为空，且不能超过 80 字。");
        if (string.IsNullOrWhiteSpace(botId) || botId.Trim().Length > 256)
            throw new ArgumentException("Bot ID 不能为空，且不能超过 256 字。");
        if (description.Length > 500) throw new ArgumentException("备注不能超过 500 字。");
        if (string.IsNullOrEmpty(protectedSecret) && string.IsNullOrEmpty(ProtectedSecret))
            throw new ArgumentException("请填写机器人 Secret。");
        Name = name.Trim(); BotId = botId.Trim(); Description = description.Trim(); Enabled = enabled;
        if (!string.IsNullOrEmpty(protectedSecret)) ProtectedSecret = protectedSecret;
        UpdatedAt = DateTimeOffset.UtcNow;
    }
}
