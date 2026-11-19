using System.Globalization;
using System.Text.RegularExpressions;

namespace AiBot.Application;

public static partial class QuoteInquiryParser
{
    public static Dictionary<string, object> Parse(string content)
    {
        var fields = new Dictionary<string, object>();
        var destination = DestinationPattern().Matches(content);
        if (destination.Count == 1) fields["destination"] = destination[0].Groups[1].Value.ToUpperInvariant();

        var pieces = PiecePattern().Matches(content);
        if (pieces.Count == 1 && int.TryParse(pieces[0].Groups[1].Value, out var count) && count > 0)
            fields["piece"] = count;

        AddPositiveNumber(fields, "gross_weight", WeightPattern().Matches(content));
        AddPositiveNumber(fields, "volume", VolumePattern().Matches(content));
        return fields;
    }

    private static void AddPositiveNumber(Dictionary<string, object> fields, string name, MatchCollection matches)
    {
        if (matches.Count == 1 && double.TryParse(matches[0].Groups[1].Value, NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) && value > 0)
            fields[name] = value;
    }

    [GeneratedRegex(@"(?:目的地|到|至)\s*[：:]?\s*([A-Za-z]{3}|[\u4e00-\u9fff]{2,12})(?![A-Za-z\u4e00-\u9fff])", RegexOptions.IgnoreCase)]
    private static partial Regex DestinationPattern();

    [GeneratedRegex(@"(?<![\d.])(\d{1,9})\s*(?:件|箱|托|PCS|CTNS|PLTS)(?![A-Za-z])", RegexOptions.IgnoreCase)]
    private static partial Regex PiecePattern();

    [GeneratedRegex(@"(?<![\d.])(\d+(?:\.\d+)?)\s*(?:KG|公斤|千克)(?![A-Za-z])", RegexOptions.IgnoreCase)]
    private static partial Regex WeightPattern();

    [GeneratedRegex(@"(?<![\d.])(\d+(?:\.\d+)?)\s*(?:M3|M³|立方米|立方|方)(?![A-Za-z])", RegexOptions.IgnoreCase)]
    private static partial Regex VolumePattern();
}
