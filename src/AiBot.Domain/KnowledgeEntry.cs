using System.Text;

namespace AiBot.Domain;

public sealed class KnowledgeEntry
{
    public Guid Id { get; private set; } = Guid.NewGuid();
    public Guid? RobotId { get; private set; }
    public string Question { get; private set; } = "";
    public string Keywords { get; private set; } = "";
    public string Answer { get; private set; } = "";
    public string MatchMode { get; private set; } = "exact";
    public int Priority { get; private set; } = 100;
    public bool Enabled { get; private set; } = true;
    public DateTimeOffset UpdatedAt { get; private set; } = DateTimeOffset.UtcNow;
    public void Update(Guid? robotId, string question, string keywords, string answer, string matchMode, int priority, bool enabled)
    {
        if (string.IsNullOrWhiteSpace(question) || question.Trim().Length > 300) throw new ArgumentException("问题不能为空，且最多 300 字。");
        if (keywords.Length > 2000) throw new ArgumentException("匹配词不能超过 2000 字。");
        if (string.IsNullOrWhiteSpace(answer) || Encoding.UTF8.GetByteCount(answer) > 4096) throw new ArgumentException("知识库答案不能为空，且最多 4096 个 UTF-8 字节。");
        if (matchMode is not ("exact" or "contains") || priority is < 0 or > 9999) throw new ArgumentException("匹配方式或优先级无效。");
        RobotId = robotId; Question = question.Trim(); Keywords = keywords.Trim(); Answer = answer.Trim(); MatchMode = matchMode; Priority = priority; Enabled = enabled; UpdatedAt = DateTimeOffset.UtcNow;
    }
}
