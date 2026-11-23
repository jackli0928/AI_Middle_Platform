using System.Text.RegularExpressions;

namespace AiBot.Application;

public sealed record QuoteRoute(string? Origin, string? Destination, bool OriginNeedsConfirmation = false);

public static class QuoteRouteResolver
{
    private static readonly Dictionary<string, string> AirportAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["深圳"] = "SZX", ["深圳宝安"] = "SZX",
        ["广州"] = "CAN", ["广州白云"] = "CAN",
        ["法兰克福"] = "FRA", ["洛杉矶"] = "LAX"
    };

    private static readonly Regex RoutePattern = new(
        @"(?:^|[，,；;\s]|从)(?<origin>[\u4e00-\u9fff]{2,8}|[A-Za-z]{3})\s*(?:发往|发到|发去|发|飞往|飞到|飞|至|到|→|->)\s*(?<destination>[\u4e00-\u9fff]{2,12}|[A-Za-z]{3})(?![A-Za-z\u4e00-\u9fff])",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static QuoteRoute Parse(string content)
    {
        var match = RoutePattern.Match(content);
        if (!match.Success) return new(null, null);
        var destination = Normalize(match.Groups["destination"].Value);
        if (destination is null) return new(null, null);
        var rawOrigin = match.Groups["origin"].Value;
        if (rawOrigin.StartsWith('从') && rawOrigin.Length > 2) rawOrigin = rawOrigin[1..];
        var origin = Normalize(rawOrigin);
        return new(origin, destination, origin is null);
    }

    public static string? Normalize(string value)
    {
        var text = value.Trim();
        if (AirportAliases.TryGetValue(text, out var code)) return code;
        return text.Length == 3 && text.All(c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z')
            ? text.ToUpperInvariant() : null;
    }
}
