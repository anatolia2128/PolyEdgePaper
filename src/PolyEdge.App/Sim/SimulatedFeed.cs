using Microsoft.Extensions.Logging;
using PolyEdge.Core;

namespace PolyEdge.App.Sim;

/// <summary>
/// ÇEVRİMDIŞI TEST: Rastgele yürüyüş fiyatı + gizli bir "balina baskısı" süreci üretir.
/// Sadece altyapının (karar, dolum, fee, settlement, dashboard) çalıştığını doğrulamak içindir;
/// buradaki kâr/zarar gerçek bir edge hakkında HİÇBİR şey söylemez.
/// </summary>
public sealed class SimulatedFeed : BackgroundService
{
    private const double SigmaPerSqrtSec = 0.00012;
    private readonly MarketData _d;
    private readonly ILogger<SimulatedFeed> _log;
    private readonly Random _rng = new(42);
    private double _price = 95_000;
    private double _pressure;

    public SimulatedFeed(MarketData d, ILogger<SimulatedFeed> log) { _d = d; _log = log; }

    private double Gauss()
    {
        var u1 = 1.0 - _rng.NextDouble();
        var u2 = _rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }

    private void Step(double dt)
    {
        _pressure += -0.5 * _pressure * dt + 0.9 * Math.Sqrt(dt) * Gauss();
        var mu = _pressure * 0.02 * SigmaPerSqrtSec;           // saniye başına log-drift
        _price *= Math.Exp(mu * dt + SigmaPerSqrtSec * Math.Sqrt(dt) * Gauss());
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Isınma: son 20 dakikalık geçmiş (sigma ve pencere açılışı hemen hazır olsun).
        var t0 = DateTimeOffset.UtcNow.AddMinutes(-20);
        for (var i = 0; i < 1200; i++)
        {
            Step(1.0);
            var t = t0.AddSeconds(i);
            _d.Binance.Add(t, (decimal)_price);
            if (i >= 2) _d.Chainlink.Add(t, (decimal)_price * (1m + (decimal)(Gauss() * 1e-5)));
        }
        _log.LogInformation("Simülasyon feed'i başladı (başlangıç fiyatı {Price:0}).", _price);

        var tick = 0;
        while (!ct.IsCancellationRequested)
        {
            Step(0.1);
            var now = DateTimeOffset.UtcNow;
            _d.Binance.Add(now, (decimal)_price);
            _d.Health.MarkBinance();

            var buyProb = 0.5 + 0.25 * Math.Tanh(_pressure);
            for (var k = 0; k < 2; k++)
            {
                var notional = Math.Exp(Math.Log(15_000) + 0.9 * Gauss());
                var isBuy = _rng.NextDouble() < buyProb;
                _d.Flow.AddTrade(new TradeTick(now, (decimal)_price, (decimal)(notional / _price), !isBuy));
            }

            if (_rng.NextDouble() < 0.004)
            {
                var whale = 300_000 + _rng.NextDouble() * 1_200_000;
                var isBuy = _rng.NextDouble() < 0.5 + 0.4 * Math.Tanh(_pressure);
                _d.Flow.AddTrade(new TradeTick(now, (decimal)_price, (decimal)(whale / _price), !isBuy));
            }

            if (_rng.NextDouble() < 0.003)
            {
                var notional = 100_000 + _rng.NextDouble() * 600_000;
                var sellLiq = _rng.NextDouble() < 0.5 - 0.3 * Math.Tanh(_pressure);
                _d.Flow.AddLiquidation(new LiquidationTick(now, sellLiq ? "SELL" : "BUY", (decimal)_price, (decimal)(notional / _price)));
            }

            var skew = 0.3 * Math.Tanh(_pressure);
            _d.Flow.SetDepth((decimal)(5_000_000 * (1 + skew)), (decimal)(5_000_000 * (1 - skew)));

            if (++tick % 10 == 0)
            {
                var lagged = _d.Binance.AtOrBefore(now.AddSeconds(-1.5), TimeSpan.FromSeconds(5));
                if (lagged is not null)
                {
                    _d.Chainlink.Add(now, lagged.Value.Price * (1m + (decimal)(Gauss() * 1e-5)));
                    _d.Health.MarkChainlink();
                }
            }

            try { await Task.Delay(100, ct); } catch (OperationCanceledException) { break; }
        }
    }
}
