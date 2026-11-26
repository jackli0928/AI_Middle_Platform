using System.Globalization;
using System.Text.RegularExpressions;
using AiBot.Domain;

namespace AiBot.Application;

public static partial class BookingInstructionRules
{
    public static MailAnalysis Enrich(MailAnalysis analysis, string subject, DateOnly? mailDate)
    {
        if (analysis.Category != "request") return analysis;
        var fields = analysis.Fields.ToDictionary(field => field.Key);
        foreach (var definition in MailFieldCatalog.Fields)
            fields.TryAdd(definition.Key, new MailField(definition.Key, definition.Label, []));
        List<MailCandidate> handling = [];
        void Add(string key, string value, string source, string quote)
        {
            var field = fields[key];
            var candidates = field.Candidates;
            if (key == "trade")
                candidates = candidates.Select(candidate =>
                {
                    var code = TradeCode().Match(candidate.Value);
                    return code.Success ? candidate with { Value = code.Value.ToUpperInvariant() } : candidate;
                }).ToArray();
            if (candidates.Count < 8 && !candidates.Any(candidate => Equivalent(key, candidate.Value, value)))
                candidates = candidates.Append(new MailCandidate(value, source, quote)).ToArray();
            fields[key] = field with { Candidates = candidates };
        }

        void Read(string text, string source)
        {
            foreach (var line in text.Split('\n').Select(x => x.Trim()).Where(x => x.Length is > 0 and <= 1600))
            {
                var dispatch = Dispatch().Match(line);
                if (dispatch.Success) Add("carrier", dispatch.Groups["carrier"].Value.ToUpperInvariant(), source, dispatch.Value);
                var booking = PrepaidCustomer().Match(line);
                if (booking.Success && fields["customer"].Candidates.Count == 0 && !fields["customer"].HasConflict)
                    Add("customer", booking.Groups["customer"].Value.Trim(), source, booking.Value);
                if (line.Contains("直客预付货", StringComparison.Ordinal)) Add("freight_payment", "预付", source, "直客预付货");

                foreach (Match terms in Trade().Matches(line))
                    Add("trade", terms.Groups["code"].Value.ToUpperInvariant(), source, terms.Value);
                foreach (Match flightDate in FlightDate().Matches(line))
                    if (ResolveDate(flightDate, mailDate) is { } date)
                        Add("etd", date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), source, flightDate.Value);
                var handover = Handover().Match(line);
                if (handover.Success)
                {
                    var when = ResolveDate(handover, mailDate)?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                    Add("cargo_handover", when is null ? handover.Value : $"{when} {handover.Groups["place"].Value.Trim()}", source, handover.Value);
                }

                foreach (Match cargo in CargoLine().Matches(line))
                {
                    Add("gross", cargo.Groups["gross"].Value, source, cargo.Value);
                    Add("volume", cargo.Groups["volume"].Value, source, cargo.Value);
                    Add("pieces", cargo.Groups["pieces"].Value, source, cargo.Value);
                    var packing = cargo.Groups["packing"].Value;
                    if (packing != "件") Add("packaging", packing, source, cargo.Value);
                }
                var price = SellRate().Match(line);
                if (price.Success)
                {
                    Add("sell_rate", price.Groups["price"].Value, source, price.Value);
                    if (fields["quoted_freight"].Candidates.Count == 0)
                        Add("quoted_freight", price.Value, source, price.Value);
                }
                foreach (Match instruction in Handling().Matches(line))
                    if (handling.Count < 12 && !handling.Any(candidate => candidate.Value == instruction.Value))
                        handling.Add(new(instruction.Value, source, instruction.Value));
            }
        }

        Read(CurrentText().Split(analysis.Body, 2)[0], "正文");
        if (!ReplySubject().IsMatch(subject)) Read(subject, "主题");
        if (handling.Count > 0 && fields["handling"].Candidates.Count == 0 && !fields["handling"].HasConflict)
            Add("handling", string.Join("；", handling.Select(x => x.Value)), "操作要求",
                string.Join("；", handling.Select(x => $"{x.Source}：{x.Evidence}")));

        // Derived values should not present disputed weights or dimensions as settled.
        if (fields["gross"].HasConflict || fields["volume"].HasConflict)
            foreach (var key in new[] { "volume_weight", "customer_weight", "cost_weight" })
            {
                if (key == "volume_weight" && !fields["volume"].HasConflict) continue;
                var field = fields[key];
                fields[key] = field with { Candidates = field.Candidates.Where(candidate =>
                    !candidate.Source.Contains("自动计算", StringComparison.Ordinal) &&
                    !candidate.Evidence.Contains("自动计算", StringComparison.Ordinal)).ToArray() };
            }
        return analysis with { Fields = fields.Values.ToArray() };
    }

    private static bool Equivalent(string key, string left, string right) =>
        left.Equals(right, StringComparison.OrdinalIgnoreCase) ||
        (key is "pieces" or "gross" or "volume" or "sell_rate") &&
        decimal.TryParse(left, NumberStyles.AllowDecimalPoint | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var a) &&
        decimal.TryParse(right, NumberStyles.AllowDecimalPoint | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var b) && a == b;

    private static DateOnly? ResolveDate(Match match, DateOnly? mailDate)
    {
        if (mailDate is not { } anchor || !int.TryParse(match.Groups["month"].Value, out var month) ||
            !int.TryParse(match.Groups["day"].Value, out var day) || month is < 1 or > 12) return null;
        return new[] { anchor.Year - 1, anchor.Year, anchor.Year + 1 }.Where(year =>
                year is >= 1 and <= 9999 && day >= 1 && day <= DateTime.DaysInMonth(year, month))
            .Select(year => (DateOnly?)new DateOnly(year, month, day))
            .OrderBy(candidate => Math.Abs(candidate!.Value.DayNumber - anchor.DayNumber)).FirstOrDefault();
    }

    private const string Terms = "EXW|FCA|FAS|FOB|CFR|CIF|CPT|CIP|DAP|DPU|DDP|DAT|DDU";
    private const string MonthDay = @"(?<![\d./])(?<month>\d{1,2})\s*[月./]\s*(?<day>\d{1,2})\s*(?:日|号)?";
    [GeneratedRegex(@"(?im)^(?:--完\s*$|>\s*|-----\s*(?:Original Message|原始邮件)|On .{1,200}wrote:|发件人[:：].*\n\s*(?:发送时间|日期)[:：])")]
    private static partial Regex CurrentText();
    [GeneratedRegex(@"^\s*(?:RE|FW|FWD|回复|转发)\s*[:：]", RegexOptions.IgnoreCase)]
    private static partial Regex ReplySubject();
    [GeneratedRegex(@"(?:预订|订|定|安排)\s*(?:自己\s*)?(?<carrier>[A-Z][A-Z0-9]|[0-9][A-Z])\s*(?:直飞|转飞)\s*[A-Z]{3}\s*[-–→]\s*[A-Z]{3}", RegexOptions.IgnoreCase)]
    private static partial Regex Dispatch();
    [GeneratedRegex(@"直客预付货[，,]\s*(?<customer>[\p{IsCJKUnifiedIdeographs}A-Za-z0-9（）() ·]{2,40})[，,]\s*(?:定|订|安排)")]
    private static partial Regex PrepaidCustomer();
    [GeneratedRegex(@"(?:贸易条款|条款)\s*[:：]?\s*(?<code>"+Terms+@")(?![A-Z])", RegexOptions.IgnoreCase)]
    private static partial Regex Trade();
    [GeneratedRegex(@"(?<![A-Z])(?:"+Terms+@")(?![A-Z])", RegexOptions.IgnoreCase)]
    private static partial Regex TradeCode();
    [GeneratedRegex(MonthDay + @"\s*(?:的\s*)?航班")]
    private static partial Regex FlightDate();
    [GeneratedRegex(@"预计\s*" + MonthDay + @"\s*(?<place>交[^,，。；;!\r\n]{1,24})")]
    private static partial Regex Handover();
    [GeneratedRegex(@"(?<![\d.])(?<gross>\d+(?:\.\d+)?)\s*(?:kgs?|公斤|千克)\s+(?<volume>\d+(?:\.\d+)?)\s*(?:cbm|m³|立方米)\s+(?<pieces>\d+)\s*(?<packing>箱|桶|袋|托|件)", RegexOptions.IgnoreCase)]
    private static partial Regex CargoLine();
    [GeneratedRegex(@"卖价\s*(?:\+?\d+(?:\.\d+)?\s*kg\s*)?[:：]?\s*(?<price>\d+(?:\.\d+)?)\s*/\s*(?:kg|公斤)(?![A-Z])", RegexOptions.IgnoreCase)]
    private static partial Regex SellRate();
    [GeneratedRegex(@"(?:需要|务必|请)\s*[^，,。；;：:！!\r\n]{0,20}?(?:倒车|倒货|缠膜|贴唛(?:\s*\+\s*拍照)?|发入仓图|确认鉴定\s*\+\s*报关资料)|(?:不能|禁止)打托")]
    private static partial Regex Handling();
}
