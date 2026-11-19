namespace AiBot.Application;

public interface IMailboxCredentials
{
    Task<bool> HasPasswordAsync(string mailbox, CancellationToken ct);
    Task SavePasswordAsync(string mailbox, string password, CancellationToken ct);
}

public static class SortingMailboxIdentity
{
    public static string Key(SortingMailbox mailbox)
    {
        var pop = mailbox.Protocol.Equals("pop3", StringComparison.OrdinalIgnoreCase);
        return System.Text.Json.JsonSerializer.Serialize(new[]
        {
            pop ? "pop3" : "imap", mailbox.Host.Trim().ToLowerInvariant(),
            (mailbox.Port > 0 ? mailbox.Port : pop ? 995 : 993).ToString(System.Globalization.CultureInfo.InvariantCulture),
            mailbox.Username, pop ? "" : mailbox.Folder
        });
    }
}
