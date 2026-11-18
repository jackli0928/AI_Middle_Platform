using System.Net.WebSockets;

namespace AiBot.Infrastructure;

public interface IWeComSocketFactory { Task<WebSocket> ConnectAsync(CancellationToken ct); }
public sealed class WeComSocketFactory : IWeComSocketFactory
{
    public async Task<WebSocket> ConnectAsync(CancellationToken ct)
    {
        var socket = new ClientWebSocket();
        try { await socket.ConnectAsync(new Uri("wss://openws.work.weixin.qq.com"), ct); return socket; }
        catch { socket.Dispose(); throw; }
    }
}
