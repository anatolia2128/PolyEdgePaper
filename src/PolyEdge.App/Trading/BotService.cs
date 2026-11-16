using System.Globalization;
using Microsoft.Extensions.Logging;
using PolyEdge.App.Polymarket;
using PolyEdge.Core;

namespace PolyEdge.App.Trading;

/// <summary>
/// Ana döngü: pencere yaşam döngüsü, order book takibi, olasılık modeli, edge hesabı ve PAPER emir yürütme.
/// Gerçek emir göndermez.
/// </summary>
public sealed class BotService : BackgroundService
{
    private readonly BotSettings _b;
    private readonly StrategySettings _st;
    private readonly FlowSettings _fl;
    private readonly ExecutionSettings _ex;
    private readonly FeedSettings _feed;
    private readonly MarketData _d;
    private readonly IPolymarketApi _api;
    private readonly PaperBroker _broker;
    private readonly StateStore _store;
    private readonly WindowLog _windows;
    private readonly ILogger<BotService> _log;

    private readonly Dictionary<long, MarketWindow> _marketCache = new();
    private DateTimeOffset _lastPrefetch = DateTimeOffset.MinValue;
    private DateTimeOffset _lastFeatureLog = DateTimeOffset.MinValue;

    public LiveSnapshot? Live { get; private set; }

    private sealed class WindowCtx
    {
        public long StartUnix;
        public DateTimeOffset Start, End;
        public MarketWindow? Market;
        public decimal? OpenBinance;
        public decimal? OpenChainlink;
        public string? SkipReason;
        public BookSnapshot? Up, Down;
        public Task? BookTask;
        public DateTimeOffset LastBookFetch = DateTimeOffset.MinValue;
        public DateTimeOffset LastMarketTry = DateTimeOffset.MinValue;
        public int Attempts;
        public bool Done;
        public string? LastNote;
    }

    private sealed record Eval(
        double Elapsed, double SecondsLeft, decimal Binance, decimal? Chainlink, decimal Open, double RelPct, double Sigma,
        FlowFeatures Flow, double PModel, double PMarket, double PFinal,
        double? EdgeUp, double? EdgeDown);

    public BotService(BotSettings b, StrategySettings st, FlowSettings fl, ExecutionSettings ex, FeedSettings feed,
        MarketData d, IPolymarketApi api, PaperBroker broker, StateStore store, WindowLog windows, ILogger<BotService> log)
    {
        _b = b; _st = st; _fl = fl; _ex = ex; _feed = feed; _d = d; _api = api; _broker = broker; _store = store; _windows = windows; _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _log.LogInformation("Bot başladı. Mod={Mode} Bakiye={Cash:0.00} USD  (PAPER: gerçek emir gönderilmez)", _b.Mode, _broker.Cash);
        WindowCtx? cur = null;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var now = DateTimeOffset.UtcNow;
                var w = _b.WindowSeconds;
                var startUnix = now.ToUnixTimeSeconds() / w * w;

                if (cur is null || cur.StartUnix != startUnix)
                {
                    if (cur?.Market is { } fin)
                        _windows.Add(new FinishedWindow(fin.Slug, fin.ConditionId, fin.StartUnix, fin.EndUnix, cur.OpenBinance, cur.SkipReason is not null));
                    cur = NewWindow(startUnix);
                    foreach (var k in _marketCache.Keys.Where(k => k < startUnix - 2L * w).ToList()) _marketCache.Remove(k);
                }

                await PrefetchNextMarketAsync(startUnix, now, ct);
                await EnsureMarketAsync(cur, now, ct);
                EnsureBooks(cur, now, ct);

                var eval = Evaluate(cur, now);
                var status = await DecideAndActAsync(cur, eval, now, ct);
                PublishLive(cur, eval, status, now);
                LogFeatures(cur, eval, now);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _log.LogError(ex, "Ana döngüde hata (devam ediliyor).");
            }

            try { await Task.Delay(_b.LoopMs, ct); } catch (OperationCanceledException) { break; }
        }
    }

    // ---------------------------------------------------------------- pencere

    private WindowCtx NewWindow(long startUnix)
    {
        var start = DateTimeOffset.FromUnixTimeSeconds(startUnix);
        var ctx = new WindowCtx { StartUnix = startUnix, Start = start, End = start.AddSeconds(_b.WindowSeconds) };

        // Pencere açılış fiyatı: başlangıçtan hemen önceki 1 sn'lik bar.
        var ob = _d.Binance.AtOrBefore(start.AddMilliseconds(-1), TimeSpan.FromSeconds(5));
        ctx.OpenBinance = ob?.Price;
        ctx.OpenChainlink = _d.Chainlink.AtOrBefore(start, TimeSpan.FromSeconds(10))?.Price;

        if (ctx.OpenBinance is null)
            ctx.SkipReason = "Pencere açılışında Binance verisi yok (bot pencere ortasında başladı). Sonraki pencere bekleniyor.";

        _log.LogInformation("Yeni pencere {Start:HH:mm:ss} UTC | açılış Binance={Open} Chainlink={Cl}{Skip}",
            start, ctx.OpenBinance?.ToString("0.00") ?? "-", ctx.OpenChainlink?.ToString("0.00") ?? "-",
            ctx.SkipReason is null ? "" : " | ATLANDI");
        return ctx;
    }

    private async Task PrefetchNextMarketAsync(long curStart, DateTimeOffset now, CancellationToken ct)
    {
        var next = curStart + _b.WindowSeconds;
        var untilNext = (DateTimeOffset.FromUnixTimeSeconds(next) - now).TotalSeconds;
        if (untilNext > 60 || _marketCache.ContainsKey(next) || (now - _lastPrefetch).TotalSeconds < 3) return;

        _lastPrefetch = now;
        var m = await _api.GetWindowAsync(next, ct);
        if (m is not null) _marketCache[next] = m;
    }

    private async Task EnsureMarketAsync(WindowCtx ctx, DateTimeOffset now, CancellationToken ct)
    {
        if (ctx.Market is not null) return;
        if (_marketCache.TryGetValue(ctx.StartUnix, out var cached)) { ctx.Market = cached; return; }
        if ((now - ctx.LastMarketTry).TotalSeconds < 2) return;

        ctx.LastMarketTry = now;
        var m = await _api.GetWindowAsync(ctx.StartUnix, ct);
        if (m is not null) { _marketCache[ctx.StartUnix] = m; ctx.Market = m; }
    }

    private void EnsureBooks(WindowCtx ctx, DateTimeOffset now, CancellationToken ct)
    {
        if (ctx.Market is null) return;
        if (ctx.BookTask is { IsCompleted: false }) return;
        if ((now - ctx.LastBookFetch).TotalMilliseconds < _feed.BookPollMs) return;

        ctx.LastBookFetch = now;
        var m = ctx.Market;
        ctx.BookTask = Task.Run(async () =>
        {
            var upT = _api.GetBookAsync(m.UpToken, ct);
            var dnT = _api.GetBookAsync(m.DownToken, ct);
            await Task.WhenAll(upT, dnT);
            if (upT.Result is { } u) ctx.Up = u;
            if (dnT.Result is { } d) ctx.Down = d;
            if (upT.Result is not null && dnT.Result is not null) _d.Health.MarkBook();
        }, ct);
    }

    // ---------------------------------------------------------------- model

    private Eval? Evaluate(WindowCtx ctx, DateTimeOffset now)
    {
        if (ctx.OpenBinance is not { } open || ctx.Up is null || ctx.Down is null) return null;

        var last = _d.Binance.Latest;
        if (last is null || (now - last.Value.Time).TotalSeconds > 5) return null;

        var sigma = _d.Binance.SigmaPerSqrtSecond(_st.SigmaLookbackSeconds, _st.MinSigmaSamples);
        if (sigma is null) return null;

        var elapsed = (now - ctx.Start).TotalSeconds;
        var left = (ctx.End - now).TotalSeconds;
        if (left <= 0) return null;

        var flow = _d.Flow.Compute(now, _fl);
        var rel = Math.Log((double)last.Value.Price / (double)open);
        var pModel = ProbabilityModel.ProbUp(rel, sigma.Value, left, _b.WindowSeconds, flow.Score, _st);

        // Piyasanın örtük P(Up) tahmini: Up mid ile (1 - Down mid) ortalaması.
        double pMarket = 0.5;
        if (ctx.Up.Mid is { } um && ctx.Down.Mid is { } dm) pMarket = ((double)um + (1.0 - (double)dm)) / 2.0;
        else if (ctx.Up.Mid is { } um2) pMarket = (double)um2;

        var pFinal = Math.Clamp(_st.ModelWeight * pModel + (1 - _st.ModelWeight) * pMarket, 0.01, 0.99);

        double? edgeUp = EdgeFor(ctx.Up.BestAsk, pFinal);
        double? edgeDn = EdgeFor(ctx.Down.BestAsk, 1.0 - pFinal);

        var cl = _d.Chainlink.Latest?.Price;
        return new Eval(elapsed, left, last.Value.Price, cl, open, rel * 100.0, sigma.Value, flow, pModel, pMarket, pFinal, edgeUp, edgeDn);
    }

    /// <summary>Net edge = P(kazanma) - (ask + payın taker fee'si). Slippage ve gecikme ayrıca emir anında uygulanır.</summary>
    private double? EdgeFor(decimal? ask, double p) =>
        ask is { } a && a > 0 && a < 1 ? p - (double)(a + FeeModel.FeePerShare(a, _ex)) : null;

    // ---------------------------------------------------------------- karar & emir

    private async Task<string> DecideAndActAsync(WindowCtx ctx, Eval? ev, DateTimeOffset now, CancellationToken ct)
    {
        if (ctx.SkipReason is not null) return ctx.SkipReason;
        if (ctx.Market is null) return "Market bekleniyor (Gamma API).";
        if (ctx.Done) return ctx.LastNote ?? "Bu pencerede işlem tamamlandı.";
        if (ctx.Up is null || ctx.Down is null) return "Order book bekleniyor.";
        if (ev is null) return "Veri ısınıyor (Binance fiyatı / oynaklık).";

        if (ev.Elapsed < _st.EntryMinSeconds) return $"Giriş penceresi {_st.EntryMinSeconds:0}. saniyede açılır.";
        if (ev.Elapsed > _st.EntryMaxSeconds) return "Giriş penceresi kapandı; sonuç bekleniyor.";

        var bookAge = (now - new[] { ctx.Up.At, ctx.Down.At }.Min()).TotalMilliseconds;
        if (bookAge > _st.MaxBookAgeMs) return $"Order book bayat ({bookAge / 1000:0.0}s).";

        // En iyi tarafı seç.
        var cand = new List<(Side side, BookSnapshot book, double p, double edge)>();
        if (ev.EdgeUp is { } eu) cand.Add((Side.Up, ctx.Up, ev.PFinal, eu));
        if (ev.EdgeDown is { } ed) cand.Add((Side.Down, ctx.Down, 1.0 - ev.PFinal, ed));

        foreach (var c in cand.OrderByDescending(c => c.edge))
        {
            var ask = c.book.BestAsk!.Value;
            if (c.edge < _st.MinEdge) continue;
            if (ask < _st.MinEntryPrice || ask > _st.MaxEntryPrice) continue;
            if (c.book.Spread is { } sp && sp > _st.MaxSpread) continue;

            return await PlaceBuyAsync(ctx, ev, c.side, c.book, c.p, c.edge, now, ct);
        }

        return $"Edge yetersiz (Up {Pct(ev.EdgeUp)}, Down {Pct(ev.EdgeDown)}; eşik {_st.MinEdge * 100:0.0}%).";
    }

    private async Task<string> PlaceBuyAsync(WindowCtx ctx, Eval ev, Side side, BookSnapshot decisionBook,
        double pSide, double edge, DateTimeOffset decisionAt, CancellationToken ct)
    {
        ctx.Attempts++;
        var ask = decisionBook.BestAsk!.Value;

        // Kelly: ikili payoff, birim maliyet c (ask + fee), kazanırsa 1.
        var c = (double)(ask + FeeModel.FeePerShare(ask, _ex));
        var kelly = Math.Max(0.0, (pSide - c) / (1.0 - c));
        var equity = _broker.Equity();
        var stake = equity * (decimal)(_st.KellyFraction * kelly);
        stake = Math.Min(stake, equity * _st.MaxStakeFractionOfEquity);
        stake = Math.Min(stake, _st.MaxStakeUsd);
        stake = Math.Min(stake, _broker.Cash);

        if (stake < _st.MinStakeUsd)
        {
            ctx.Done = ctx.Attempts >= _st.MaxAttemptsPerWindow;
            return $"Stake çok küçük ({stake:0.00} USD).";
        }

        var shares = BookMath.FloorShares(stake / (decimal)c);
        if (shares < _ex.MinOrderShares)
        {
            ctx.Done = ctx.Attempts >= _st.MaxAttemptsPerWindow;
            return $"Pay adedi min {_ex.MinOrderShares:0.##}'in altında ({shares:0.##}).";
        }

        // Gecikme: emir matching engine'e LatencyMs sonra ulaşır; o andaki GERÇEK defterle dolar.
        await Task.Delay(_ex.LatencyMs, ct);
        var fresh = await _api.GetBookAsync(decisionBook.TokenId, ct) ?? decisionBook;
        var limit = Math.Min(0.99m, ask + _ex.MaxSlippage);

        var tctx = new TradeContext(side == Side.Up ? ev.PModel : 1.0 - ev.PModel, pSide, edge, ev.Flow.Score, ev.RelPct, decisionAt);
        var res = _broker.TryBuy(ctx.Market!, side, fresh, shares, limit, ask, tctx, DateTimeOffset.UtcNow);

        if (!res.Ok)
        {
            ctx.Done = ctx.Attempts >= _st.MaxAttemptsPerWindow;
            _log.LogInformation("Emir reddedildi ({Side}): {Reason}", side, res.Reason);
            return $"Emir dolmadı: {res.Reason}";
        }

        var t = res.Trade!;
        ctx.Done = true;
        ctx.LastNote = $"İşlem #{t.Id} açıldı: {t.Side} {t.Shares:0.##} pay @ {t.AvgPrice:0.000}";
        _store.Save(_broker.Export());
        _log.LogInformation(
            "PAPER AL #{Id} {Side} {Shares:0.##} pay @ {Avg:0.0000} (karar ask {Ask:0.000}) | maliyet {Cost:0.00} fee {Fee:0.0000} gas {Gas:0.00} slippage {Slip:0.0000} | P={P:P1} edge={E:P1} flow={F:0.00} | bakiye {Cash:0.00}",
            t.Id, t.Side, t.Shares, t.AvgPrice, ask, t.Cost, t.Fee, t.Gas, t.SlippageUsd, pSide, edge, ev.Flow.Score, _broker.Cash);
        return ctx.LastNote!;
    }

    // ---------------------------------------------------------------- gösterge & log

    private void PublishLive(WindowCtx ctx, Eval? ev, string status, DateTimeOffset now)
    {
        var elapsed = (now - ctx.Start).TotalSeconds;
        Live = new LiveSnapshot(
            now, _b.Mode, ctx.Market?.Slug, elapsed, (ctx.End - now).TotalSeconds,
            ev?.Binance ?? _d.Binance.Latest?.Price, _d.Chainlink.Latest?.Price, ctx.OpenBinance,
            ev?.RelPct, ev?.Sigma, ev?.Flow, ev?.PModel, ev?.PMarket, ev?.PFinal,
            ctx.Up?.BestBid, ctx.Up?.BestAsk, ctx.Down?.BestBid, ctx.Down?.BestAsk,
            ev?.EdgeUp, ev?.EdgeDown, status);
    }

    private void LogFeatures(WindowCtx ctx, Eval? ev, DateTimeOffset now)
    {
        if (ev is null || _b.FeatureLogSeconds <= 0 || (now - _lastFeatureLog).TotalSeconds < _b.FeatureLogSeconds) return;
        _lastFeatureLog = now;

        string D(decimal? v) => v?.ToString(CultureInfo.InvariantCulture) ?? "";
        string P(double? v) => v is null ? "" : StateStore.F(v.Value);

        _store.AppendFeature(string.Join(',',
            now.ToString("o"), ctx.Market?.Slug ?? "", StateStore.F(ev.Elapsed), D(ev.Binance), D(ev.Chainlink),
            StateStore.F(ev.RelPct), StateStore.F(ev.Sigma), StateStore.F(ev.Flow.Score),
            StateStore.F(ev.Flow.Whale30sNet), StateStore.F(ev.Flow.CvdRatio15s), StateStore.F(ev.Flow.DepthImbalance), StateStore.F(ev.Flow.LiqNet30s),
            StateStore.F(ev.PModel), StateStore.F(ev.PMarket), StateStore.F(ev.PFinal),
            D(ctx.Up?.BestBid), D(ctx.Up?.BestAsk), D(ctx.Down?.BestBid), D(ctx.Down?.BestAsk), P(ev.EdgeUp), P(ev.EdgeDown)));
    }

    private static string Pct(double? v) => v is null ? "-" : $"{v * 100:0.0}%";
}
