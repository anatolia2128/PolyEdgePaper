using System.Net;
using System.Net.WebSockets;
using System.Text;

namespace PolyEdge.App.Net;

public static class NetUtil
{
    public static HttpClient CreateHttpClient(string? proxyUrl, string baseUrl)
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(10)
        };
        if (!string.IsNullOrWhiteSpace(proxyUrl))
        {
            handler.Proxy = new WebProxy(proxyUrl);
            handler.UseProxy = true;
        }

        var client = new HttpClient(handler) { BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("PolyEdgePaper/1.0");
        return client;
    }

    public static ClientWebSocket CreateWebSocket(string? proxyUrl)
    {
        var ws = new ClientWebSocket();
        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
        if (!string.IsNullOrWhiteSpace(proxyUrl))
            ws.Options.Proxy = new WebProxy(proxyUrl);
        return ws;
    }

    /// <summary>
    /// Tam bir text mesajı okur. Sunucu kapatırsa null döner.
    /// idle süresince hiç veri gelmezse TimeoutException fırlatır (sessiz kopma tespiti).
    /// </summary>
    public static async Task<string?> ReceiveTextAsync(ClientWebSocket ws, TimeSpan idle, CancellationToken ct)
    {
        var buffer = new byte[32 * 1024];
        using var ms = new MemoryStream();
        while (true)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(idle);
            ValueWebSocketReceiveResult r;
            try
            {
                r = await ws.ReceiveAsync(buffer.AsMemory(), cts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException($"{idle.TotalSeconds:0}s boyunca veri gelmedi.");
            }

            if (r.MessageType == WebSocketMessageType.Close) return null;
            ms.Write(buffer, 0, r.Count);
            if (ms.Length > 4 * 1024 * 1024) throw new InvalidOperationException("Mesaj çok büyük.");
            if (r.EndOfMessage) break;
        }
        return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
    }

    public static Task SendTextAsync(ClientWebSocket ws, string text, CancellationToken ct) =>
        ws.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, ct);
}
