using System.ComponentModel.DataAnnotations;
using System.Net.Mail;
using System.Text.RegularExpressions;
using AiBot.Application;

namespace AiBot.Web.Models;

public sealed partial class MailRuleEditor
{
    public static IReadOnlyDictionary<string, string> DefaultFields { get; } = new Dictionary<string, string>
    {
        ["salesperson"] = "销售员", ["settlement"] = "结算方式", ["tax_exempt"] = "是否免税",
        ["cost_split"] = "成本分泡", ["sell_currency"] = "卖价币种", ["cost_currency"] = "成本币种"
    };

    public int Revision { get; set; }
    [Range(1, 999999, ErrorMessage = "体积重除数必须是 1 到 999999 的整数。")]
    public int VolumeDivisor { get; set; }
    public Dictionary<string, string?> Keywords { get; set; } = new();
    public Dictionary<string, string?> Defaults { get; set; } = new();
    [StringLength(100000, ErrorMessage = "收件地址路由内容过长。")] public string? Routes { get; set; }
    [StringLength(152000, ErrorMessage = "客户映射内容过长。")] public string? Customers { get; set; }

    public static MailRuleEditor From(MailRuleSettings rules) => new()
    {
        Revision = rules.Revision, VolumeDivisor = rules.Snapshot.VolumeDivisor,
        Keywords = rules.Snapshot.Keywords.ToDictionary(x => x.Key, x => (string?)string.Join('\n', x.Value)),
        Defaults = rules.Snapshot.Defaults.ToDictionary(x => x.Key, x => (string?)x.Value),
        Routes = string.Join('\n', rules.Snapshot.Routes.Select(x => $"{x.Key} = {CategoryLabel(x.Value)}")),
        Customers = string.Join('\n', rules.Snapshot.Customers.Select(x => $"{x.Key} = {x.Value}"))
    };

    public MailRuleSnapshot CreateSnapshot(int version, List<string> errors)
    {
        var keywords = new Dictionary<string, string[]>();
        foreach (var (key, label) in MailSorting.Categories.Where(x => x.Key != "review"))
        {
            var terms = Lines(Keywords.GetValueOrDefault(key)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (terms.Length > 30 || terms.Any(word => word.Length > 100))
                errors.Add($"{label}最多可设置 30 个关键词，每个不超过 100 字。");
            keywords[key] = terms;
        }

        var defaults = new Dictionary<string, string>();
        foreach (var (key, label) in DefaultFields)
        {
            if (Defaults.GetValueOrDefault(key)?.Trim() is not { Length: > 0 } value) continue;
            if (value.Length > 1500 ||
                key == "tax_exempt" && value is not ("是" or "否") ||
                key.EndsWith("_currency", StringComparison.Ordinal) && !Currency().IsMatch(value))
            {
                errors.Add($"请检查{label}的格式。");
                continue;
            }
            defaults[key] = key.EndsWith("_currency", StringComparison.Ordinal) ? value.ToUpperInvariant() : value;
        }

        var routes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        ParsePairs(Routes, "收件地址路由", errors, (key, value) =>
        {
            var category = value is "auto" or "按内容识别" ? "auto" : MailSorting.Categories
                .FirstOrDefault(p => p.Key != "review" && (p.Key == value || p.Value == value)).Key;
            return category is not null && MailAddress.TryCreate(key, out var address) && address.Address == key &&
                routes.TryAdd(key.ToLowerInvariant(), category);
        });
        var customers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        ParsePairs(Customers, "客户映射", errors, (key, value) =>
            key.Length <= 150 && value.Length <= 150 &&
            (!key.Contains('@') || CustomerAddress().IsMatch(key)) && customers.TryAdd(key.ToLowerInvariant(), value));
        if (customers.Count > 500) errors.Add("客户映射最多可保存 500 条。");
        return new MailRuleSnapshot(version, VolumeDivisor, keywords, defaults, routes, customers);
    }

    private static string CategoryLabel(string key) => MailSorting.Categories.GetValueOrDefault(key, "按内容识别");
    private static IEnumerable<string> Lines(string? value) =>
        (value ?? "").Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    private static void ParsePairs(string? text, string label, List<string> errors, Func<string, string, bool> add)
    {
        var index = 0;
        foreach (var line in (text ?? "").Split('\n'))
        {
            index++;
            if (string.IsNullOrWhiteSpace(line)) continue;
            var divider = line.IndexOf('=');
            if (divider <= 0 || line[..divider].Trim() is not { Length: > 0 } key ||
                line[(divider + 1)..].Trim() is not { Length: > 0 } value || !add(key, value))
                errors.Add($"{label}第 {index} 行格式无效或存在重复项。");
        }
    }

    [GeneratedRegex("^[A-Z]{3}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Currency();

    [GeneratedRegex(@"^(?:[^@\s]+)?@[a-z0-9.-]+\.[a-z]{2,}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CustomerAddress();
}
