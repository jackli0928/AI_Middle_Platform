using AiBot.Application;
using AiBot.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AiBot.Infrastructure;

public sealed class AutomationRepository(BotDbContext db) : IAutomationRepository
{
    public async Task<IReadOnlyList<KnowledgeEntry>> KnowledgeAsync(string? search, CancellationToken ct)
    {
        var query = db.KnowledgeEntries.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search)) query = query.Where(x => x.Question.Contains(search) || x.Keywords.Contains(search) || x.Answer.Contains(search));
        return await query.OrderBy(x => x.Priority).ThenByDescending(x => x.UpdatedAt).ToListAsync(ct);
    }
    public Task<KnowledgeEntry?> KnowledgeByIdAsync(Guid id, CancellationToken ct) => db.KnowledgeEntries.SingleOrDefaultAsync(x => x.Id == id, ct);
    public async Task<IReadOnlyList<AiModelEndpoint>> ModelsAsync(CancellationToken ct) => await db.AiModels.AsNoTracking().OrderBy(x => x.Name).ToListAsync(ct);
    public Task<AiModelEndpoint?> ModelByIdAsync(Guid id, CancellationToken ct) => db.AiModels.SingleOrDefaultAsync(x => x.Id == id, ct);
    public Task<Robot?> RobotAsync(Guid id, CancellationToken ct) => db.Robots.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
    public async Task<bool> ModelInUseAsync(Guid id, CancellationToken ct)
    { var configs = await db.Robots.AsNoTracking().ToListAsync(ct); return configs.Any(x => x.GetModelPriority().Contains(id) || x.TrackingModelId == id); }
    public void Add(KnowledgeEntry x) => db.KnowledgeEntries.Add(x);
    public void Remove(KnowledgeEntry x) => db.KnowledgeEntries.Remove(x);
    public void Add(AiModelEndpoint x) => db.AiModels.Add(x);
    public void Remove(AiModelEndpoint x) => db.AiModels.Remove(x);
    public async Task SaveAsync(CancellationToken ct) => await db.SaveChangesAsync(ct);
    public async Task<bool> ClaimAsync(AutoReplyLog log, CancellationToken ct)
    {
        db.AutoReplies.Add(log);
        try { await db.SaveChangesAsync(ct); return true; }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        { db.Entry(log).State = EntityState.Detached; return false; }
    }
    public async Task CompleteAsync(AutoReplyLog log, CancellationToken ct) => await db.SaveChangesAsync(ct);
    public Task<AutoReplyLog?> ReplyForMessageAsync(Guid id, CancellationToken ct) => db.AutoReplies.AsNoTracking().SingleOrDefaultAsync(x => x.ReceivedMessageId == id, ct);
}
