using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using AiBot.Application;

namespace AiBot.Infrastructure;

internal sealed class AuthenticationRejectedException(int code) : Exception { public int Code { get; } = code; }
internal sealed class ConnectionTakenOverException : Exception;
internal sealed record WeComSessionOptions(TimeSpan HeartbeatInterval, TimeSpan RequestTimeout)
{ public static WeComSessionOptions Default { get; } = new(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(10)); }

internal sealed class WeComSession(WebSocket socket, string botId, string secret,
    Func<JsonElement, CancellationToken, Task> onMessage, Action onAuthenticated, Action onHeartbeat,
    Func<CancellationToken, Task> checkLease, WeComSessionOptions? options = null) : IDisposable
{
    private readonly WeComSessionOptions settings = options ?? WeComSessionOptions.Default;
    private readonly SemaphoreSlim wireGate = new(1, 1);
    private readonly SemaphoreSlim messageGate = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<(int Code, string Message)>> pending = new();
    private readonly Channel<JsonElement> callbacks = Channel.CreateBounded<JsonElement>(new BoundedChannelOptions(64) { SingleWriter = true, SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private CancellationToken lifetime;
    private volatile bool ready;
    public bool IsReady => ready;
    public bool Matches(string id, string value) => botId == id && secret == value;

    public async Task RunAsync(CancellationToken ct)
    {
        using var transport = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var storage = new CancellationTokenSource();
        lifetime = transport.Token;
        var receiver = ReceiveAsync(transport.Token);
        var consumer = ConsumeAsync(storage.Token);
        Task? heartbeat = null;
        try
        {
            var auth = await RequestAsync("aibot_subscribe", "auth_" + Guid.NewGuid().ToString("N"), new { bot_id = botId, secret }, ct);
            if (auth.Code != 0) throw new AuthenticationRejectedException(auth.Code);
            ready = true; onAuthenticated();
            heartbeat = HeartbeatAsync(transport.Token);
            var completed = await Task.WhenAny(receiver, consumer, heartbeat);
            await completed;
            throw new WebSocketException("Connection ended.");
        }
        finally
        {
            ready = false; transport.Cancel(); socket.Abort();
            foreach (var waiter in pending.Values) waiter.TrySetException(new WebSocketException("Connection closed."));
            callbacks.Writer.TryComplete();
            // Drain callbacks already received, including during an orderly stop, for up to ten seconds.
            storage.CancelAfter(TimeSpan.FromSeconds(10));
            await IgnoreAsync(receiver); if (heartbeat is not null) await IgnoreAsync(heartbeat); await IgnoreAsync(consumer);
        }
    }
    public async Task<SendResult> SendAsync(string targetType, string targetId, string content, CancellationToken ct)
    {
        if (!ready) return new(false, "offline", "机器人长连接尚未就绪，请查看接收消息页面的连接状态。");
        if (!await messageGate.WaitAsync(0, ct)) return new(false, "busy", "该机器人已有发送请求正在等待回执，请稍后再试。");
        var attempted = false; var id = "send_" + Guid.NewGuid().ToString("N");
        try
        {
            var receipt = await RequestAsync("aibot_send_msg", id, new { chatid = targetId, msgtype = "markdown", markdown = new { content } }, ct, () => attempted = true);
            var message = receipt.Code == 0 ? targetType == "group" ? "企业微信已接受消息，请到目标群聊中查看。" : "企业微信已接受消息，请到接收人的机器人会话中查看。"
                : receipt.Code == 93006 ? targetType == "group" ? "群聊会话无效（93006）：请核对回调中的 chatid，并确认机器人已加入该群。" : "接收人会话无效（93006）：请核对实际 userid、可见范围及机器人会话。"
                : receipt.Code == 846607 ? "企业微信拒绝发送（846607）：请同时核对目标会话 ID、机器人权限和发送频率或额度，避免连续重试。"
                : "企业微信拒绝发送，请根据错误码检查目标会话及机器人权限。";
            return new(receipt.Code == 0, "send", message, id, receipt.Code, receipt.Message.Replace(secret, "[已隐藏]", StringComparison.Ordinal));
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or InvalidDataException or ObjectDisposedException)
        {
            return attempted ? new(false, "send-unknown", "连接中断或等待回执超时，发送结果未确认。请先检查企微会话，避免重复发送。", id)
                : new(false, "offline", "机器人连接不可用，本次消息未提交。请查看连接状态。", id);
        }
        finally { messageGate.Release(); }
    }
    private async Task<(int Code, string Message)> RequestAsync(string cmd, string id, object? body, CancellationToken ct, Action? beforeWrite = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime);
        deadline.CancelAfter(settings.RequestTimeout);
        var waiter = new TaskCompletionSource<(int, string)>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!pending.TryAdd(id, waiter)) throw new InvalidOperationException("Duplicate request ID.");
        try
        {
            var bytes = body is null ? JsonSerializer.SerializeToUtf8Bytes(new { cmd, headers = new { req_id = id } })
                : JsonSerializer.SerializeToUtf8Bytes(new { cmd, headers = new { req_id = id }, body });
            await wireGate.WaitAsync(deadline.Token);
            try { deadline.Token.ThrowIfCancellationRequested(); beforeWrite?.Invoke(); await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, deadline.Token); }
            finally { wireGate.Release(); }
            return await waiter.Task.WaitAsync(deadline.Token);
        }
        finally { pending.TryRemove(id, out _); }
    }
    private async Task HeartbeatAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(settings.HeartbeatInterval);
        while (await timer.WaitForNextTickAsync(ct))
        {
            await checkLease(ct);
            var receipt = await RequestAsync("ping", "ping_" + Guid.NewGuid().ToString("N"), null, ct);
            if (receipt.Code != 0) throw new WebSocketException("Heartbeat rejected.");
            onHeartbeat();
        }
    }
    private async Task ReceiveAsync(CancellationToken ct)
    {
        var buffer = new byte[8192];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                using var message = new MemoryStream();
                WebSocketReceiveResult part;
                do
                {
                    part = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                    if (part.MessageType != WebSocketMessageType.Text || message.Length + part.Count > 1024 * 1024) throw new InvalidDataException("Invalid WebSocket frame.");
                    message.Write(buffer, 0, part.Count);
                } while (!part.EndOfMessage);
                using var json = JsonDocument.Parse(message.ToArray());
                var frame = json.RootElement;
                if (frame.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid frame.");
                var cmd = frame.TryGetProperty("cmd", out var command) && command.ValueKind == JsonValueKind.String ? command.GetString() : null;
                if (cmd == "aibot_event_callback")
                {
                    if (frame.TryGetProperty("body", out var body) && body.ValueKind == JsonValueKind.Object && body.TryGetProperty("event", out var ev)
                        && ev.ValueKind == JsonValueKind.Object && ev.TryGetProperty("eventtype", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() == "disconnected_event")
                        throw new ConnectionTakenOverException();
                    continue;
                }
                if (cmd == "aibot_msg_callback") { await callbacks.Writer.WriteAsync(frame.Clone(), ct); continue; }
                if (!frame.TryGetProperty("headers", out var headers) || headers.ValueKind != JsonValueKind.Object || !headers.TryGetProperty("req_id", out var req)
                    || req.ValueKind != JsonValueKind.String || !pending.TryGetValue(req.GetString() ?? "", out var waiter)) continue;
                if (!frame.TryGetProperty("errcode", out var code) || code.ValueKind != JsonValueKind.Number || !code.TryGetInt32(out var number))
                { waiter.TrySetException(new InvalidDataException("Receipt missing errcode.")); continue; }
                var error = frame.TryGetProperty("errmsg", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
                waiter.TrySetResult((number, error));
            }
        }
        catch (JsonException) { throw new InvalidDataException("Invalid JSON receipt."); }
    }
    private async Task ConsumeAsync(CancellationToken ct)
    { await foreach (var frame in callbacks.Reader.ReadAllAsync(ct)) await onMessage(frame, ct); }
    private static async Task IgnoreAsync(Task task) { try { await task; } catch { /* Cleanup must preserve the original failure. */ } }
    // Managed semaphores may still be released by an in-flight HTTP request during disconnect.
    public void Dispose() => socket.Dispose();
}
