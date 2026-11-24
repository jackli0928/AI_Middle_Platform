using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

namespace AiBot.Infrastructure.Migrations;

[DbContext(typeof(BotDbContext))]
public sealed class BotDbContextModelSnapshot : ModelSnapshot
{
    protected override void BuildModel(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("ai_bot").HasAnnotation("ProductVersion", "10.0.0").HasAnnotation("Relational:MaxIdentifierLength", 63);
        modelBuilder.HasAnnotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn);
        modelBuilder.Entity("AiBot.Domain.TrackingCommand", b =>
        {
            b.Property<Guid>("Id").ValueGeneratedOnAdd().HasColumnType("uuid");
            b.Property<Guid>("RobotId").HasColumnType("uuid");
            b.Property<string>("SenderUserId").IsRequired().HasColumnType("text");
            b.Property<string>("Status").IsRequired().HasColumnType("text");
            b.Property<string>("Result").IsRequired().HasColumnType("text");
            b.Property<string>("Intent").IsRequired().HasColumnType("text");
            b.Property<DateTimeOffset>("CreatedAt").HasColumnType("timestamp with time zone");
            b.Property<DateTimeOffset?>("StartedAt").HasColumnType("timestamp with time zone");
            b.Property<DateTimeOffset?>("CompletedAt").HasColumnType("timestamp with time zone");
            b.HasKey("Id"); b.HasIndex("Status", "CreatedAt"); b.HasIndex("RobotId", "SenderUserId", "CreatedAt");
            b.ToTable("tracking_commands", "ai_bot");
        });
        modelBuilder.Entity("AiBot.Domain.TrackingSubscription", b =>
        {
            b.Property<Guid>("Id").ValueGeneratedOnAdd().HasColumnType("uuid");
            b.Property<Guid>("RobotId").HasColumnType("uuid");
            b.Property<string>("SenderUserId").IsRequired().HasColumnType("text");
            b.Property<string>("Awb").IsRequired().HasColumnType("text");
            b.Property<string>("Email").IsRequired().HasColumnType("text");
            b.Property<string>("Type").IsRequired().HasColumnType("text");
            b.Property<string>("Status").IsRequired().HasColumnType("text");
            b.Property<DateTimeOffset>("UpdatedAt").HasColumnType("timestamp with time zone");
            b.HasKey("Id"); b.HasIndex("Awb", "Email", "Type").IsUnique(); b.HasIndex("Status", "Awb");
            b.ToTable("tracking_subscriptions", "ai_bot");
        });
        modelBuilder.Entity("AiBot.Domain.TrackingPush", b =>
        {
            b.Property<string>("Awb").HasColumnType("text");
            b.Property<string>("Node").HasColumnType("text");
            b.Property<string>("PushTime").HasColumnType("text");
            b.Property<string>("QueryAddress").HasColumnType("text");
            b.Property<DateTimeOffset>("DetectedAt").HasColumnType("timestamp with time zone");
            b.HasKey("Awb", "PushTime", "Node");
            b.ToTable("tracking_pushes", "ai_bot");
        });
        modelBuilder.Entity("AiBot.Domain.TrackingRefresh", b =>
        {
            b.Property<string>("Awb").HasColumnType("text");
            b.Property<DateTimeOffset>("QueriedAt").HasColumnType("timestamp with time zone");
            b.Property<string>("Result").IsRequired().HasColumnType("text");
            b.HasKey("Awb");
            b.ToTable("tracking_refreshes", "ai_bot");
        });
        modelBuilder.Entity("AiBot.Domain.KnowledgeEntry", b => {
            b.Property<Guid>("Id").ValueGeneratedOnAdd().HasColumnType("uuid"); b.Property<Guid?>("RobotId").HasColumnType("uuid");
            b.Property<string>("Question").IsRequired().HasMaxLength(300).HasColumnType("character varying(300)");
            b.Property<string>("Keywords").IsRequired().HasMaxLength(2000).HasColumnType("character varying(2000)");
            b.Property<string>("Answer").IsRequired().HasMaxLength(4096).HasColumnType("character varying(4096)");
            b.Property<string>("MatchMode").IsRequired().HasMaxLength(10).HasColumnType("character varying(10)");
            b.Property<int>("Priority").HasColumnType("integer"); b.Property<bool>("Enabled").HasColumnType("boolean");
            b.Property<DateTimeOffset>("UpdatedAt").HasColumnType("timestamp with time zone");
            b.HasKey("Id"); b.HasIndex("RobotId", "Enabled", "Priority"); b.ToTable("knowledge_entries", "ai_bot");
        });
        modelBuilder.Entity("AiBot.Domain.AiModelEndpoint", b => {
            b.Property<Guid>("Id").ValueGeneratedOnAdd().HasColumnType("uuid");
            b.Property<string>("Name").IsRequired().HasMaxLength(80).HasColumnType("character varying(80)");
            b.Property<string>("Provider").IsRequired().HasMaxLength(80).HasColumnType("character varying(80)");
            b.Property<string>("Endpoint").IsRequired().HasMaxLength(1024).HasColumnType("character varying(1024)");
            b.Property<string>("ModelId").IsRequired().HasMaxLength(256).HasColumnType("character varying(256)");
            b.Property<string>("ApiStyle").IsRequired().HasMaxLength(20).HasColumnType("character varying(20)");
            b.Property<string>("ProtectedApiKey").IsRequired().HasMaxLength(8192).HasColumnType("character varying(8192)");
            b.Property<bool>("Enabled").HasColumnType("boolean"); b.Property<int>("TimeoutSeconds").HasColumnType("integer"); b.Property<int>("MaxOutputTokens").HasColumnType("integer");
            b.Property<DateTimeOffset>("UpdatedAt").HasColumnType("timestamp with time zone"); b.HasKey("Id"); b.ToTable("ai_models", "ai_bot");
        });
        modelBuilder.Entity("AiBot.Domain.AutoReplyLog", b => {
            b.Property<Guid>("Id").ValueGeneratedOnAdd().HasColumnType("uuid"); b.Property<Guid>("ReceivedMessageId").HasColumnType("uuid"); b.Property<Guid>("RobotId").HasColumnType("uuid");
            b.Property<string>("Mode").IsRequired().HasMaxLength(10).HasColumnType("character varying(10)"); b.Property<string>("Status").IsRequired().HasMaxLength(20).HasColumnType("character varying(20)");
            b.Property<string>("ModelName").IsRequired().HasMaxLength(150).HasColumnType("character varying(150)"); b.Property<string>("ReplyContent").IsRequired().HasMaxLength(4096).HasColumnType("character varying(4096)");
            b.Property<string>("Result").IsRequired().HasMaxLength(2048).HasColumnType("character varying(2048)"); b.Property<string>("Attempts").IsRequired().HasMaxLength(4000).HasColumnType("character varying(4000)");
            b.Property<DateTimeOffset>("CreatedAt").HasColumnType("timestamp with time zone"); b.Property<DateTimeOffset?>("CompletedAt").HasColumnType("timestamp with time zone");
            b.HasKey("Id"); b.HasIndex("ReceivedMessageId").IsUnique(); b.HasIndex("RobotId"); b.ToTable("auto_reply_logs", "ai_bot");
        });
        modelBuilder.Entity("AiBot.Domain.QuoteDraft", b => {
            b.Property<Guid>("Id").ValueGeneratedOnAdd().HasColumnType("uuid"); b.Property<Guid>("RobotId").HasColumnType("uuid");
            b.Property<string>("ChatType").IsRequired().HasMaxLength(10).HasColumnType("character varying(10)");
            b.Property<string>("ChatId").IsRequired().HasMaxLength(256).HasColumnType("character varying(256)");
            b.Property<string>("SenderUserId").IsRequired().HasMaxLength(256).HasColumnType("character varying(256)");
            b.Property<string>("Status").IsRequired().HasMaxLength(20).HasColumnType("character varying(20)");
            b.Property<string>("StateJson").IsRequired().HasColumnType("text");
            b.Property<string>("RuleVersion").IsRequired().HasMaxLength(40).HasColumnType("character varying(40)");
            b.Property<DateTimeOffset>("UpdatedAt").HasColumnType("timestamp with time zone");
            b.HasKey("Id"); b.HasIndex("RobotId", "ChatType", "ChatId", "SenderUserId", "Status"); b.ToTable("quote_drafts", "ai_bot");
        });
        modelBuilder.Entity("AiBot.Domain.ReceivedMessage", b => {
            b.Property<Guid>("Id").ValueGeneratedOnAdd().HasColumnType("uuid");
            b.Property<Guid>("RobotId").HasColumnType("uuid");
            b.Property<string>("RobotName").IsRequired().HasMaxLength(80).HasColumnType("character varying(80)");
            b.Property<string>("MessageId").IsRequired().HasMaxLength(256).HasColumnType("character varying(256)");
            b.Property<string>("RequestId").IsRequired().HasMaxLength(256).HasColumnType("character varying(256)");
            b.Property<string>("SenderUserId").IsRequired().HasMaxLength(256).HasColumnType("character varying(256)");
            b.Property<string>("ChatId").IsRequired().HasMaxLength(256).HasColumnType("character varying(256)");
            b.Property<string>("ChatType").IsRequired().HasMaxLength(10).HasColumnType("character varying(10)");
            b.Property<string>("MessageType").IsRequired().HasMaxLength(64).HasColumnType("character varying(64)");
            b.Property<string>("Content").IsRequired().HasColumnType("text");
            b.Property<string>("PayloadJson").IsRequired().HasColumnType("text");
            b.Property<DateTimeOffset?>("SentAt").HasColumnType("timestamp with time zone");
            b.Property<DateTimeOffset>("ReceivedAt").HasColumnType("timestamp with time zone");
            b.HasKey("Id"); b.HasIndex("RobotId", "MessageId").IsUnique(); b.HasIndex("ReceivedAt");
            b.HasIndex("ChatId"); b.HasIndex("SenderUserId"); b.ToTable("received_messages", "ai_bot");
        });
        modelBuilder.Entity("AiBot.Domain.Robot", b => {
            b.Property<Guid>("Id").ValueGeneratedOnAdd().HasColumnType("uuid");
            b.Property<string>("Name").IsRequired().HasMaxLength(80).HasColumnType("character varying(80)");
            b.Property<string>("BotId").IsRequired().HasMaxLength(256).HasColumnType("character varying(256)");
            b.Property<string>("ProtectedSecret").IsRequired().HasMaxLength(2048).HasColumnType("character varying(2048)");
            b.Property<string>("Description").IsRequired().HasMaxLength(500).HasColumnType("character varying(500)");
            b.Property<bool>("Enabled").HasColumnType("boolean");
            b.Property<bool>("AutoReplyEnabled").ValueGeneratedOnAdd().HasColumnType("boolean").HasDefaultValue(false);
            b.Property<string>("ReplyMode").IsRequired().ValueGeneratedOnAdd().HasMaxLength(10).HasColumnType("character varying(10)").HasDefaultValue("knowledge");
            b.Property<bool>("ReplyToGroups").ValueGeneratedOnAdd().HasColumnType("boolean").HasDefaultValue(true).HasSentinel(true);
            b.Property<string>("ReplyPrompt").IsRequired().ValueGeneratedOnAdd().HasMaxLength(2000).HasColumnType("character varying(2000)").HasDefaultValue("");
            b.Property<string>("ModelPriorityJson").IsRequired().ValueGeneratedOnAdd().HasColumnType("text").HasDefaultValue("[]");
            b.Property<DateTimeOffset>("CreatedAt").HasColumnType("timestamp with time zone");
            b.Property<DateTimeOffset>("UpdatedAt").HasColumnType("timestamp with time zone");
            b.Property<Guid?>("TrackingModelId").HasColumnType("uuid");
            b.HasKey("Id"); b.HasIndex("BotId").IsUnique(); b.ToTable("robots", "ai_bot");
        });
        modelBuilder.Entity("AiBot.Domain.MessageLog", b => {
            b.Property<Guid>("Id").ValueGeneratedOnAdd().HasColumnType("uuid");
            b.Property<Guid>("RobotId").HasColumnType("uuid");
            b.Property<string>("RobotName").IsRequired().HasMaxLength(80).HasColumnType("character varying(80)");
            b.Property<string>("UserId").IsRequired().HasMaxLength(256).HasColumnType("character varying(256)");
            b.Property<string>("ChatId").IsRequired().ValueGeneratedOnAdd().HasMaxLength(256).HasColumnType("character varying(256)").HasDefaultValue("");
            b.Property<string>("TargetType").IsRequired().ValueGeneratedOnAdd().HasMaxLength(10).HasColumnType("character varying(10)").HasDefaultValue("single");
            b.Property<string>("Content").IsRequired().HasMaxLength(4096).HasColumnType("character varying(4096)");
            b.Property<string>("Operator").IsRequired().HasMaxLength(80).HasColumnType("character varying(80)");
            b.Property<string>("Status").IsRequired().HasMaxLength(20).HasColumnType("character varying(20)");
            b.Property<string>("Result").IsRequired().HasMaxLength(2048).HasColumnType("character varying(2048)");
            b.Property<string>("RequestId").HasMaxLength(128).HasColumnType("character varying(128)");
            b.Property<int?>("ErrorCode").HasColumnType("integer");
            b.Property<DateTimeOffset>("CreatedAt").HasColumnType("timestamp with time zone");
            b.HasKey("Id"); b.HasIndex("CreatedAt"); b.ToTable("message_logs", "ai_bot");
        });
        modelBuilder.Entity("AiBot.Domain.AdminUser", b => {
            b.Property<Guid>("Id").ValueGeneratedOnAdd().HasColumnType("uuid");
            b.Property<string>("Username").IsRequired().HasMaxLength(80).HasColumnType("character varying(80)");
            b.Property<string>("NormalizedUsername").IsRequired().HasMaxLength(80).HasColumnType("character varying(80)");
            b.Property<string>("PasswordHash").IsRequired().HasMaxLength(512).HasColumnType("character varying(512)");
            b.Property<int>("FailedAttempts").HasColumnType("integer");
            b.Property<DateTimeOffset?>("LockedUntil").HasColumnType("timestamp with time zone");
            b.HasKey("Id"); b.HasIndex("NormalizedUsername").IsUnique(); b.ToTable("admin_users", "ai_bot");
        });
    }
}
