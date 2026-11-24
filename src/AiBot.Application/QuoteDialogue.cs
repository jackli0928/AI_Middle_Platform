using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AiBot.Application;

public sealed class QuoteDialogueState
{
    public Dictionary<string, string> Fields { get; set; } = new(StringComparer.Ordinal);
    public List<string> CustomerMessages { get; set; } = [];
    public string DimensionMessage { get; set; } = "";
    public double? DeclaredVolume { get; set; }
    public bool OriginNeedsConfirmation { get; set; }
}

public sealed record QuoteDialogueStep(QuoteDialogueState State, string? Question, IReadOnlyList<Dictionary<string, object>> Calls)
{
    public bool Ready => Question is null && Calls.Count > 0;
}

public static class QuoteDialogue
{
    private static readonly string[] Required = ["destination", "piece", "gross_weight", "volume"];
    private static readonly HashSet<string> TextFields = ["origin", "destination", "cargo_type", "server_type", "packing", "customs", "delivery_time"];
    private static readonly HashSet<string> NumericFields = ["piece", "gross_weight", "volume"];
    private static readonly Regex NewTicket = new(@"新询价|另一票|另外一票|重新询价|新的一票", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Dimensions = new(@"尺寸|规格|长\s*\d.{0,20}宽\s*\d|\d\s*[x×*]\s*\d", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex TwoOrigins = new(@"深圳\s*[/／或和、]\s*广州|广州\s*[/／或和、]\s*深圳|SZX\s*[/／或和、]\s*CAN|CAN\s*[/／或和、]\s*SZX", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex PieceEvidence = new(@"件|箱|托|(?<![A-Za-z])(?:PCS|CTNS|PLTS?|PALLETS?|PIECES?|CARTONS?)(?![A-Za-z])", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex WeightEvidence = new(@"KGS?|公斤|千克", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex VolumeEvidence = new(@"CBM|M3|M³|立方|方", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex DateEvidence = new(@"\d{4}[-年/.]\d{1,2}[-月/.]\d{1,2}|\d{1,2}月\d{1,2}日", RegexOptions.Compiled);

    public static bool StartsNewTicket(string message, QuoteDialogueState? existing, IReadOnlyDictionary<string, object>? modelPatch = null)
    {
        if (existing is null || NewTicket.IsMatch(message)) return true;
        var fields = QuoteInquiryParser.Parse(message);
        if (modelPatch is not null)
            foreach (var field in modelPatch) fields.TryAdd(field.Key, field.Value);
        var route = QuoteRouteResolver.Parse(message);
        if (route.Destination is not null) fields["destination"] = route.Destination;
        return fields.ContainsKey("destination") && fields.ContainsKey("piece") && fields.ContainsKey("gross_weight")
            || fields.ContainsKey("piece") && fields.ContainsKey("gross_weight")
                && (fields.ContainsKey("volume") || Dimensions.IsMatch(message));
    }

    public static Dictionary<string, object> ParseModelPatch(string response, string customerMessage)
    {
        var text = response.Trim();
        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            var lineEnd = text.IndexOf('\n'); var closing = text.LastIndexOf("```", StringComparison.Ordinal);
            if (lineEnd < 0 || closing <= lineEnd) throw new JsonException("模型未返回完整 JSON。");
            text = text[(lineEnd + 1)..closing].Trim();
        }
        using var document = JsonDocument.Parse(text);
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty("fields", out var fields) || fields.ValueKind != JsonValueKind.Object)
            throw new JsonException("模型未返回字段对象。");
        var patch = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var property in fields.EnumerateObject())
        {
            if (!TextFields.Contains(property.Name) && !NumericFields.Contains(property.Name)) continue;
            if (property.Value.ValueKind != JsonValueKind.Object
                || !property.Value.TryGetProperty("value", out var value)
                || !property.Value.TryGetProperty("evidence", out var evidence)
                || evidence.ValueKind != JsonValueKind.String) continue;
            var source = evidence.GetString();
            if (string.IsNullOrWhiteSpace(source) || !customerMessage.Contains(source, StringComparison.OrdinalIgnoreCase)) continue;
            if (TextFields.Contains(property.Name) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
                && (property.Name != "delivery_time" || DateEvidence.IsMatch(source)))
                patch[property.Name] = value.GetString()!.Trim();
            else if (property.Name == "piece" && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var pieces) && pieces > 0
                && HasNumber(source) && PieceEvidence.IsMatch(source)) patch[property.Name] = pieces;
            else if (NumericFields.Contains(property.Name) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)
                && double.IsFinite(number) && number > 0 && HasNumber(source)
                && (property.Name == "gross_weight" ? WeightEvidence : VolumeEvidence).IsMatch(source)) patch[property.Name] = number;
        }
        return patch;
    }

    public static QuoteDialogueStep Apply(QuoteDialogueState state, string message, IReadOnlyDictionary<string, object> modelPatch)
    {
        if (state.CustomerMessages.Count >= 12 || state.CustomerMessages.Sum(x => x.Length) + message.Length > 12000)
            return new(state, "本票补充信息过长，请重新发送一条完整询价。", []);

        state.CustomerMessages.Add(message);
        var patch = modelPatch.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);
        foreach (var field in QuoteInquiryParser.Parse(message)) patch[field.Key] = field.Value;
        var route = QuoteRouteResolver.Parse(message);
        if (route.OriginNeedsConfirmation)
        {
            state.Fields.Remove("origin");
            state.OriginNeedsConfirmation = true;
        }
        if (route.Origin is not null) patch["origin"] = route.Origin;
        if (route.Destination is not null) patch["destination"] = route.Destination;
        if (TwoOrigins.IsMatch(message)) patch["origin"] = "SZX,CAN";
        else if (Regex.IsMatch(message, @"(?:深圳|SZX)\s*(?:出|起飞|机场)", RegexOptions.IgnoreCase)) patch["origin"] = "SZX";
        else if (Regex.IsMatch(message, @"(?:广州|CAN)\s*(?:出|起飞|机场)", RegexOptions.IgnoreCase)) patch["origin"] = "CAN";
        if (Regex.IsMatch(message, @"(?<![A-Za-z])(?:PLTS?|PALLETS?)(?![A-Za-z])|托盘|\d\s*托", RegexOptions.IgnoreCase)) patch["packing"] = "托盘";
        var clearService = Regex.IsMatch(message, @"不要直飞|不要求直飞|直飞转飞均可");
        var clearCargo = Regex.IsMatch(message, @"不带电|无电池|不要带电|货型不确定");

        foreach (var (name, value) in patch)
        {
            if (TextFields.Contains(name) && value is string text && text.Length is > 0 and <= 256)
            {
                if (name is "origin" or "destination") text = QuoteRouteResolver.Normalize(text) ?? text.ToUpperInvariant();
                if (name == "delivery_time" && !DateOnly.TryParseExact(text, "yyyy-MM-dd", out _)) continue;
                if (!Allowed(name, text)) continue;
                state.Fields[name] = text;
            }
            else if (name == "piece" && value is int count && count > 0) state.Fields[name] = count.ToString(CultureInfo.InvariantCulture);
            else if (name is "gross_weight" or "volume" && value is double number && double.IsFinite(number) && number > 0)
                state.Fields[name] = number.ToString(CultureInfo.InvariantCulture);
        }
        if (clearService) state.Fields.Remove("server_type");
        if (clearCargo) state.Fields.Remove("cargo_type");
        if (state.Fields.ContainsKey("origin")) state.OriginNeedsConfirmation = false;

        if (patch.TryGetValue("volume", out var suppliedVolume) && suppliedVolume is double declared)
            state.DeclaredVolume = declared;
        if (Dimensions.IsMatch(message)) state.DimensionMessage = message;
        string? dimensionClarification = null;
        if (state.DimensionMessage.Length > 0)
        {
            var pieces = state.Fields.TryGetValue("piece", out var pieceText) && int.TryParse(pieceText, out var piece) ? piece : (int?)null;
            var volume = QuoteVolumeResolver.Resolve(state.DimensionMessage, pieces, state.DeclaredVolume, trustedDeclaredVolume: true);
            if (volume.Pieces is { } resolvedPieces) state.Fields["piece"] = resolvedPieces.ToString(CultureInfo.InvariantCulture);
            if (volume.Volume is { } resolvedVolume) state.Fields["volume"] = resolvedVolume.ToString(CultureInfo.InvariantCulture);
            else state.Fields.Remove("volume");
            dimensionClarification = volume.Clarification;
        }

        var missing = Required.Where(x => !state.Fields.ContainsKey(x) && !(x == "volume" && dimensionClarification is not null)).Select(x => x switch
        {
            "destination" => "目的地机场", "piece" => "总件数", "gross_weight" => "总毛重（kg）", _ => "总体积（m³）或完整尺寸和各组件数"
        }).ToArray();
        if (missing.Length > 0 || dimensionClarification is not null || state.OriginNeedsConfirmation)
            return Question(state, string.Join("、", missing.Append(dimensionClarification)
                .Append(state.OriginNeedsConfirmation ? "起运地机场三字码" : null).Where(x => x is not null)));

        var origins = state.Fields.TryGetValue("origin", out var origin) ? origin.Split(',', StringSplitOptions.RemoveEmptyEntries) : [""];
        var calls = new List<Dictionary<string, object>>();
        foreach (var airport in origins)
        {
            var args = new Dictionary<string, object>();
            foreach (var (name, value) in state.Fields)
            {
                if (name == "origin") continue;
                args[name] = name == "piece" ? int.Parse(value, CultureInfo.InvariantCulture)
                    : name is "gross_weight" or "volume" ? double.Parse(value, CultureInfo.InvariantCulture) : value;
            }
            if (airport.Length > 0) args["origin"] = airport;
            args["all_content"] = string.Join("\n", state.CustomerMessages);
            calls.Add(args);
        }
        return new(state, null, calls);
    }

    private static QuoteDialogueStep Question(QuoteDialogueState state, string missing) =>
        new(state, "请补充或确认询价信息：" + missing + "。直接回复缺少的信息即可。", []);

    private static bool HasNumber(string text) => text.Any(char.IsDigit)
        || text.Any(c => "零一二三四五六七八九十两半".Contains(c));

    private static bool Allowed(string name, string value) => name switch
    {
        "cargo_type" => value is "普货" or "带电" or "温控" or "电商" or "冷藏" or "危险品",
        "server_type" => value is "转飞" or "直飞",
        "packing" => value is "托盘" or "卡板" or "超高托盘" or "桶" or "卷" or "袋" or "纸箱" or "木箱" or "框箱" or "捆包" or "散货",
        "customs" => value is "快件" or "9610",
        "origin" => value.Split(',').All(x => x.Length == 3 && x.All(char.IsLetter)),
        "destination" => value.Length == 3 && value.All(char.IsLetter) || value.Length is >= 2 and <= 12 && value.All(char.IsLetter),
        _ => true
    };
}
