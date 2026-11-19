namespace AiBot.Domain;

public sealed record MailCandidate(string Value, string Source, string Evidence);
public sealed record MailField(string Key, string Label, IReadOnlyList<MailCandidate> Candidates,
    bool RequiresReview = false, string? ReviewEvidence = null)
{
    public bool HasConflict => RequiresReview || Candidates.Select(c => c.Value)
        .Distinct(StringComparer.OrdinalIgnoreCase).Skip(1).Any();
}
public sealed record MailAttachment(string Name, string Preview, string? Note);
public sealed record MailAnalysis(string Category, string Body, IReadOnlyList<MailField> Fields,
    IReadOnlyList<MailAttachment> Attachments, string Engine, IReadOnlyList<string> References);
public sealed record MailReview(string Category, Dictionary<string, string> Fields, bool Confirmed);
public sealed record SortedMail(Guid Id, string Fingerprint, string Subject, string Sender, string Source,
    DateTimeOffset ReceivedAt, int Revision, MailAnalysis Analysis, MailReview? Review);
public sealed record MailListItem(Guid Id, string Subject, string Sender, string Source, string Category,
    bool Confirmed, DateTimeOffset ReceivedAt);
public sealed record MailAudit(string Action, string Operator, DateTimeOffset At);
