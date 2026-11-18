using System.ComponentModel.DataAnnotations;
using AiBot.Domain;

namespace AiBot.Application;

public sealed record RobotInfo(Guid Id, string Name, string BotId, string Description, bool Enabled, DateTimeOffset UpdatedAt)
{
    public bool AutoReplyEnabled { get; init; }
    public string ReplyMode { get; init; } = "knowledge";
    public bool ReplyToGroups { get; init; } = true;
    public string ReplyPrompt { get; init; } = "";
    public IReadOnlyList<Guid> ModelPriority { get; init; } = [];
}
public sealed class RobotInput
{
    public Guid? Id { get; set; }
    [Required(ErrorMessage = "请填写机器人名称"), StringLength(80)] public string Name { get; set; } = "";
    [Required(ErrorMessage = "请填写 Bot ID"), StringLength(256)] public string BotId { get; set; } = "";
    [StringLength(512, MinimumLength = 1)] public string? Secret { get; set; }
    [StringLength(500), DisplayFormat(ConvertEmptyStringToNull = false)] public string Description { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public bool AutoReplyEnabled { get; set; }
    public string ReplyMode { get; set; } = "knowledge";
    public bool ReplyToGroups { get; set; } = true;
    [StringLength(2000), DisplayFormat(ConvertEmptyStringToNull = false)] public string ReplyPrompt { get; set; } = "";
    [MaxLength(8)] public List<Guid> ModelPriority { get; set; } = [];
}
public sealed class SendInput : IValidatableObject
{
    public Guid OperationId { get; set; } = Guid.NewGuid();
    public Guid RobotId { get; set; }
    [Required, RegularExpression("^(single|group)$", ErrorMessage = "请选择单聊或群聊")] public string TargetType { get; set; } = "single";
    [StringLength(256)] public string? UserId { get; set; }
    [StringLength(256)] public string? ChatId { get; set; }
    [Required(ErrorMessage = "请填写消息内容"), StringLength(4096)] public string Content { get; set; } = "";
    public string GetTargetId() => (TargetType == "group" ? ChatId : UserId)?.Trim() ?? "";
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (string.IsNullOrWhiteSpace(GetTargetId()))
            yield return new ValidationResult(TargetType == "group" ? "请填写群聊 Chat ID" : "请填写接收人 User ID", [TargetType == "group" ? nameof(ChatId) : nameof(UserId)]);
    }
}
public sealed record SendResult(bool Ok, string Stage, string Message, string? RequestId = null, int? Errcode = null, string? Errmsg = null);
public sealed record Dashboard(int Robots, int Enabled, int TodayMessages, int Accepted, IReadOnlyList<MessageLog> Recent);
public sealed record PageResult<T>(IReadOnlyList<T> Items, int Total, int Page, int PageSize)
{ public int Pages => Math.Max(1, (int)Math.Ceiling((double)Total / PageSize)); }
public interface IRobotService
{
    Task<IReadOnlyList<RobotInfo>> ListAsync(string? search, CancellationToken ct);
    Task<RobotInfo?> FindAsync(Guid id, CancellationToken ct);
    Task SaveAsync(RobotInput input, CancellationToken ct);
    Task DeleteAsync(Guid id, CancellationToken ct);
    Task<SendResult> SendAsync(SendInput input, string actor, CancellationToken ct);
    Task<Dashboard> DashboardAsync(CancellationToken ct);
    Task<PageResult<MessageLog>> LogsAsync(int page, string? search, CancellationToken ct);
}
public interface IRobotRepository
{
    Task<IReadOnlyList<Robot>> ListAsync(string? search, CancellationToken ct);
    Task<Robot?> FindAsync(Guid id, CancellationToken ct);
    Task<bool> BotIdExistsAsync(string botId, Guid? exceptId, CancellationToken ct);
    void Add(Robot robot);
    void Remove(Robot robot);
    Task SaveAsync(CancellationToken ct);
    Task<bool> TryStartMessageAsync(MessageLog log, CancellationToken ct);
    Task CompleteMessageAsync(MessageLog log, CancellationToken ct);
    Task<Dashboard> DashboardAsync(DateTimeOffset today, CancellationToken ct);
    Task<PageResult<MessageLog>> LogsAsync(int page, string? search, CancellationToken ct);
}
public interface ISecretProtector { string Protect(string value); string Unprotect(string value); }
public interface IWeComGateway { Task<SendResult> SendAsync(string botId, string secret, string targetType, string targetId, string content, CancellationToken ct); }
public interface IAccountService
{
    Task<AdminUser?> AuthenticateAsync(string username, string password, CancellationToken ct);
    Task<bool> ChangePasswordAsync(Guid id, string current, string next, CancellationToken ct);
}
