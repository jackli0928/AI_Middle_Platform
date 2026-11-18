using System.Text.Json;
using System.Text.Json.Nodes;
using AiBot.Domain;

namespace AiBot.Infrastructure;

public static class IncomingMessageParser
{
    public static ReceivedMessage? Parse(Guid robotId, string robotName, JsonElement frame)
    {
        if (Text(frame, "cmd") != "aibot_msg_callback" || !frame.TryGetProperty("body", out var body) || body.ValueKind != JsonValueKind.Object) return null;
        var id = Text(body, "msgid"); var type = Text(body, "msgtype"); var chatType = Text(body, "chattype");
        var sender = body.TryGetProperty("from", out var from) ? Text(from, "userid") : "";
        var chatId = Text(body, "chatid");
        var requestId = frame.TryGetProperty("headers", out var headers) ? Text(headers, "req_id") : "";
        if (id.Length is 0 or > 256 || type.Length is 0 or > 64 || chatType is not ("single" or "group") || sender.Length is 0 or > 256 || chatId.Length > 256 || requestId.Length > 256 || chatType == "group" && chatId.Length == 0) return null;
        var content = type switch {
            "text" => NestedText(body, "text"), "voice" => NestedText(body, "voice"),
            "mixed" => MixedText(body), "image" => "[图片消息]", "file" => "[文件消息]", "video" => "[视频消息]",
            _ => $"[其他消息：{type}]"
        };
        DateTimeOffset? sentAt = null;
        if (body.TryGetProperty("create_time", out var stamp) && stamp.TryGetInt64Safe(out var seconds))
        { try { sentAt = DateTimeOffset.FromUnixTimeSeconds(seconds); } catch (ArgumentOutOfRangeException) { } }
        var payload = JsonNode.Parse(body.GetRawText())!;
        Redact(payload);
        return new(robotId, robotName, id, requestId, sender, chatId, chatType, type, content,
            payload.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), sentAt);
    }
    private static string Text(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String ? field.GetString() ?? "" : "";
    private static string NestedText(JsonElement value, string name) => value.TryGetProperty(name, out var child) ? Text(child, "content") : "";
    private static string MixedText(JsonElement body)
    {
        if (!body.TryGetProperty("mixed", out var mixed) || mixed.ValueKind != JsonValueKind.Object || !mixed.TryGetProperty("msg_item", out var items) || items.ValueKind != JsonValueKind.Array) return "[图文消息]";
        return string.Join("\n", items.EnumerateArray().Select(x => Text(x, "msgtype") == "text" ? NestedText(x, "text") : "[图片消息]"));
    }
    private static void Redact(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            foreach (var property in obj.ToList())
            {
                if (property.Key.Equals("aeskey", StringComparison.OrdinalIgnoreCase) || property.Key.Equals("secret", StringComparison.OrdinalIgnoreCase)
                    || property.Key.Equals("response_url", StringComparison.OrdinalIgnoreCase) || property.Key.Equals("url", StringComparison.OrdinalIgnoreCase)
                    || property.Key.Equals("access_token", StringComparison.OrdinalIgnoreCase)) obj[property.Key] = "[已隐藏]";
                else if (property.Value is { } child) Redact(child);
            }
        }
        else if (node is JsonArray array) foreach (var item in array) if (item is not null) Redact(item);
    }
    private static bool TryGetInt64Safe(this JsonElement value, out long number)
    { number = 0; return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out number); }
}
