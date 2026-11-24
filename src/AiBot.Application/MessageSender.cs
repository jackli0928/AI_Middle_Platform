using System.Text.Json;
using AiBot.Domain;

namespace AiBot.Application;

public static class MessageSender
{
    public static bool IsHuman(ReceivedMessage message)
    {
        if (string.IsNullOrWhiteSpace(message.SenderUserId)) return false;
        using var json = JsonDocument.Parse(message.PayloadJson);
        if (!json.RootElement.TryGetProperty("from", out var from) || from.ValueKind != JsonValueKind.Object)
            return true;
        return !(from.TryGetProperty("is_bot", out var bot) && bot.ValueKind == JsonValueKind.True
            || from.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() == "bot"
            || from.TryGetProperty("bot_id", out var id) && id.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(id.GetString()));
    }
}
