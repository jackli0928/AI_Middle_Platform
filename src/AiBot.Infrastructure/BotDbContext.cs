using AiBot.Domain;
using Microsoft.EntityFrameworkCore;

namespace AiBot.Infrastructure;

public sealed class BotDbContext(DbContextOptions<BotDbContext> options) : DbContext(options)
{
    public DbSet<Robot> Robots => Set<Robot>();
    public DbSet<MessageLog> Messages => Set<MessageLog>();
    public DbSet<AdminUser> Users => Set<AdminUser>();
    public DbSet<ReceivedMessage> ReceivedMessages => Set<ReceivedMessage>();
    public DbSet<KnowledgeEntry> KnowledgeEntries => Set<KnowledgeEntry>();
    public DbSet<AiModelEndpoint> AiModels => Set<AiModelEndpoint>();
    public DbSet<AutoReplyLog> AutoReplies => Set<AutoReplyLog>();
    public DbSet<QuoteDraft> QuoteDrafts => Set<QuoteDraft>();
    public DbSet<TrackingCommand> TrackingCommands => Set<TrackingCommand>();
    public DbSet<TrackingSubscription> TrackingSubscriptions => Set<TrackingSubscription>();
    public DbSet<TrackingPush> TrackingPushes => Set<TrackingPush>();
    public DbSet<TrackingRefresh> TrackingRefreshes => Set<TrackingRefresh>();
    protected override void OnModelCreating(ModelBuilder model)
    {
        model.HasDefaultSchema("ai_bot");
        model.Entity<TrackingCommand>(b =>
        {
            b.ToTable("tracking_commands");
            b.HasKey(x => x.Id);
            b.HasIndex(x => new { x.Status, x.CreatedAt });
            b.HasIndex(x => new { x.RobotId, x.SenderUserId, x.CreatedAt });
        });
        model.Entity<TrackingSubscription>(b =>
        {
            b.ToTable("tracking_subscriptions");
            b.HasKey(x => x.Id);
            b.HasIndex(x => new { x.Awb, x.Email, x.Type }).IsUnique();
            b.HasIndex(x => new { x.Status, x.Awb });
        });
        model.Entity<TrackingPush>(b =>
        {
            b.ToTable("tracking_pushes");
            b.HasKey(x => new { x.Awb, x.PushTime, x.Node });
        });
        model.Entity<TrackingRefresh>(b =>
        {
            b.ToTable("tracking_refreshes");
            b.HasKey(x => x.Awb);
        });
        model.Entity<KnowledgeEntry>(b => {
            b.ToTable("knowledge_entries"); b.HasKey(x => x.Id);
            b.Property(x => x.Question).HasMaxLength(300); b.Property(x => x.Keywords).HasMaxLength(2000);
            b.Property(x => x.Answer).HasMaxLength(4096); b.Property(x => x.MatchMode).HasMaxLength(10);
            b.HasIndex(x => new { x.RobotId, x.Enabled, x.Priority });
        });
        model.Entity<AiModelEndpoint>(b => {
            b.ToTable("ai_models"); b.HasKey(x => x.Id);
            b.Property(x => x.Name).HasMaxLength(80); b.Property(x => x.Provider).HasMaxLength(80);
            b.Property(x => x.Endpoint).HasMaxLength(1024); b.Property(x => x.ModelId).HasMaxLength(256);
            b.Property(x => x.ApiStyle).HasMaxLength(20); b.Property(x => x.ProtectedApiKey).HasMaxLength(8192);
        });
        model.Entity<AutoReplyLog>(b => {
            b.ToTable("auto_reply_logs"); b.HasKey(x => x.Id);
            b.Property(x => x.Mode).HasMaxLength(10); b.Property(x => x.Status).HasMaxLength(20);
            b.Property(x => x.ModelName).HasMaxLength(150); b.Property(x => x.ReplyContent).HasMaxLength(4096);
            b.Property(x => x.Result).HasMaxLength(2048); b.Property(x => x.Attempts).HasMaxLength(4000);
            b.HasIndex(x => x.ReceivedMessageId).IsUnique(); b.HasIndex(x => x.RobotId);
        });
        model.Entity<QuoteDraft>(b => {
            b.ToTable("quote_drafts"); b.HasKey(x => x.Id);
            b.Property(x => x.ChatType).HasMaxLength(10); b.Property(x => x.ChatId).HasMaxLength(256);
            b.Property(x => x.SenderUserId).HasMaxLength(256); b.Property(x => x.Status).HasMaxLength(20);
            b.Property(x => x.StateJson).HasColumnType("text"); b.Property(x => x.RuleVersion).HasMaxLength(40);
            b.HasIndex(x => new { x.RobotId, x.ChatType, x.ChatId, x.SenderUserId, x.Status });
        });
        model.Entity<ReceivedMessage>(b => {
            b.ToTable("received_messages"); b.HasKey(x => x.Id);
            b.Property(x => x.RobotName).HasMaxLength(80); b.Property(x => x.MessageId).HasMaxLength(256);
            b.Property(x => x.RequestId).HasMaxLength(256); b.Property(x => x.SenderUserId).HasMaxLength(256);
            b.Property(x => x.ChatId).HasMaxLength(256); b.Property(x => x.ChatType).HasMaxLength(10);
            b.Property(x => x.MessageType).HasMaxLength(64); b.Property(x => x.Content).HasColumnType("text");
            b.Property(x => x.PayloadJson).HasColumnType("text");
            b.HasIndex(x => new { x.RobotId, x.MessageId }).IsUnique(); b.HasIndex(x => x.ReceivedAt);
            b.HasIndex(x => x.ChatId); b.HasIndex(x => x.SenderUserId);
        });
        model.Entity<Robot>(b => {
            b.ToTable("robots"); b.HasKey(x => x.Id);
            b.Property(x => x.Name).HasMaxLength(80); b.Property(x => x.BotId).HasMaxLength(256);
            b.Property(x => x.ProtectedSecret).HasMaxLength(2048); b.Property(x => x.Description).HasMaxLength(500);
            b.HasIndex(x => x.BotId).IsUnique();
            b.Property(x => x.AutoReplyEnabled).HasDefaultValue(false);
            b.Property(x => x.ReplyMode).HasMaxLength(10).HasDefaultValue("knowledge");
            b.Property(x => x.ReplyToGroups).HasDefaultValue(true).HasSentinel(true);
            b.Property(x => x.ReplyPrompt).HasMaxLength(2000).HasDefaultValue("");
            b.Property(x => x.ModelPriorityJson).HasColumnType("text").HasDefaultValue("[]");
        });
        model.Entity<MessageLog>(b => {
            b.ToTable("message_logs"); b.HasKey(x => x.Id);
            b.Property(x => x.RobotName).HasMaxLength(80); b.Property(x => x.UserId).HasMaxLength(256);
            b.Property(x => x.ChatId).HasMaxLength(256).HasDefaultValue("");
            b.Property(x => x.TargetType).HasMaxLength(10).HasDefaultValue("single");
            b.Ignore(x => x.TargetId);
            b.Property(x => x.Content).HasMaxLength(4096); b.Property(x => x.Operator).HasMaxLength(80);
            b.Property(x => x.Result).HasMaxLength(2048); b.Property(x => x.RequestId).HasMaxLength(128);
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            b.HasIndex(x => x.CreatedAt);
            // Audit snapshots survive deletion of a robot.
        });
        model.Entity<AdminUser>(b => {
            b.ToTable("admin_users"); b.HasKey(x => x.Id);
            b.Property(x => x.Username).HasMaxLength(80); b.Property(x => x.NormalizedUsername).HasMaxLength(80);
            b.Property(x => x.PasswordHash).HasMaxLength(512); b.HasIndex(x => x.NormalizedUsername).IsUnique();
        });
    }
}
