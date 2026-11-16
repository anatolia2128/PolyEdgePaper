using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PolyEdge.App.Net;
using PolyEdge.Core;

namespace PolyEdge.App.Feeds;

/// <summary>
/// Polymarket RTDS (herkese açık, kimlik gerektirmez): Chainlink BTC/USD.
/// 5 dk marketlerinin çözüm kaynağı bu fiyattır.
/// </summary>
public sealed class ChainlinkRtdsFeed : BackgroundService
{
    private const string Topic = "crypto_prices_chainlink";
    private readonly FeedSettings _f;
    private readonly MarketData _d;
    private readonly ILogger<ChainlinkRtdsFeed> _log;

    public ChainlinkRtdsFeed(FeedSettings f, MarketData d, ILogger<ChainlinkRtdsFeed> log) { _f = f; _d = d; _log = log; }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var delay = _f.ReconnectDelaySeconds;
        while (!ct.IsCancellationRequested)
        {
            using var ws = NetUtil.CreateWebSocket(_f.ProxyUrl);
            try
            {
                _log.LogInformation("RTDS bağlanıyor: {Url}", _f.RtdsUrl);
                using (var cc = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    cc.CancelAfter(TimeSpan.FromSeconds(15));
                    await ws.ConnectAsync(new Uri(_f.RtdsUrl), cc.Token);
                }

                var sub = JsonSerializer.Serialize(new
                {
                    action = "subscribe",
                    subscriptions = new[]
                    {
                        new { topic = Topic, type = "*", filters = JsonSerializer.Serialize(new { symbol = _f.RtdsSymbol }) }
                    }
                });
                await NetUtil.SendTextAsync(ws, sub, ct);
                _log.LogInformation("RTDS abone olundu: {Topic} {Symbol}", Topic, _f.RtdsSymbol);
                delay = _f.ReconnectDelaySeconds;

                var idle = TimeSpan.FromSeconds(_f.IdleTimeoutSeconds);
                while (ws.State == System.Net.WebSockets.WebSocketState.Open)
                {
                    var msg = await NetUtil.ReceiveTextAsync(ws, idle, ct);
                    if (msg is null) break;
                    Process(msg);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { _log.LogWarning("RTDS koptu: {Msg}", ex.Message); }

            try { await Task.Delay(TimeSpan.FromSeconds(delay), ct); } catch (OperationCanceledException) { break; }
            delay = Math.Min(delay * 2, 30);
        }
    }

    private void Process(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;
            if (!root.TryGetProperty("topic", out var t) || t.GetString() != Topic) return;
            if (!root.TryGetProperty("payload", out var payload)) return;

            if (payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("data", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in arr.EnumerateArray().OrderBy(i => ReadTs(i)))
                    Apply(item);
            }
            else Apply(payload);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "RTDS mesajı ayrıştırılamadı.");
        }
    }

    private void Apply(JsonElement p)
    {
        if (p.ValueKind != JsonValueKind.Object) return;
        if (p.TryGetProperty("symbol", out var s) && s.ValueKind == JsonValueKind.String &&
            !string.Equals(s.GetString(), _f.RtdsSymbol, StringComparison.OrdinalIgnoreCase)) return;

        var ts = ReadTs(p);
        var price = ReadPrice(p);
        if (ts <= 0 || price <= 0) return;

        _d.Chainlink.Add(DateTimeOffset.FromUnixTimeMilliseconds(ts), price);
        _d.Health.MarkChainlink();
    }

    private static long ReadTs(JsonElement p)
    {
        if (!p.TryGetProperty("timestamp", out var e)) return 0;
        if (e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out var n)) return n;
        if (e.ValueKind == JsonValueKind.String && long.TryParse(e.GetString(), out var m)) return m;
        return 0;
    }

    /// <summary>"value" / "price" alanları normal USD'dir. full_accuracy_value (E18) sadece yedek olarak ve ölçek kontrolüyle kullanılır.</summary>
    private static decimal ReadPrice(JsonElement p)
    {
        foreach (var name in new[] { "value", "price" })
            if (p.TryGetProperty(name, out var e) && TryDec(e, out var v) && v > 0) return v;

        if (p.TryGetProperty("full_accuracy_value", out var f) && TryDec(f, out var big) && big > 0)
            return big > 1_000_000_000m ? big / 1_000_000_000_000_000_000m : big;

        return 0m;
    }

    private static bool TryDec(JsonElement e, out decimal v)
    {
        v = 0;
        var text = e.ValueKind == JsonValueKind.String ? e.GetString() : e.ValueKind == JsonValueKind.Number ? e.GetRawText() : null;
        return text is not null && decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
    }
}
