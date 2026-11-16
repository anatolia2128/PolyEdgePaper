using Microsoft.Extensions.Logging;
using PolyEdge.App.Polymarket;
using PolyEdge.Core;

namespace PolyEdge.App.Trading;

/// <summary>
/// Biten pencerelerdeki açık paper pozisyonları kapatır. Önce Polymarket'in resmi çözümünü (Gamma) bekler;
/// gecikirse Chainlink açılış/kapanış fiyatlarıyla (marketlerin çözüm kaynağı) yaklaşık sonuç üretir.
/// </summary>
public sealed class SettlementService : BackgroundService
{
    private const int FallbackAfterSeconds = 180;

    private readonly IPolymarketApi _api;
    private readonly PaperBroker _broker;
    private readonly StateStore _store;
    private readonly MarketData _d;
    private readonly ILogger<SettlementService> _log;
    private readonly Dictionary<int, DateTimeOffset> _lastDiag = new();

    public SettlementService(IPolymarketApi api, PaperBroker broker, StateStore store, MarketData d, ILogger<SettlementService> log)
    {
        _api = api; _broker = broker; _store = store; _d = d; _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                foreach (var t in _broker.OpenTrades())
                    await TrySettleAsync(t, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { _log.LogError(ex, "Settlement döngüsünde hata."); }

            try { await Task.Delay(3000, ct); } catch (OperationCanceledException) { break; }
        }
    }

    private async Task TrySettleAsync(PaperTrade t, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var end = DateTimeOffset.FromUnixTimeSeconds(t.EndUnix);
        if (now < end.AddSeconds(2)) return;

        var res = await _api.GetResolutionAsync(t.Slug, t.ConditionId, (now - end).TotalSeconds, ct);
        var source = res.Source;
        var winner = res.Winner;

        if (winner is null && (now - end).TotalSeconds >= FallbackAfterSeconds)
        {
            var open = _d.Chainlink.AtOrBefore(DateTimeOffset.FromUnixTimeSeconds(t.StartUnix), TimeSpan.FromSeconds(15));
            var close = _d.Chainlink.AtOrBefore(end, TimeSpan.FromSeconds(15));
            if (open is not null && close is not null)
            {
                winner = close.Value.Price >= open.Value.Price ? Side.Up : Side.Down;
                source = "chainlink-fallback";
            }
        }

        if (winner is null)
        {
            // Neden bekleniyor? 30 sn'de bir logla.
            if (!_lastDiag.TryGetValue(t.Id, out var last) || (now - last).TotalSeconds >= 30)
            {
                _lastDiag[t.Id] = now;
                _log.LogInformation("Çözüm bekleniyor #{Id} ({Slug}, bitişten {Sec:0}s sonra): {Detail}", t.Id, t.Slug, (now - end).TotalSeconds, res.Detail);
            }
            return;
        }

        _broker.Settle(t, winner == Side.Up, now, source);
        _store.Save(_broker.Export());
        _store.AppendSettledTrade(t);

        var s = _broker.Stats();
        _log.LogInformation(
            "SONUÇ #{Id} {Side}: kazanan={Winner} ({Src}) | PnL {Pnl:+0.00;-0.00} USD (ROI {Roi:+0.0%;-0.0%}) | bakiye {Cash:0.00} | toplam PnL {Net:+0.00;-0.00} ({RoiTot:+0.00;-0.00}%) win-rate {Wr:0}%",
            t.Id, t.Side, winner, source, t.Pnl, t.Roi, _broker.Cash, s.NetPnl, s.RoiPct, s.WinRatePct);
    }
}
