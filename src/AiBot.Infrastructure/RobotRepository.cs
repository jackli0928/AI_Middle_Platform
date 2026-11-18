using AiBot.Application;
using AiBot.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AiBot.Infrastructure;

public sealed class RobotRepository(BotDbContext db) : IRobotRepository
{
    public async Task<IReadOnlyList<Robot>> ListAsync(string? search, CancellationToken ct)
    {
        var query = db.Robots.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search)) query = query.Where(x => x.Name.Contains(search) || x.BotId.Contains(search));
        return await query.OrderByDescending(x => x.CreatedAt).ToListAsync(ct);
    }
    public Task<Robot?> FindAsync(Guid id, CancellationToken ct) => db.Robots.SingleOrDefaultAsync(x => x.Id == id, ct);
    public Task<bool> BotIdExistsAsync(string botId, Guid? exceptId, CancellationToken ct) => db.Robots.AnyAsync(x => x.BotId == botId && x.Id != exceptId, ct);
    public void Add(Robot robot) => db.Robots.Add(robot);
    public void Remove(Robot robot) => db.Robots.Remove(robot);
    public async Task SaveAsync(CancellationToken ct)
    {
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        { throw new ArgumentException("该 Bot ID 已存在，请勿重复添加。"); }
    }
    public async Task<bool> TryStartMessageAsync(MessageLog log, CancellationToken ct)
    {
        db.Messages.Add(log);
        try { await db.SaveChangesAsync(ct); return true; }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        { db.Entry(log).State = EntityState.Detached; return false; }
    }
    public async Task CompleteMessageAsync(MessageLog log, CancellationToken ct) => await db.SaveChangesAsync(ct);
    public async Task<Dashboard> DashboardAsync(DateTimeOffset today, CancellationToken ct)
    {
        var count = await db.Robots.CountAsync(ct);
        var enabled = await db.Robots.CountAsync(x => x.Enabled, ct);
        var messages = await db.Messages.CountAsync(x => x.CreatedAt >= today, ct);
        var accepted = await db.Messages.CountAsync(x => x.CreatedAt >= today && x.Status == DeliveryStatus.Accepted, ct);
        var recent = await db.Messages.AsNoTracking().OrderByDescending(x => x.CreatedAt).Take(6).ToListAsync(ct);
        return new(count, enabled, messages, accepted, recent);
    }
    public async Task<PageResult<MessageLog>> LogsAsync(int page, string? search, CancellationToken ct)
    {
        var query = db.Messages.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search)) query = query.Where(x => x.RobotName.Contains(search) || x.UserId.Contains(search) || x.ChatId.Contains(search));
        var total = await query.CountAsync(ct);
        page = Math.Clamp(page, 1, Math.Max(1, (total + 19) / 20));
        var rows = await query.OrderByDescending(x => x.CreatedAt).Skip((page - 1) * 20).Take(20).ToListAsync(ct);
        return new(rows, total, page, 20);
    }
}
