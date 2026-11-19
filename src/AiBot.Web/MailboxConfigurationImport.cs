using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using AiBot.Application;
using Microsoft.Extensions.Options;

internal static class MailboxConfigurationImport
{
    public static async Task RunAsync(IServiceProvider services)
    {
        var settings = await JsonSerializer.DeserializeAsync<MailboxSettings[]>(Console.OpenStandardInput(),
            new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? [];
        await using var scope = services.CreateAsyncScope();
        var mailboxes = scope.ServiceProvider.GetRequiredService<IMailboxSettings>();
        var deployed = services.GetRequiredService<IOptions<MailSortingOptions>>().Value.Mailboxes;
        var known = (await mailboxes.ListAsync(default)).Select(box => box.ToMailbox())
            .Concat(deployed).Select(SortingMailboxIdentity.Key)
            .ToHashSet(StringComparer.Ordinal);
        var imported = 0;
        var skipped = 0;
        foreach (var box in settings)
        {
            Validator.ValidateObject(box, new ValidationContext(box), validateAllProperties: true);
            if (box.Id is not null || Uri.CheckHostName(box.Host.Trim()) == UriHostNameType.Unknown ||
                box.Protocol == "imap" && string.IsNullOrWhiteSpace(box.Folder) ||
                !string.IsNullOrEmpty(box.Category) && !MailSorting.Categories.ContainsKey(box.Category))
                throw new InvalidDataException("旧邮箱连接配置无效。");
            var key = SortingMailboxIdentity.Key(box.ToMailbox());
            if (!known.Add(key))
            {
                skipped++;
                continue;
            }
            // Let the operator verify the cutover before both workbenches can receive this mailbox.
            box.Enabled = false;
            await mailboxes.SaveAsync(box, default);
            imported++;
        }
        Console.WriteLine(JsonSerializer.Serialize(new { imported, skipped }));
    }
}
