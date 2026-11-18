using System.Collections.Concurrent;
using System.Net.WebSockets;
using AiBot.Infrastructure;

namespace AiBot.Tests;

internal static class SessionTests
{
    internal static async Task UntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }
    public static async Task RunAsync(Action<bool, string> check)
    {
        var received = new ConcurrentQueue<string>(); var heartbeats = 0;
        var socket = new PersistentSocket(fragmented: true);
        using var stop = new CancellationTokenSource();
        using var session = new WeComSession(socket, "bot", "test-secret", (frame, _) => { received.Enqueue(frame.GetProperty("body").GetProperty("msgid").GetString()!); return Task.CompletedTask; },
            () => { }, () => Interlocked.Increment(ref heartbeats), _ => Task.CompletedTask,
            new(TimeSpan.FromMilliseconds(60), TimeSpan.FromMilliseconds(250)));
        var running = session.RunAsync(stop.Token);
        await UntilAsync(() => session.IsReady);
        socket.Publish("""{"cmd":"aibot_msg_callback","headers":{"req_id":"callback"},"body":{"msgid":"first"}}""");
        var receipt = await session.SendAsync("single", "user", "hello", default);
        var groupReceipt = await session.SendAsync("group", "wr-test-group", "hello", default);
        await UntilAsync(() => received.Count == 1 && Volatile.Read(ref heartbeats) > 0);
        check(receipt.Ok && groupReceipt.Ok && socket.SentChatId == "wr-test-group", "长连接复用同一已认证会话发送单聊与群聊");
        check(socket.Commands.Count(x => x == "aibot_subscribe") == 1, "连续发送不会重复认证或另开连接");
        check(received.Single() == "first" && session.IsReady, "接收回调与发送回执并行分发，支持分片和未知请求回执");
        check(heartbeats > 0 && socket.Commands.Contains("ping"), "后台心跳匹配回执并保持连接");
        socket.DropSendAck = true;
        var unknown = await session.SendAsync("single", "user", "hello", default);
        check(!unknown.Ok && unknown.Stage == "send-unknown", "主动发送超时标为未确认且不自动重试");
        socket.DropSendAck = false;
        var afterTimeout = await session.SendAsync("single", "user", "again", default);
        check(afterTimeout.Ok && socket.Commands.Count(x => x == "aibot_send_msg") == 4, "单条回执超时不破坏后续发送，也不重复提交");
        socket.Publish("""{"cmd":"aibot_event_callback","body":{"event":{"eventtype":"disconnected_event"}}}""");
        try { await running; check(false, "连接接管必须终止会话"); }
        catch (ConnectionTakenOverException) { check(!session.IsReady, "接管事件退出会话，避免反复争抢连接"); }
        var rejectedSocket = new PersistentSocket(authCode: 40001);
        using var rejected = NewSession(rejectedSocket);
        try { await rejected.RunAsync(default); check(false, "错误认证必须被拒绝"); }
        catch (AuthenticationRejectedException ex) { check(ex.Code == 40001 && rejectedSocket.Commands.Count == 1, "认证失败阻止业务消息发送并保留错误码"); }
        using var malformed = NewSession(new PersistentSocket(malformed: true));
        try { await malformed.RunAsync(default); check(false, "异常认证回执必须拒绝"); }
        catch (InvalidDataException) { check(true, "缺少 errcode 的回执不计为认证成功"); }
        var failedSocket = new PersistentSocket(sendCode: 93006);
        using var failedStop = new CancellationTokenSource(); using var failed = NewSession(failedSocket);
        var failedRunning = failed.RunAsync(failedStop.Token); await UntilAsync(() => failed.IsReady);
        var failedGroup = await failed.SendAsync("group", "group", "test", default);
        check(!failedGroup.Ok && failedGroup.Errcode == 93006 && failedGroup.Message.Contains("群聊会话无效") && !failedGroup.Errmsg!.Contains("test-secret"), "群聊业务错误、错误码与回执脱敏保持有效");
        failedStop.Cancel(); try { await failedRunning; } catch (OperationCanceledException) { }
        var silent = new PersistentSocket { DropHeartbeatAck = true };
        using var heartbeatSession = new WeComSession(silent, "bot", "test-secret", (_, _) => Task.CompletedTask, () => { }, () => { }, _ => Task.CompletedTask,
            new(TimeSpan.FromMilliseconds(30), TimeSpan.FromMilliseconds(80)));
        try { await heartbeatSession.RunAsync(default).WaitAsync(TimeSpan.FromSeconds(3)); check(false, "失去心跳必须退出会话"); }
        catch (OperationCanceledException) { check(!heartbeatSession.IsReady, "心跳超时终止失活会话，交给连接管理器重连"); }
        var oversized = new PersistentSocket(); using var sizeStop = new CancellationTokenSource(); using var sizeSession = NewSession(oversized);
        var sizeRunning = sizeSession.RunAsync(sizeStop.Token); await UntilAsync(() => sizeSession.IsReady);
        oversized.PublishBytes(new byte[1024 * 1024 + 1]);
        try { await sizeRunning; check(false, "超大帧必须拒绝"); }
        catch (InvalidDataException) { check(true, "接收帧限制为 1 MiB，避免无限内存增长"); }
    }
    private static WeComSession NewSession(WebSocket socket) => new(socket, "bot", "test-secret", (_, _) => Task.CompletedTask, () => { }, () => { }, _ => Task.CompletedTask,
        new(TimeSpan.FromMinutes(1), TimeSpan.FromMilliseconds(300)));
}
