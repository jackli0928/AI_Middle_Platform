using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace AiBot.Tests;

public sealed class PersistentSocket(int authCode = 0, int sendCode = 0, bool malformed = false, bool fragmented = false) : WebSocket
{
    private readonly Channel<(byte[] Bytes, bool End)> frames = Channel.CreateUnbounded<(byte[], bool)>();
    private WebSocketState state = WebSocketState.Open;
    private (byte[] Bytes, bool End)? remaining;
    public ConcurrentQueue<string> Commands { get; } = new();
    public ConcurrentQueue<string> SentContents { get; } = new();
    public ConcurrentQueue<(string RequestId, string StreamId, string Content, bool Finish)> Streams { get; } = new();
    public string? SentChatId { get; private set; }
    public bool DropSendAck { get; set; }
    public bool DropStreamAck { get; set; }
    public bool DropHeartbeatAck { get; set; }
    public override WebSocketCloseStatus? CloseStatus => null;
    public override string? CloseStatusDescription => null;
    public override WebSocketState State => state;
    public override string? SubProtocol => null;
    public override void Abort() { state = WebSocketState.Aborted; frames.Writer.TryComplete(); }
    public override void Dispose() => Abort();
    public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken ct) { Abort(); return Task.CompletedTask; }
    public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken ct) => CloseAsync(closeStatus, statusDescription, ct);
    public void Publish(string frame) => frames.Writer.TryWrite((Encoding.UTF8.GetBytes(frame), true));
    public void PublishBytes(byte[] bytes) => frames.Writer.TryWrite((bytes, true));
    public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var json = JsonDocument.Parse(buffer.AsMemory());
        var cmd = json.RootElement.GetProperty("cmd").GetString()!;
        var id = json.RootElement.GetProperty("headers").GetProperty("req_id").GetString();
        Commands.Enqueue(cmd);
        if (cmd == "aibot_send_msg")
        {
            SentChatId = json.RootElement.GetProperty("body").GetProperty("chatid").GetString();
            SentContents.Enqueue(json.RootElement.GetProperty("body").GetProperty("markdown").GetProperty("content").GetString()!);
            if (DropSendAck) return Task.CompletedTask;
        }
        if (cmd == "aibot_respond_msg")
        {
            var body = json.RootElement.GetProperty("body");
            var stream = body.GetProperty("stream");
            Streams.Enqueue((id!, stream.GetProperty("id").GetString()!, stream.GetProperty("content").GetString()!, stream.GetProperty("finish").GetBoolean()));
            if (DropStreamAck) return Task.CompletedTask;
        }
        if (cmd == "ping" && DropHeartbeatAck) return Task.CompletedTask;
        Publish("""{"errcode":999,"headers":{"req_id":"other-request"}}""");
        var receipt = malformed ? """{"headers":{"req_id":"ID"}}""".Replace("ID", id) : JsonSerializer.Serialize(new { headers = new { req_id = id }, errcode = cmd == "aibot_subscribe" ? authCode : cmd == "ping" ? 0 : sendCode, errmsg = "server test-secret" });
        var bytes = Encoding.UTF8.GetBytes(receipt);
        if (fragmented) { frames.Writer.TryWrite((bytes[..(bytes.Length / 2)], false)); frames.Writer.TryWrite((bytes[(bytes.Length / 2)..], true)); }
        else frames.Writer.TryWrite((bytes, true));
        return Task.CompletedTask;
    }
    public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken ct)
    {
        (byte[] Bytes, bool End) part;
        try { part = remaining ?? await frames.Reader.ReadAsync(ct); }
        catch (ChannelClosedException) { throw new WebSocketException("Test socket closed."); }
        var count = Math.Min(part.Bytes.Length, buffer.Count);
        part.Bytes.AsSpan(0, count).CopyTo(buffer.AsSpan());
        remaining = count < part.Bytes.Length ? (part.Bytes[count..], part.End) : null;
        return new WebSocketReceiveResult(count, WebSocketMessageType.Text, remaining is null && part.End);
    }
}
