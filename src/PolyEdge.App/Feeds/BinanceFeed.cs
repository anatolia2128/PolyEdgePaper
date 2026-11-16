using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PolyEdge.App.Net;
using PolyEdge.Core;

namespace PolyEdge.App.Feeds;

/// <summary>Binance Futures: aggTrade (balina/CVD), forceOrder (likidasyon), depth10 (defter dengesizliği).</summary>
public sealed class BinanceFeed : BackgroundService
{
    private readonly FeedSettings _f;
    private readonly MarketData _d;
    private readonly ILogger<BinanceFeed> _log;

    public BinanceFeed(FeedSettings f, MarketData d, ILogger<BinanceFeed> log) { _f = f; _d = d; _log = log; }

    protected override Task ExecuteAsync(CancellationToken ct)
    {
        var sym = _f.BinanceSymbol.ToLowerInvariant();
        var pub = $"{_f.BinancePublicUrl}?streams={sym}@depth10@100ms";
        var mkt = $"{_f.BinanceMarketUrl}?streams={sym}@aggTrade/{sym}@forceOrder";
        return Task.WhenAll(Run(pub, "binance-public", ct), Run(mkt, "binance-market", ct));
    }

    private async Task Run(string url, string name, CancellationToken ct)
    {
        var delay = _f.ReconnectDelaySeconds;
        while (!ct.IsCancellationRequested)
        {
            using var ws = NetUtil.CreateWebSocket(_f.ProxyUrl);
            try
            {
                _log.LogInformation("{Feed} bağlanıyor: {Url}", name, url);
                using (var cc = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    cc.CancelAfter(TimeSpan.FromSeconds(15));
                    await ws.ConnectAsync(new Uri(url), cc.Token);
                }
                _log.LogInformation("{Feed} bağlandı.", name);
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
            catch (Exception ex)
            {
                _log.LogWarning("{Feed} koptu: {Msg}", name, ex.Message);
            }

            try { await Task.Delay(TimeSpan.FromSeconds(delay), ct); } catch (OperationCanceledException) { break; }
            delay = Math.Min(delay * 2, 30);
        }
    }

    private void Process(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            var data = root.TryGetProperty("data", out var dd) ? dd : root;
            if (!data.TryGetProperty("e", out var ev)) return;
            var now = DateTimeOffset.UtcNow;

            switch (ev.GetString())
            {
                case "aggTrade":
                {
                    var price = Dec(data, "p");
                    var qty = Dec(data, "q");
                    var buyerIsMaker = data.GetProperty("m").GetBoolean();
                    _d.Binance.Add(now, price);
                    _d.Flow.AddTrade(new TradeTick(now, price, qty, buyerIsMaker));
                    _d.Health.MarkBinance();
                    break;
                }
                case "forceOrder":
                {
                    var o = data.GetProperty("o");
                    var qty = o.TryGetProperty("z", out var z) && Dec(z) > 0 ? Dec(z) : Dec(o, "q");
                    var price = o.TryGetProperty("ap", out var ap) && Dec(ap) > 0 ? Dec(ap) : Dec(o, "p");
                    _d.Flow.AddLiquidation(new LiquidationTick(now, o.GetProperty("S").GetString() ?? "", price, qty));
                    break;
                }
                case "depthUpdate":
                {
                    var bids = data.TryGetProperty("b", out var b) ? b : data.GetProperty("bids");
                    var asks = data.TryGetProperty("a", out var a) ? a : data.GetProperty("asks");
                    _d.Flow.SetDepth(SumNotional(bids), SumNotional(asks));
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Binance mesajı ayrıştırılamadı.");
        }
    }

    private static decimal SumNotional(JsonElement levels)
    {
        decimal sum = 0;
        foreach (var lv in levels.EnumerateArray())
            sum += Dec(lv[0]) * Dec(lv[1]);
        return sum;
    }

    private static decimal Dec(JsonElement e, string prop) => Dec(e.GetProperty(prop));
    private static decimal Dec(JsonElement e) =>
        decimal.Parse(e.ValueKind == JsonValueKind.String ? e.GetString()! : e.GetRawText(), NumberStyles.Float, CultureInfo.InvariantCulture);
}
