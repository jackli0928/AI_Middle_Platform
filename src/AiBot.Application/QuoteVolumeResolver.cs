using System.Globalization;
using System.Text.RegularExpressions;

namespace AiBot.Application;

public sealed record QuoteVolumeResolution(int? Pieces, double? Volume, string? Clarification);

public static class QuoteVolumeResolver
{
    private const RegexOptions Options = RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    private static readonly Regex DimensionHint = new(@"尺寸|规格|长\s*\d.{0,20}宽\s*\d|\d\s*[x×*]\s*\d", Options);
    private static readonly Regex Triples = new(@"(?<![\d.])(?<l>\d+(?:\.\d+)?)\s*[x×*]\s*(?<w>\d+(?:\.\d+)?)\s*[x×*]\s*(?<h>\d+(?:\.\d+)?)\s*(?<unit>毫米|厘米|mm|cm|米|m)?(?![A-Za-z])", Options);
    private static readonly Regex TriplesWithUnits = new(@"(?<![\d.])(?<l>\d+(?:\.\d+)?)\s*(?<lu>毫米|厘米|mm|cm|米|m)\s*[x×*]\s*(?<w>\d+(?:\.\d+)?)\s*(?<wu>毫米|厘米|mm|cm|米|m)\s*[x×*]\s*(?<h>\d+(?:\.\d+)?)\s*(?<hu>毫米|厘米|mm|cm|米|m)(?![A-Za-z])", Options);
    private static readonly Regex Labelled = new(@"长\s*(?<l>\d+(?:\.\d+)?)\s*(?<lu>毫米|厘米|mm|cm|米|m)?\s*[,，]?\s*宽\s*(?<w>\d+(?:\.\d+)?)\s*(?<wu>毫米|厘米|mm|cm|米|m)?\s*[,，]?\s*高\s*(?<h>\d+(?:\.\d+)?)\s*(?<hu>毫米|厘米|mm|cm|米|m)?", Options);
    private static readonly Regex Quantity = new(@"(?<![\d.])(?<n>\d{1,9})\s*(?:件|箱|托|PCS|CTNS|PLTS?|PALLETS?|PIECES?|CARTONS?)(?![A-Za-z])", Options);
    private static readonly Regex TotalQuantity = new(@"(?:总件数|合计|总计|共)\s*[:：]?\s*(?<n>\d{1,9})\s*(?:件|箱|托|PCS|CTNS|PLTS)(?![A-Za-z])", Options);
    private static readonly Regex PerPiece = new(@"每件|每箱|每托|单件|单箱|同尺寸|相同尺寸", Options);

    public static QuoteVolumeResolution Resolve(string content, int? extractedPieces, double? extractedVolume, bool trustedDeclaredVolume = false)
    {
        var explicitFields = QuoteInquiryParser.Parse(content);
        var declared = trustedDeclaredVolume && extractedVolume is { } verified ? verified
            : explicitFields.TryGetValue("volume", out var value) ? (double)value : (double?)null;
        if (!DimensionHint.IsMatch(content)) return new(extractedPieces, extractedVolume, null);
        QuoteVolumeResolution Incomplete(int? pieces, string clarification) =>
            declared is { } stated ? new(pieces, stated, null) : new(pieces, null, clarification);

        var matches = Triples.Matches(content).Cast<Match>().Concat(TriplesWithUnits.Matches(content).Cast<Match>())
            .Concat(Labelled.Matches(content).Cast<Match>())
            .OrderBy(x => x.Index).ToArray();
        if (matches.Length == 0) return Incomplete(extractedPieces, "完整长宽高、尺寸单位及各组件数");
        var clauses = Regex.Matches(content, @"[^，,；;\n]+", Options).Cast<Match>();
        if (clauses.Any(clause => DimensionHint.IsMatch(clause.Value) && clause.Value.Any(char.IsDigit)
            && !matches.Any(x => x.Index >= clause.Index && x.Index + x.Length <= clause.Index + clause.Length)))
            return Incomplete(extractedPieces, "全部货物的完整长宽高尺寸明细");
        if (matches.Zip(matches.Skip(1)).Any(x => x.First.Index + x.First.Length > x.Second.Index))
            return Incomplete(extractedPieces, "不重复的完整尺寸明细");

        var groups = new List<(double Volume, int? Count)>();
        foreach (var match in matches)
        {
            var units = new[] { match.Groups["lu"].Value, match.Groups["wu"].Value, match.Groups["hu"].Value };
            if (units.All(string.IsNullOrEmpty)) units = [match.Groups["unit"].Value, match.Groups["unit"].Value, match.Groups["unit"].Value];
            var specified = units.Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (specified.Length == 0 || units.Any(x => x.Length == 0) && specified.Length > 1)
                return Incomplete(extractedPieces, "明确的尺寸单位（mm、cm 或 m）");
            var lengths = new[] { "l", "w", "h" }.Select((name, index) =>
                double.Parse(match.Groups[name].Value, CultureInfo.InvariantCulture) * UnitFactor(units[index].Length > 0 ? units[index] : specified[0])).ToArray();
            var volume = lengths.Aggregate(1d, (value, side) => value * side);
            if (lengths.Any(x => !double.IsFinite(x) || x <= 0) || !double.IsFinite(volume) || volume <= 0)
                return Incomplete(extractedPieces, "有效的长宽高尺寸");

            var start = content.LastIndexOfAny(['，', ',', '；', ';', '\n'], Math.Max(0, match.Index - 1)) + 1;
            var end = content.IndexOfAny(['，', ',', '；', ';', '\n'], match.Index + match.Length);
            if (end < 0) end = content.Length;
            var segment = content[start..end];
            var totals = TotalQuantity.Matches(segment).Cast<Match>().ToArray();
            var counts = Quantity.Matches(segment).Cast<Match>()
                .Where(x => !totals.Any(t => x.Index >= t.Index && x.Index + x.Length <= t.Index + t.Length))
                .Select(x => int.Parse(x.Groups["n"].Value, CultureInfo.InvariantCulture)).Distinct().ToArray();
            if (matches.Count(x => x.Index >= start && x.Index + x.Length <= end) > 1 && counts.Length == 1)
                counts = [];
            if (counts.Length > 1) return Incomplete(extractedPieces, "每组尺寸对应的明确件数");
            groups.Add((volume, counts.Length == 1 ? counts[0] : null));
        }

        var totalsInText = TotalQuantity.Matches(content).Cast<Match>().Select(x => int.Parse(x.Groups["n"].Value, CultureInfo.InvariantCulture)).Distinct().ToArray();
        if (totalsInText.Length > 1) return new(extractedPieces, null, "一致的总件数");
        var sourcePieces = extractedPieces ?? (totalsInText.Length == 1 ? totalsInText[0]
            : QuoteInquiryParser.Parse(content).TryGetValue("piece", out var parsed) ? (int)parsed : (int?)null);
        if (groups.Any(x => x.Count is null))
        {
            if (groups.All(x => x.Count is null) && sourcePieces == groups.Count)
                groups = groups.Select(x => (x.Volume, (int?)1)).ToList();
            else if (groups.Count == 1 && sourcePieces is > 0 && PerPiece.IsMatch(content))
                groups[0] = (groups[0].Volume, sourcePieces);
            else return Incomplete(sourcePieces ?? extractedPieces, "每组尺寸对应的件数，或与总件数一致的逐件尺寸清单");
        }
        var countedPieces = groups.Sum(x => (long)x.Count!.Value);
        if (countedPieces is <= 0 or > int.MaxValue || sourcePieces is > 0 && countedPieces != sourcePieces)
            return new(sourcePieces ?? extractedPieces, null, "尺寸清单件数与总件数一致的资料");
        var pieces = sourcePieces ?? (int)countedPieces;
        var calculated = groups.Sum(x => x.Volume * x.Count!.Value);
        if (!double.IsFinite(calculated) || calculated <= 0) return Incomplete(pieces, "有效的尺寸和件数");
        calculated = Math.Round(calculated, 9, MidpointRounding.AwayFromZero);
        if (calculated <= 0) return Incomplete(pieces, "有效的尺寸和件数");

        if (declared is { } stated && Math.Abs(stated - calculated) > Math.Max(0.005, calculated * 0.02))
            return new(pieces, null, $"总体积申报为 {stated.ToString(CultureInfo.InvariantCulture)} m³、按尺寸计算为 {calculated.ToString(CultureInfo.InvariantCulture)} m³，需确认正确值");
        return new(pieces, declared ?? calculated, null);
    }

    private static double UnitFactor(string unit) => unit.ToLowerInvariant() switch
    {
        "毫米" or "mm" => 0.001,
        "厘米" or "cm" => 0.01,
        "米" or "m" => 1,
        _ => throw new ArgumentException("无效尺寸单位。", nameof(unit))
    };
}
