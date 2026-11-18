namespace AiBot.Domain;

public sealed class AdminUser
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Username { get; set; } = "";
    public string NormalizedUsername { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public int FailedAttempts { get; set; }
    public DateTimeOffset? LockedUntil { get; set; }
}
