using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.Logging;
using PolyEdge.App.Polymarket;
using PolyEdge.Core;

namespace PolyEdge.App.Trading;

public sealed record FinishedWindow(string Slug, string ConditionId, long StartUnix, long EndUnix, decimal? OpenBinance, bool Skipped);

/// <summary>Biten pencereleri (işlem açılsın açılmasın) kaydedilmek üzere kuyruğa alır.</summary>
public sealed class WindowLog
{
    private readonly ConcurrentQueue<FinishedWindow> _q = new();
    public void Add(FinishedWindow w) => _q.Enqueue(w);
    public bool TryTake(out FinishedWindow? w) => _q.TryDequeue(out w);
}

/// <summary>
/// Her biten pencere için windows.csv satırı yazar: Chainlink açılış/kapanış, Polymarket'in resmi sonucu ve kaynağı.
/// Analiz için "gerçek sonuç" kaydı budur (features.csv'den yaklaşık sonuç çıkarmaya gerek kalmaz).
/// </summary>
public sealed class WindowRecorderService : BackgroundService
{
    private const double GiveUpAfterSeconds = 900;

    private readonly WindowLog _log;
    private readonly IPolymarketApi _api;
    private readonly PaperBroker _broker;
    private readonly StateStore _store;
    private readonly MarketData _d;
    private readonly ILogger<WindowRecorderService> _logger;
    private readonly List<FinishedWindow> _pending = new();

    public WindowRecorderService(WindowLog log, IPolymarketApi api, PaperBroker broker, StateStore store, MarketData d, ILogger<WindowRecorderService> logger)
    {
        _log = log; _api = api; _broker = broker; _store = store; _d = d; _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                while (_log.TryTake(out var w)) _pending.Add(w!);

                foreach (var w in _pending.ToList())
                {
                    var end = DateTimeOffset.FromUnixTimeSeconds(w.EndUnix);
                    var since = (DateTimeOffset.UtcNow - end).TotalSeconds;
                    if (since < 5) continue;

                    var res = await _api.GetResolutionAsync(w.Slug, w.ConditionId, since, ct);
                    if (res.Winner is null && since < GiveUpAfterSeconds) continue;

                    Write(w, res, since);
                    _pending.Remove(w);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { _logger.LogError(ex, "WindowRecorder hatası."); }

            try { await Task.Delay(5000, ct); } catch (OperationCanceledException) { break; }
        }
    }

    private void Write(FinishedWindow w, Resolution res, double sinceEndSec)
    {
        var start = DateTimeOffset.FromUnixTimeSeconds(w.StartUnix);
        var end = DateTimeOffset.FromUnixTimeSeconds(w.EndUnix);
        var o = _d.Chainlink.AtOrBefore(start, TimeSpan.FromSeconds(15))?.Price;
        var c = _d.Chainlink.AtOrBefore(end, TimeSpan.FromSeconds(15))?.Price;

        string moveBp = "", clWinner = "";
        if (o is not null && c is not null)
        {
            moveBp = ((c.Value / o.Value - 1m) * 10000m).ToString("0.####", CultureInfo.InvariantCulture);
            clWinner = c.Value >= o.Value ? "Up" : "Down";
        }

        var trades = _broker.AllTrades().Count(t => t.Slug == w.Slug);
        _store.AppendWindow(string.Join(',',
            w.Slug, start.ToString("o"), end.ToString("o"),
            o?.ToString(CultureInfo.InvariantCulture) ?? "", c?.ToString(CultureInfo.InvariantCulture) ?? "",
            moveBp, clWinner,
            res.Winner?.ToString() ?? "", string.IsNullOrEmpty(res.Source) ? "none" : res.Source,
            w.OpenBinance?.ToString(CultureInfo.InvariantCulture) ?? "", w.Skipped, trades,
            res.Winner is null ? "" : sinceEndSec.ToString("0", CultureInfo.InvariantCulture)));
    }
}
