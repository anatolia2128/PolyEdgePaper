namespace PolyEdge.Core;

/// <summary>Binance akışından balina / CVD / derinlik / likidasyon özellikleri üretir.</summary>
public sealed class FlowState
{
    private static readonly TimeSpan Retention = TimeSpan.FromSeconds(70);

    private readonly object _g = new();
    private readonly Queue<TradeTick> _trades = new();
    private readonly Queue<LiquidationTick> _liqs = new();
    private decimal _bidNotional, _askNotional;

    public void AddTrade(TradeTick t)
    {
        lock (_g)
        {
            _trades.Enqueue(t);
            while (_trades.Count > 0 && t.Time - _trades.Peek().Time > Retention) _trades.Dequeue();
        }
    }

    public void AddLiquidation(LiquidationTick l)
    {
        lock (_g)
        {
            _liqs.Enqueue(l);
            while (_liqs.Count > 0 && l.Time - _liqs.Peek().Time > Retention) _liqs.Dequeue();
        }
    }

    public void SetDepth(decimal bidNotional, decimal askNotional)
    {
        lock (_g) { _bidNotional = bidNotional; _askNotional = askNotional; }
    }

    public FlowFeatures Compute(DateTimeOffset now, FlowSettings s)
    {
        decimal whale5 = 0, whale30 = 0, buy15 = 0, sell15 = 0, liqNet = 0;
        var whaleCount = 0;
        double depth;

        lock (_g)
        {
            foreach (var t in _trades)
            {
                var age = (now - t.Time).TotalSeconds;
                if (age < 0 || age > 30) continue;

                var n = t.Notional;
                if (n >= s.WhaleNotionalUsd)
                {
                    whale30 += t.SignedNotional;
                    whaleCount++;
                    if (age <= 5) whale5 += t.SignedNotional;
                }

                if (age <= 15)
                {
                    if (t.BuyerIsMaker) sell15 += n; else buy15 += n;
                }
            }

            foreach (var l in _liqs)
            {
                var age = (now - l.Time).TotalSeconds;
                if (age < 0 || age > 30) continue;
                liqNet += l.Side.Equals("BUY", StringComparison.OrdinalIgnoreCase) ? l.Notional : -l.Notional;
            }

            var total = _bidNotional + _askNotional;
            depth = total > 0 ? (double)((_bidNotional - _askNotional) / total) : 0.0;
        }

        var vol15 = buy15 + sell15;
        var cvdRatio = vol15 > 0 ? (double)((buy15 - sell15) / vol15) : 0.0;

        var wC = Math.Tanh((double)(whale30 / s.WhaleScaleUsd));
        var cC = cvdRatio * Math.Min(1.0, (double)(vol15 / s.MinVolume15sUsd));
        var dC = Math.Clamp(depth, -1.0, 1.0);
        var lC = Math.Tanh((double)(liqNet / s.LiqScaleUsd));

        var wSum = s.WeightWhale + s.WeightCvd + s.WeightDepth + s.WeightLiq;
        var score = wSum > 0
            ? (s.WeightWhale * wC + s.WeightCvd * cC + s.WeightDepth * dC + s.WeightLiq * lC) / wSum
            : 0.0;

        return new FlowFeatures(now, whale5, whale30, whaleCount, cvdRatio, vol15, depth, liqNet,
            wC, cC, dC, lC, Math.Clamp(score, -1.0, 1.0));
    }
}
