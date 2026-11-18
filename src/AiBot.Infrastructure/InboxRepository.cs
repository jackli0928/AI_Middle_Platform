using AiBot.Application;
using AiBot.Domain;
using Microsoft.EntityFrameworkCore;

namespace AiBot.Infrastructure;

public sealed class InboxRepository(BotDbContext db) : IInboxRepository
{
    public Task<ReceivedMessage?> FindAsync(Guid id, CancellationToken ct) => db.ReceivedMessages.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
    public async Task<PageResult<ReceivedMessage>> ListAsync(InboxFilter filter, CancellationToken ct)
    {
        var query = db.ReceivedMessages.AsNoTracking();
        if (filter.RobotId is { } id) query = query.Where(x => x.RobotId == id);
        if (filter.ChatType is { } chat) query = query.Where(x => x.ChatType == chat);
        if (filter.MessageType is { } type) query = query.Where(x => x.MessageType == type);
        if (filter.Search is { } search) query = query.Where(x => x.RobotName.Contains(search) || x.SenderUserId.Contains(search) || x.ChatId.Contains(search) || x.Content.Contains(search));
        var total = await query.CountAsync(ct);
        var page = Math.Clamp(filter.Page, 1, Math.Max(1, (total + 19) / 20));
        var rows = await query.OrderByDescending(x => x.ReceivedAt).ThenByDescending(x => x.Id).Skip((page - 1) * 20).Take(20).ToListAsync(ct);
        return new(rows, total, page, 20);
    }
    public async Task<bool> InsertIfNewAsync(ReceivedMessage m, CancellationToken ct)
    {
        // The database unique index handles retries and concurrent duplicate callbacks atomically.
        var count = await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO ai_bot.received_messages
            ("Id", "RobotId", "RobotName", "MessageId", "RequestId", "SenderUserId", "ChatId", "ChatType", "MessageType", "Content", "PayloadJson", "SentAt", "ReceivedAt")
            VALUES ({m.Id}, {m.RobotId}, {m.RobotName}, {m.MessageId}, {m.RequestId}, {m.SenderUserId}, {m.ChatId}, {m.ChatType}, {m.MessageType}, {m.Content}, {m.PayloadJson}, {m.SentAt}, {m.ReceivedAt})
            ON CONFLICT ("RobotId", "MessageId") DO NOTHING
            """, ct);
        return count == 1;
    }
}
