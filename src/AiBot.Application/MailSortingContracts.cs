using AiBot.Domain;

namespace AiBot.Application;

public static class MailSorting
{
    public const int MaxMailBytes = 12 * 1024 * 1024;
    public const int PageSize = 30;
    public static IReadOnlyDictionary<string, string> Categories { get; } = new Dictionary<string, string>
    {
        ["request"] = "订舱申请", ["confirmation"] = "订舱确认", ["documents"] = "文件检查",
        ["exception"] = "异常与变更", ["review"] = "待分拣"
    };
}

public interface IMailSorting
{
    Task<IReadOnlyList<MailListItem>> ListAsync(string search, string category, string status, int page, CancellationToken ct);
    Task<SortedMail?> FindAsync(Guid id, CancellationToken ct);
    Task<IReadOnlyList<MailAudit>> AuditAsync(Guid id, CancellationToken ct);
    Task<Guid> ImportAsync(byte[] raw, string source, string actor, string? category, CancellationToken ct);
    Task<bool> ReviewAsync(SortedMail mail, MailReview review, string actor, CancellationToken ct);
    Task<bool> ReanalyzeAsync(SortedMail mail, string actor, CancellationToken ct);
    Task<byte[]?> OriginalAsync(Guid id, CancellationToken ct);
    Task<(byte[] Data, string Name)?> AttachmentAsync(Guid id, int index, CancellationToken ct);
}

public sealed class InvalidMailException(string message) : Exception(message);
