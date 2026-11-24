namespace AiBot.Application;

public static class WeComReplies
{
    public static async Task<SendResult> SendReplyAsync(this IWeComGateway gateway, string botId, string secret,
        Guid messageId, string targetType, string target, string content, CancellationToken ct)
    {
        var stream = await gateway.UpdateReplyStreamAsync(botId, secret, messageId, content, true, ct);
        if (stream is { Ok: true } or { Stage: "stream-unknown" }) return stream;
        return await gateway.SendAsync(botId, secret, targetType, target, content, ct);
    }
}
