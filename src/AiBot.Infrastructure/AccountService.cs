using AiBot.Application;
using AiBot.Domain;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace AiBot.Infrastructure;

public sealed class SecretProtector(IDataProtectionProvider provider) : ISecretProtector
{
    private readonly IDataProtector protector = provider.CreateProtector("AiBot.RobotSecrets.v1");
    public string Protect(string value) => protector.Protect(value);
    public string Unprotect(string value) => protector.Unprotect(value);
}

public sealed class AccountService(BotDbContext db, IPasswordHasher<AdminUser> hasher) : IAccountService
{
    public async Task<AdminUser?> AuthenticateAsync(string username, string password, CancellationToken ct)
    {
        var normalized = username.Trim().ToUpperInvariant();
        // Serialize attempts for one account across application instances.
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtext({normalized}))", ct);
        var user = await db.Users.SingleOrDefaultAsync(x => x.NormalizedUsername == normalized, ct);
        if (user is null)
        { hasher.VerifyHashedPassword(new AdminUser(), DummyHash, password); return null; }
        if (user.LockedUntil > DateTimeOffset.UtcNow) return null;
        var verified = hasher.VerifyHashedPassword(user, user.PasswordHash, password);
        if (verified == PasswordVerificationResult.Failed)
        {
            if (++user.FailedAttempts >= 5) { user.LockedUntil = DateTimeOffset.UtcNow.AddMinutes(15); user.FailedAttempts = 0; }
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return null;
        }
        user.FailedAttempts = 0; user.LockedUntil = null;
        if (verified == PasswordVerificationResult.SuccessRehashNeeded) user.PasswordHash = hasher.HashPassword(user, password);
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return user;
    }
    private static readonly string DummyHash = new PasswordHasher<AdminUser>().HashPassword(new AdminUser(), Guid.NewGuid().ToString());
    public async Task<bool> ChangePasswordAsync(Guid id, string current, string next, CancellationToken ct)
    {
        var user = await db.Users.FindAsync([id], ct);
        if (user is null || hasher.VerifyHashedPassword(user, user.PasswordHash, current) == PasswordVerificationResult.Failed) return false;
        user.PasswordHash = hasher.HashPassword(user, next); await db.SaveChangesAsync(ct); return true;
    }
}
