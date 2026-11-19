using AiBot.Application;
using AiBot.Domain;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AiBot.Tests;

public sealed class TestApp : WebApplicationFactory<global::Program>
{
    public TestApp() => Automation = new(Repository);
    public MemoryRepository Repository { get; } = new();
    public FakeGateway Gateway { get; } = new();
    public MemoryInboxRepository Inbox { get; } = new();
    public FakeConnections Connections { get; } = new();
    public MemoryAutomationRepository Automation { get; }
    public FakeAiClient Ai { get; } = new();
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseContentRoot(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/AiBot.Web")));
        builder.UseEnvironment("Development");
        builder.ConfigureLogging(logging => logging.SetMinimumLevel(LogLevel.Error));
        builder.ConfigureServices(services => {
            var startup = services.Single(x => x.ServiceType == typeof(IHostedService) && x.ImplementationType == typeof(DatabaseStartup));
            services.Remove(startup);
            services.Remove(services.Single(x => x.ServiceType == typeof(IHostedService) && x.ImplementationType == typeof(MailSortingWorker)));
            services.Remove(services.Single(x => x.ServiceType == typeof(IHostedService) && x.ImplementationType == typeof(AiBot.Infrastructure.WeComBackgroundWorker)));
            foreach (var warmup in services.Where(x => x.ServiceType == typeof(IHostedService) && x.ImplementationType?.Name == "DataProtectionHostedService").ToList()) services.Remove(warmup);
            services.RemoveAll<IRobotRepository>(); services.AddSingleton<IRobotRepository>(Repository);
            services.RemoveAll<IWeComGateway>(); services.AddSingleton<IWeComGateway>(Gateway);
            services.RemoveAll<IConnectionMonitor>(); services.AddSingleton<IConnectionMonitor>(Connections);
            services.RemoveAll<IInboxRepository>(); services.AddSingleton<IInboxRepository>(Inbox);
            services.RemoveAll<IAutomationRepository>(); services.AddSingleton<IAutomationRepository>(Automation);
            services.RemoveAll<IAiCompletionClient>(); services.AddSingleton<IAiCompletionClient>(Ai);
            services.RemoveAll<IAccountService>(); services.AddSingleton<IAccountService, FakeAccount>();
            services.RemoveAll<DatabaseState>(); services.AddSingleton(new DatabaseState { Ready = true });
            services.RemoveAll<IDataProtectionProvider>(); services.AddSingleton<IDataProtectionProvider, EphemeralDataProtectionProvider>();
        });
    }
}

public sealed class FakeAccount : IAccountService
{
    public Task<AdminUser?> AuthenticateAsync(string username, string password, CancellationToken ct) =>
        Task.FromResult(username == "reviewer" && password == "PreviewOnly!2026" ? new AdminUser { Username = "reviewer" } : null);
    public Task<bool> ChangePasswordAsync(Guid id, string current, string next, CancellationToken ct) => Task.FromResult(current == "PreviewOnly!2026");
}
public sealed class FakeGateway : IWeComGateway
{
    public int Calls { get; private set; }
    public string? LastTargetType { get; private set; }
    public string? LastTargetId { get; private set; }
    public SendResult Result { get; set; } = new(true, "send", "企业微信已接受消息。", "test-receipt", 0);
    public Task<SendResult> SendAsync(string botId, string secret, string targetType, string targetId, string content, CancellationToken ct)
    { Calls++; LastTargetType = targetType; LastTargetId = targetId; return Task.FromResult(Result); }
}
public sealed class MemoryRepository : IRobotRepository
{
    public List<Robot> Robots { get; } = [];
    public List<MessageLog> Messages { get; } = [];
    public Task<IReadOnlyList<Robot>> ListAsync(string? search, CancellationToken ct) => Task.FromResult<IReadOnlyList<Robot>>(Robots.Where(r => string.IsNullOrEmpty(search) || r.Name.Contains(search) || r.BotId.Contains(search)).ToList());
    public Task<Robot?> FindAsync(Guid id, CancellationToken ct) => Task.FromResult(Robots.SingleOrDefault(x => x.Id == id));
    public Task<bool> BotIdExistsAsync(string botId, Guid? exceptId, CancellationToken ct) => Task.FromResult(Robots.Any(x => x.BotId == botId && x.Id != exceptId));
    public void Add(Robot robot) => Robots.Add(robot);
    public void Remove(Robot robot) => Robots.Remove(robot);
    public Task SaveAsync(CancellationToken ct) => Task.CompletedTask;
    public Task<bool> TryStartMessageAsync(MessageLog log, CancellationToken ct)
    {
        if (Messages.Any(x => x.Id == log.Id)) return Task.FromResult(false);
        Messages.Add(log); return Task.FromResult(true);
    }
    public Task CompleteMessageAsync(MessageLog log, CancellationToken ct) => Task.CompletedTask;
    public Task<Dashboard> DashboardAsync(DateTimeOffset today, CancellationToken ct) => Task.FromResult(new Dashboard(Robots.Count, Robots.Count(x => x.Enabled), Messages.Count(x => x.CreatedAt >= today), Messages.Count(x => x.CreatedAt >= today && x.Status == DeliveryStatus.Accepted), Messages.OrderByDescending(x => x.CreatedAt).Take(6).ToList()));
    public Task<PageResult<MessageLog>> LogsAsync(int page, string? search, CancellationToken ct)
    {
        var rows = Messages.Where(x => string.IsNullOrEmpty(search) || x.RobotName.Contains(search) || x.UserId.Contains(search) || x.ChatId.Contains(search)).OrderByDescending(x => x.CreatedAt).ToList();
        return Task.FromResult(new PageResult<MessageLog>(rows.Skip((page - 1) * 20).Take(20).ToList(), rows.Count, page, 20));
    }
}
