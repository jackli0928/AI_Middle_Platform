using AiBot.Application;
using AiBot.Infrastructure;
using Microsoft.Extensions.Options;

public sealed class MailSortingWorker(DatabaseState database, MailboxPoller poller,
    IServiceScopeFactory scopes, IOptions<MailSortingOptions> options, ILogger<MailSortingWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;


        while (!stoppingToken.IsCancellationRequested)
        {
            if (database.Ready)
            {
                var accounts = await ConfiguredAccountsAsync(settings, stoppingToken);
                await Parallel.ForEachAsync(accounts, new ParallelOptions
                {
                    MaxDegreeOfParallelism = 4, CancellationToken = stoppingToken
                }, async (account, ct) =>
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    deadline.CancelAfter(TimeSpan.FromMinutes(3));
                    try { await poller.PollAsync(account, deadline.Token); }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
                    catch (Exception ex)
                    {
                        logger.LogWarning("邮件自动收件未完成（{Type}），下次轮询将继续。", ex.GetType().Name);
                    }
                });
            }
            await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(settings.PollSeconds, 15, 3600)), stoppingToken);
        }
    }
    private async Task<IReadOnlyList<SortingMailbox>> ConfiguredAccountsAsync(MailSortingOptions settings, CancellationToken ct)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var repository = scope.ServiceProvider.GetRequiredService<IMailboxSettings>();
            var managed = await repository.ListAsync(ct);
            return managed.Select(x => x.ToMailbox()).Concat(settings.Mailboxes)
                .DistinctBy(SortingMailboxIdentity.Key).Where(x => x.Enabled).ToArray();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("读取收件邮箱配置失败（{Type}）。", ex.GetType().Name);
            return [];
        }
    }
}