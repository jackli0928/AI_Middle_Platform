using System.ComponentModel.DataAnnotations;

namespace AiBot.Application;

public sealed class MailboxSettings
{
    public Guid? Id { get; set; }
    [Required, StringLength(100)] public string Name { get; set; } = "";
    [Required, RegularExpression("^(imap|pop3)$")] public string Protocol { get; set; } = "imap";
    [Required, StringLength(253)] public string Host { get; set; } = "";
    [Range(0, 65535)] public int Port { get; set; }
    [Required, StringLength(320)] public string Username { get; set; } = "";
    [EmailAddress, StringLength(320)] public string? Address { get; set; }
    [StringLength(256)] public string? Folder { get; set; } = "INBOX";
    public string? Category { get; set; }
    public bool Enabled { get; set; } = true;
    public bool ImportRecent { get; set; } = true;
    [StringLength(4096)] public string? Password { get; set; }
    public bool HasPassword { get; set; }

    public SortingMailbox ToMailbox() => new()
    {
        Name = Name.Trim(), Protocol = Protocol, Host = Host.Trim().ToLowerInvariant(), Port = Port,
        Username = Username.Trim(), Address = (Address ?? "").Trim(), Folder = Protocol == "imap" ? (Folder ?? "").Trim() : "",
        Category = Category ?? "", Enabled = Enabled, ImportRecent = ImportRecent
    };
}

public interface IMailboxSettings
{
    Task<IReadOnlyList<MailboxSettings>> ListAsync(CancellationToken ct);
    Task SaveAsync(MailboxSettings input, CancellationToken ct);
}
