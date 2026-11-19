using System.Globalization;
using System.Threading.RateLimiting;
using AiBot.Application;
using AiBot.Domain;
using AiBot.Infrastructure;
using AiBot.Web;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

var importMailboxConfiguration = args is ["--import-mailbox-config"];
var builder = WebApplication.CreateBuilder(importMailboxConfiguration ? [] : args);
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; });
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false).AddEnvironmentVariables();
var connection = builder.Configuration.GetConnectionString("Postgres") ?? throw new InvalidOperationException("请配置 PostgreSQL 连接字符串。");
builder.Services.AddDbContext<BotDbContext>(o => o.UseNpgsql(connection, p => p.MigrationsHistoryTable("__EFMigrationsHistory", "ai_bot")));
builder.Services.AddScoped<IRobotRepository, RobotRepository>();
builder.Services.AddScoped<IRobotService, RobotService>();
builder.Services.AddScoped<IAccountService, AccountService>();
builder.Services.AddScoped<IPasswordHasher<AdminUser>, PasswordHasher<AdminUser>>();
builder.Services.AddSingleton<ISecretProtector, SecretProtector>();
builder.Services.AddScoped<IInboxRepository, InboxRepository>();
builder.Services.AddScoped<IInboxService, InboxService>();
builder.Services.AddScoped<IAutomationRepository, AutomationRepository>();
builder.Services.AddScoped<IAutomationSettingsService, AutomationSettingsService>();
builder.Services.AddScoped<IAutoReplyService, AutoReplyService>();
builder.Services.AddHttpClient<IAiCompletionClient, AiCompletionClient>().ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
builder.Services.AddSingleton<IWeComSocketFactory, WeComSocketFactory>();
builder.Services.AddSingleton<WeComGateway>();
builder.Services.AddSingleton<IWeComGateway>(sp => sp.GetRequiredService<WeComGateway>());
builder.Services.AddSingleton<IConnectionMonitor>(sp => sp.GetRequiredService<WeComGateway>());
builder.Services.AddSingleton<DatabaseState>();
builder.Services.AddHostedService<DatabaseStartup>();
builder.Services.AddHostedService<WeComBackgroundWorker>();
builder.Services.AddHttpClient<QuoteMcpClient>().ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
builder.Services.AddHostedService<QuoteWorker>();
builder.Services.Configure<MailSortingOptions>(builder.Configuration.GetSection("MailSorting"));
builder.Services.AddScoped<IMailSorting, PostgresMailSorting>();
builder.Services.AddScoped<IMailboxCredentials, PostgresMailboxCredentials>();
builder.Services.AddScoped<IMailboxSettings, PostgresMailboxSettings>();
builder.Services.AddSingleton<MailboxPoller>();
builder.Services.AddSingleton<LegacyMailRuleSource>();
builder.Services.AddScoped<IMailRuleSettings, PostgresMailRuleSettings>();
builder.Services.AddSingleton<LegacyMailAnalyzer>();
builder.Services.AddHttpClient<MailModelSuggestions>().ConfigurePrimaryHttpMessageHandler(
    () => new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false });
builder.Services.AddHostedService<MailSortingWorker>();
var protection = builder.Services.AddDataProtection().SetApplicationName("Li.AiBot").PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(builder.Environment.ContentRootPath, "App_Data", "keys")));
if (OperatingSystem.IsWindows()) protection.ProtectKeysWithDpapi();
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(o => {
    o.LoginPath = "/Account/Login"; o.Cookie.Name = "Li.AiBot.Auth"; o.Cookie.HttpOnly = true;
    o.Cookie.SameSite = SameSiteMode.Strict; o.Cookie.SecurePolicy = builder.Environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
    o.ExpireTimeSpan = TimeSpan.FromHours(8); o.SlidingExpiration = true;
});
builder.Services.AddAuthorization();
builder.Services.AddControllersWithViews(o => o.Filters.Add(new AutoValidateAntiforgeryTokenAttribute()));
builder.Services.AddRateLimiter(o => {
    o.RejectionStatusCode = 429;
    o.AddPolicy("login", context => RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    o.OnRejected = async (context, ct) => { await context.HttpContext.Response.WriteAsync("操作过于频繁，请一分钟后重试。", ct); };
});
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.GetCultureInfo("zh-CN");
CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.GetCultureInfo("zh-CN");
var app = builder.Build();
if (importMailboxConfiguration)
{
    await using var importing = app;
    await MailboxConfigurationImport.RunAsync(app.Services);
    return;
}
app.UseExceptionHandler("/Home/Error");
if (!app.Environment.IsDevelopment()) { app.UseHsts(); app.UseHttpsRedirection(); }
app.Use(async (context, next) => {
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    context.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; style-src 'self'; script-src 'self'; img-src 'self' data:; object-src 'none'; base-uri 'self'; frame-ancestors 'none'; form-action 'self'";
    if (!context.Request.Path.StartsWithSegments("/css") && !context.Request.Path.StartsWithSegments("/js"))
        context.Response.Headers.CacheControl = "no-cache, no-store";
    await next();
});
app.UseStaticFiles(); app.UseRouting(); app.UseRateLimiter(); app.UseAuthentication(); app.UseAuthorization();
app.MapGet("/health", (DatabaseState state) => Results.Json(new { status = state.Ready ? "ready" : "database-unavailable" }, statusCode: state.Ready ? 200 : 503));
app.MapControllerRoute("default", "{controller=Home}/{action=Index}/{id?}");
app.Run();

public partial class Program { }

public sealed class DatabaseState { public volatile bool Ready; }
public sealed class DatabaseStartup(IServiceProvider services, DatabaseState state, IConfiguration config, ILogger<DatabaseStartup> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
                await DatabaseInitializer.InitializeAsync(db, config["BootstrapAdmin:Username"], config["BootstrapAdmin:Password"], stoppingToken);
                state.Ready = true; logger.LogInformation("数据库和管理员初始化完成。"); return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                state.Ready = false;
                // Never log a connection string or credentials.
                logger.LogWarning("数据库初始化未完成（{Type}），请检查本地连接配置及初始化管理员。30 秒后重试。", ex.GetType().Name);
            }
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
    }
}
