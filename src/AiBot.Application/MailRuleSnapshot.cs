using System.Text.Json;
using System.Text.Json.Serialization;

namespace AiBot.Application;

public sealed record MailRuleSnapshot(
    int Version,
    [property: JsonPropertyName("volume_divisor")] int VolumeDivisor,
    Dictionary<string, string[]> Keywords,
    Dictionary<string, string> Defaults,
    Dictionary<string, string> Routes,
    [property: JsonPropertyName("customer_rules")] Dictionary<string, string> Customers)
{
    public static MailRuleSnapshot BuiltIn(int volumeDivisor) => new(1, Math.Clamp(volumeDivisor, 1, 999999),
        new()
        {
            ["request"] = ["订舱申请", "booking request", "请订舱", "空运委托"],
            ["confirmation"] = ["订舱确认", "booking confirmed", "booking confirmation", "舱位已确认"],
            ["documents"] = ["文件检查", "请核对", "装箱单", "packing list", "运单草稿", "awb draft"],
            ["exception"] = ["航班变更", "取消订舱", "改航班", "候补", "未确认", "未能确认", "rebook", "cancelled", "not confirmed"]
        },
        new() { ["settlement"] = "票结", ["cost_split"] = "不分泡", ["sell_currency"] = "CNY", ["cost_currency"] = "CNY" },
        new(), new());

    public static MailRuleSnapshot? Load(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        using var stream = File.OpenRead(path);
        var snapshot = JsonSerializer.Deserialize<MailRuleSnapshot>(stream, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        if (snapshot is null || snapshot.Version <= 0 || snapshot.VolumeDivisor is < 1 or > 999999 ||
            snapshot.Keywords is null || snapshot.Defaults is null || snapshot.Routes is null || snapshot.Customers is null ||
            MailSorting.Categories.Keys.Where(x => x != "review").Any(key => !snapshot.Keywords.ContainsKey(key)))
            throw new InvalidDataException("同步的邮件规则配置不完整。");
        return snapshot;
    }
}
