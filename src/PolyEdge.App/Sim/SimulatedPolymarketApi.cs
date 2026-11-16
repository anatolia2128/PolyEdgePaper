using PolyEdge.App.Polymarket;
using PolyEdge.Core;

namespace PolyEdge.App.Sim;

/// <summary>Çevrimdışı test için sahte Polymarket: gecikmeli fiyattan türetilen order book, Chainlink ile çözüm.</summary>
public sealed class SimulatedPolymarketApi : IPolymarketApi
{
    private readonly MarketData _d;
    private readonly BotSettings _b;
    private readonly StrategySettings _st;
    private readonly Random _rng = new(7);

    public SimulatedPolymarketApi(MarketData d, BotSettings b, StrategySettings st) { _d = d; _b = b; _st = st; }

    public Task<MarketWindow?> GetWindowAsync(long startUnix, CancellationToken ct) =>
        Task.FromResult<MarketWindow?>(new MarketWindow(startUnix, startUnix + _b.WindowSeconds,
            $"sim-updown-{startUnix}", "sim-condition", $"SIM-UP-{startUnix}", $"SIM-DOWN-{startUnix}"));

    public Task<BookSnapshot?> GetBookAsync(string tokenId, CancellationToken ct)
    {
        var parts = tokenId.Split('-');
        var isUp = parts[1] == "UP";
        var start = long.Parse(parts[2]);
        var startT = DateTimeOffset.FromUnixTimeSeconds(start);
        var endT = startT.AddSeconds(_b.WindowSeconds);
        var now = DateTimeOffset.UtcNow;

        var open = _d.Binance.AtOrBefore(startT.AddMilliseconds(-1), TimeSpan.FromSeconds(30));
        var seen = _d.Binance.AtOrBefore(now.AddSeconds(-1.2), TimeSpan.FromSeconds(30)); // piyasa ~1.2 sn geriden izliyor
        if (open is null || seen is null) return Task.FromResult<BookSnapshot?>(null);

        var rel = Math.Log((double)seen.Value.Price / (double)open.Value.Price);
        var sigma = _d.Binance.SigmaPerSqrtSecond(_st.SigmaLookbackSeconds, _st.MinSigmaSamples) ?? 0.0001;
        var tau = Math.Max(1.0, (endT - now).TotalSeconds);
        var z = rel / (Math.Max(sigma, _st.SigmaFloor) * Math.Sqrt(tau));
        var pUp = Math.Clamp(ProbabilityModel.NormalCdf(z) + (_rng.NextDouble() - 0.5) * 0.02, 0.03, 0.97);
        var mid = isUp ? pUp : 1 - pUp;

        var bestAsk = Math.Round((decimal)mid + 0.01m, 2);
        var bestBid = Math.Round((decimal)mid - 0.01m, 2);
        var asks = Enumerable.Range(0, 6).Select(i => new BookLevel(Math.Min(0.99m, bestAsk + 0.01m * i), 30 + (decimal)(_rng.NextDouble() * 70)));
        var bids = Enumerable.Range(0, 6).Select(i => new BookLevel(Math.Max(0.01m, bestBid - 0.01m * i), 30 + (decimal)(_rng.NextDouble() * 70)));
        return Task.FromResult<BookSnapshot?>(BookSnapshot.Create(tokenId, now, bids, asks));
    }

    public Task<Resolution> GetResolutionAsync(string slug, string conditionId, double secondsSinceEnd, CancellationToken ct)
    {
        var start = long.Parse(slug.Split('-')[^1]);
        var startT = DateTimeOffset.FromUnixTimeSeconds(start);
        var endT = startT.AddSeconds(_b.WindowSeconds);
        if (DateTimeOffset.UtcNow < endT.AddSeconds(3)) return Task.FromResult(new Resolution(null, "", "sim: pencere bitmedi"));

        var o = _d.Chainlink.AtOrBefore(startT, TimeSpan.FromSeconds(30));
        var c = _d.Chainlink.AtOrBefore(endT, TimeSpan.FromSeconds(30));
        if (o is null || c is null) return Task.FromResult(new Resolution(null, "", "sim: Chainlink verisi yok"));
        return Task.FromResult(new Resolution(c.Value.Price >= o.Value.Price ? Side.Up : Side.Down, "polymarket", "sim"));
    }
}
