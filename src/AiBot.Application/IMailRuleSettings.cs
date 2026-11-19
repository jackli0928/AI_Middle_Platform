namespace AiBot.Application;

public sealed record MailRuleSettings(MailRuleSnapshot Snapshot, int Revision);

public interface IMailRuleSettings
{
    Task<MailRuleSettings> ReadAsync(CancellationToken ct);
    Task<bool> SaveAsync(MailRuleSnapshot snapshot, int expectedRevision, CancellationToken ct);
}
