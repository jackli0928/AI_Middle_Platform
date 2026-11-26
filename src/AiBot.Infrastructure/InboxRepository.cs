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
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var count = await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO ai_bot.received_messages
            ("Id", "RobotId", "RobotName", "MessageId", "RequestId", "SenderUserId", "ChatId", "ChatType", "MessageType", "Content", "PayloadJson", "SentAt", "ReceivedAt")
            VALUES ({m.Id}, {m.RobotId}, {m.RobotName}, {m.MessageId}, {m.RequestId}, {m.SenderUserId}, {m.ChatId}, {m.ChatType}, {m.MessageType}, {m.Content}, {m.PayloadJson}, {m.SentAt}, {m.ReceivedAt})
            ON CONFLICT ("RobotId", "MessageId") DO NOTHING
            """, ct);
        if (count == 1 && m.MessageType == "text")
        {
            var directQuote = QuoteInquiryParser.IsCompleteInquiry(m.Content);
            if (m.ChatType == "single" && MessageSender.IsHuman(m))
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO ai_bot.tracking_commands ("Id", "RobotId", "SenderUserId", "Status", "Result", "Intent", "CreatedAt")
                    SELECT {m.Id}, r."Id", {m.SenderUserId}, 'queued', '', '', {m.ReceivedAt}
                    FROM ai_bot.robots r
                    WHERE r."Id" = {m.RobotId} AND r."Enabled" AND r."TrackingModelId" IS NOT NULL
                        AND NOT ({directQuote} AND r."AutoReplyEnabled" AND r."ReplyMode" = 'quote')
                    """, ct);
            var logId = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO ai_bot.auto_reply_logs
                ("Id", "ReceivedMessageId", "RobotId", "Mode", "Status", "ModelName", "ReplyContent", "Result", "Attempts", "CreatedAt", "CompletedAt")
                SELECT {logId}, {m.Id}, {m.RobotId}, 'quote', 'queued', '', '', '报价任务排队中。', '', {now}, NULL
                FROM ai_bot.robots r
                WHERE r."Id" = {m.RobotId} AND r."Enabled" AND r."AutoReplyEnabled" AND r."ReplyMode" = 'quote'
                    AND ({m.ChatType} = 'single' OR r."ReplyToGroups")
                    AND NOT EXISTS (SELECT 1 FROM ai_bot.tracking_commands t WHERE t."Id" = {m.Id})
                ON CONFLICT ("ReceivedMessageId") DO NOTHING
                """, ct);
        }
        await transaction.CommitAsync(ct);
        return count == 1;
    }
}
