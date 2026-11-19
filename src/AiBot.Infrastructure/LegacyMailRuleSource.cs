using AiBot.Application;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace AiBot.Infrastructure;

public sealed class LegacyMailRuleSource(IOptions<MailSortingOptions> options, IHostEnvironment environment)
{
    public string Path { get; } = string.IsNullOrWhiteSpace(options.Value.RulesSnapshot)
        ? "" : System.IO.Path.GetFullPath(options.Value.RulesSnapshot, environment.ContentRootPath);

    public MailRuleSnapshot? Read() => MailRuleSnapshot.Load(Path);
}
