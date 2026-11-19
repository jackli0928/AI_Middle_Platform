using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using AiBot.Domain;

namespace AiBot.Application;

public static partial class MailSortingRules
{
    private static readonly Dictionary<string, string[]> Keywords = new()
    {
        ["request"] = ["订舱申请", "booking request", "请订舱", "空运委托", "客服订舱"],
        ["confirmation"] = ["订舱确认", "booking confirmed", "booking confirmation", "舱位已确认"],
        ["documents"] = ["文件检查", "请核对", "装箱单", "packing list", "运单草稿", "awb draft"],
        ["exception"] = ["航班变更", "取消订舱", "改航班", "候补", "未确认", "未能确认", "rebook", "cancelled", "not confirmed"]
    };
    private static readonly Dictionary<string, MailFieldDefinition> Labels = BuildLabels();
    private static readonly HashSet<string> Schedule = ["carrier", "etd", "flight", "cutoff", "document_cutoff", "cargo_ready"];
    private static readonly string[] ShipmentIdentity = ["origin", "destination", "mawb", "hawb", "booking"];

    public static MailAnalysis Analyze(string subject, string body, IReadOnlyList<MailAttachment> attachments,
        IReadOnlyList<string> references, string? fixedCategory, IEnumerable<MailAnalysis> related, decimal volumeDivisor, MailRuleSnapshot? snapshot = null)
    {
        var parts = HistoryBoundary().Split(body, 2);
        var current = parts[0];
        var isReply = ReplyPrefix().IsMatch(subject);
        var subjectCategories = Categories(subject, snapshot?.Keywords ?? Keywords);
        var bodyCategories = Categories(current, snapshot?.Keywords ?? Keywords);
        var attachmentCategories = Categories(string.Join('\n', attachments.Select(a => a.Name + "\n" + a.Preview)), snapshot?.Keywords ?? Keywords);
        if (subjectCategories.Count == 0 && subject.Contains("订舱") && Route().IsMatch(subject))
            subjectCategories.Add("request");
        if (bodyCategories.Count == 0 && BookingPhrase().IsMatch(current))
            bodyCategories.Add("request");
        var first = isReply ? bodyCategories : subjectCategories;
        var second = isReply ? subjectCategories : bodyCategories;
        var preferred = first.Count > 0 ? first : second.Count > 0 ? second : attachmentCategories;
        var category = fixedCategory ?? (bodyCategories.Contains("exception") || !isReply && subjectCategories.Contains("exception")
            ? "exception" : preferred.Count == 1 ? preferred[0] : "review");

        Dictionary<string, List<MailCandidate>> fields = new();
        if (!isReply) Collect(fields, "主题", subject);
        Collect(fields, "正文", current);
        foreach (var attachment in attachments)
            Collect(fields, "附件 · " + attachment.Name, attachment.Preview);

        Dictionary<string, List<MailCandidate>> historical = new();
        if (isReply) Collect(historical, "原邮件主题", subject);
        if (parts.Length > 1) Collect(historical, "正文引用", parts[1]);
        Supplement(fields, historical, category);
        foreach (var previous in related)
        {
            var earlier = previous.Fields.ToDictionary(x => x.Key,
                x => x.Candidates.Select(c => c with { Source = "历史往来 · " + c.Source }).ToList());
            Supplement(fields, earlier, category);
        }

        if (category == "request")
        {
            if (snapshot is null)
            {
                Default(fields, "settlement", "票结");
                Default(fields, "cost_split", "不分泡");
                Default(fields, "sell_currency", "CNY");
                Default(fields, "cost_currency", "CNY");
            }
            else
            {
                foreach (var (key, value) in snapshot.Defaults) Default(fields, key, value);
            }
            if (Number(fields, "volume") is { } volume)
                Derived(fields, "volume_weight", volume * 1_000_000 / volumeDivisor, "按体积重除数计算");
            if (Number(fields, "gross") is { } gross && Number(fields, "volume_weight") is { } dimensional)
            {
                var chargeable = Math.Max(gross, dimensional);
                Derived(fields, "customer_weight", chargeable, "取毛重与体积重较大值");
                Derived(fields, "cost_weight", chargeable, "取毛重与体积重较大值");
            }
        }
        Default(fields, "service", "空运出口");
        return new(category, body, MailFieldCatalog.Fields.Select(definition => new MailField(definition.Key,
            definition.Label, fields.GetValueOrDefault(definition.Key) ?? [])).ToArray(), attachments, "规则", references);
    }

    private static Dictionary<string, MailFieldDefinition> BuildLabels()
    {
        Dictionary<string, MailFieldDefinition> result = new(StringComparer.OrdinalIgnoreCase);
        foreach (var field in MailFieldCatalog.Fields.Where(f => f.Key != "service"))
            foreach (var label in field.Aliases.Prepend(field.Label).Prepend(field.Key))
                result[LabelId(label)] = field;
        return result;
    }

    private static string LabelId(string label) => string.Concat(label.Normalize(NormalizationForm.FormKC)
        .Where(c => !char.IsWhiteSpace(c)));

    private static List<string> Categories(string text, Dictionary<string, string[]> keywords)
    {
        var matches = keywords.Where(item => item.Value.Any(word => text.Contains(word, StringComparison.OrdinalIgnoreCase)))
            .Select(item => item.Key).ToList();
        if (matches.Contains("request") || matches.Contains("confirmation")) matches.Remove("documents");
        return matches;
    }

    private static void Collect(Dictionary<string, List<MailCandidate>> fields, string source, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var lines = text.Replace("\r", "").Split('\n');
        for (var row = 0; row < lines.Length; row++)
        {
            var line = lines[row].Trim().TrimStart('>').TrimStart();
            if (line.Length == 0 || line.Length > 1800) continue;
            var entries = Cells(line);
            foreach (var item in entries)
            {
                var index = item.IndexOfAny([':', '：', '=']);
                if (index > 0)
                    Observe(fields, item[..index], item[(index + 1)..], source, line);
            }

            if (entries.Length == 2 && FindLabel(entries[1]) is null)
                Observe(fields, entries[0], entries[1], source, line);
            else if (entries.Length >= 2 && row + 1 < lines.Length &&
                     entries.Count(e => FindLabel(e) is not null) >= 2)
            {
                var values = Cells(lines[row + 1]);
                if (values.Length == entries.Length)
                    for (var col = 0; col < entries.Length; col++)
                        Observe(fields, entries[col], values[col], source, line + " / " + lines[row + 1]);
            }

            var route = Route().Match(line);
            if (route.Success && (source.Contains("主题") || line.Contains("航线") || line.Contains("route", StringComparison.OrdinalIgnoreCase)))
            {
                var stops = Airport().Matches(route.Value).Select(m => m.Value).ToArray();
                Observe(fields, "origin", stops[0], source, line);
                Observe(fields, "destination", stops[^1], source, line);
                if (stops.Length > 2) Observe(fields, "transit", stops[1], source, line);
            }
            var cargo = Cargo().Match(line);
            if (cargo.Success)
            {
                Observe(fields, "pieces", cargo.Groups[1].Value, source, line);
                Observe(fields, "gross", cargo.Groups[3].Value, source, line);
                Observe(fields, "volume", cargo.Groups[4].Value, source, line);
                if (cargo.Groups[2].Success) Observe(fields, "packaging", cargo.Groups[2].Value, source, line);
            }
        }
    }

    private static void Observe(Dictionary<string, List<MailCandidate>> fields, string label, string input,
        string source, string evidence)
    {
        if (FindLabel(label) is not { } field) return;
        var value = input.Trim();
        if (value.Length is 0 or > 1500) return;
        if (field.Kind is "airport" or "currency") value = value.ToUpperInvariant();
        if (field.Kind == "boolean")
        {
            value = value.ToLowerInvariant() switch
            {
                "有" or "yes" or "是" => "是",
                "无" or "無" or "no" or "否" => "否",
                _ => ""
            };
            if (value.Length == 0) return;
        }
        if (field.Key == "mawb")
        {
            var digits = value.Replace("-", "").Replace(" ", "");
            if (digits.Length != 11 || !digits.All(char.IsAsciiDigit)) return;
            value = digits.Insert(3, "-");
        }
        if (field.Key == "hs_code" && (value.Length is < 6 or > 10 || !value.All(char.IsAsciiDigit))) return;
        if (field.Kind is "positive" or "integer" or "money")
        {
            var numeric = NumberWithUnit().Match(value);
            if (!numeric.Success || !decimal.TryParse(numeric.Groups[1].Value, NumberStyles.AllowDecimalPoint | NumberStyles.AllowThousands,
                CultureInfo.InvariantCulture, out var n) || n > 1_000_000_000_000 ||
                field.Kind != "money" && n <= 0 || field.Kind == "integer" && decimal.Truncate(n) != n) return;
            value = n.ToString("0.################", CultureInfo.InvariantCulture);
        }
        if (!fields.TryGetValue(field.Key, out var candidates)) fields[field.Key] = candidates = [];
        if (candidates.Count < 8 && !candidates.Any(c => c.Value.Equals(value, StringComparison.OrdinalIgnoreCase) && c.Source == source))
            candidates.Add(new(value, source, evidence[..Math.Min(evidence.Length, 500)]));
    }

    private static MailFieldDefinition? FindLabel(string label)
    {
        if (Labels.TryGetValue(LabelId(label.Trim()), out var field)) return field;
        var suffix = label.IndexOfAny(['(', '（', '⏎']);
        return suffix > 0 ? Labels.GetValueOrDefault(LabelId(label[..suffix])) : null;
    }

    private static string[] Cells(string row) => row.IndexOfAny(['\t', '|', ';', '；']) >= 0
        ? row.Split(['\t', '|', ';', '；'], StringSplitOptions.None) : ColumnGap().Split(row.Trim());

    private static void Supplement(Dictionary<string, List<MailCandidate>> fields,
        Dictionary<string, List<MailCandidate>> previous, string category)
    {
        if (ShipmentIdentity.Any(key => fields.TryGetValue(key, out var now) &&
            previous.TryGetValue(key, out var old) && !now.Any(a => old.Any(b =>
                a.Value.Equals(b.Value, StringComparison.OrdinalIgnoreCase))))) return;
        foreach (var (key, candidates) in previous)
        {
            if (category is "confirmation" or "exception" && Schedule.Contains(key)) continue;
            if (candidates.Count > 0 && candidates.Select(c => c.Value).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1)
                fields.TryAdd(key, candidates);
        }
    }

    private static decimal? Number(Dictionary<string, List<MailCandidate>> fields, string key)
    {
        if (!fields.TryGetValue(key, out var values) || values.Select(x => x.Value).Distinct().Count() != 1) return null;
        return decimal.TryParse(values[0].Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    private static void Default(Dictionary<string, List<MailCandidate>> fields, string key, string value) =>
        fields.TryAdd(key, [new(value, "流程默认值", value)]);

    private static void Derived(Dictionary<string, List<MailCandidate>> fields, string key, decimal value, string evidence) =>
        fields.TryAdd(key, [new(value.ToString("0.###", CultureInfo.InvariantCulture), "自动计算", evidence)]);

    [GeneratedRegex(@"(?im)^(?:>\s*|-----\s*(?:Original Message|原始邮件)|On .{1,200}wrote:|发件人[:：].*\n\s*(?:发送时间|日期)[:：])")]
    private static partial Regex HistoryBoundary();
    [GeneratedRegex(@"^\s*(?:RE|FW|FWD|回复|转发)\s*[:：]", RegexOptions.IgnoreCase)]
    private static partial Regex ReplyPrefix();
    [GeneratedRegex(@"\b[A-Z]{3}(?:\s*(?:[-–→]|\bTO\b)\s*[A-Z]{3})+\b")]
    private static partial Regex Route();
    [GeneratedRegex(@"\b[A-Z]{3}\b")]
    private static partial Regex Airport();
    [GeneratedRegex(@"新\s*BK\b|安排订.{0,12}航班|请\s*订\s*[A-Z0-9]{2}\s+.{0,30}航班", RegexOptions.IgnoreCase)]
    private static partial Regex BookingPhrase();
    [GeneratedRegex(@"(?<![\d./])(\d+)\s*(PCS?|CTNS?|PLTS?|PKGS?|件|箱|托)?\s*[/_]\s*(\d+(?:\.\d+)?)\s*(?:KGS?)?\s*[/_]\s*(\d+(?:\.\d+)?)\s*(?:CBM|M3|M³)?(?![\d./])", RegexOptions.IgnoreCase)]
    private static partial Regex Cargo();
    [GeneratedRegex(@"^(\d+(?:,\d{3})*(?:\.\d+)?)\s*(?:PCS?|CTNS?|PLTS?|PKGS?|件|箱|托|KGS?|公斤|千克|CBM|M3|M³|立方米)?$", RegexOptions.IgnoreCase)]
    private static partial Regex NumberWithUnit();
    [GeneratedRegex(@" {2,}")]
    private static partial Regex ColumnGap();
}
