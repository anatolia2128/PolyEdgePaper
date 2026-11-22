namespace PolyEdge.Core;

/// <summary>Zaman sıralı fiyat serisi (thread-safe). Binance için saniyeye indirgenmiş, Chainlink için tick bazlı kullanılır.</summary>
public sealed class PriceSeries
{
    private readonly object _g = new();
    private readonly List<PricePoint> _pts = new();
    private readonly TimeSpan _retention;
    private readonly bool _collapseToSeconds;
    private DateTimeOffset _lastPrune = DateTimeOffset.MinValue;

    public PriceSeries(TimeSpan retention, bool collapseToSeconds)
    {
        _retention = retention;
        _collapseToSeconds = collapseToSeconds;
    }

    public void Add(DateTimeOffset time, decimal price)
    {
        if (price <= 0) return;
        lock (_g)
        {
            if (_collapseToSeconds)
                time = DateTimeOffset.FromUnixTimeSeconds(time.ToUnixTimeSeconds());

            if (_pts.Count == 0 || time > _pts[^1].Time)
            {
                _pts.Add(new PricePoint(time, price));
            }
            else if (time == _pts[^1].Time)
            {
                _pts[^1] = new PricePoint(time, price);
            }
            else
            {
                // Geç gelen tick (ör. RTDS snapshot): sıralı yere ekle.
                var idx = LowerBound(time);
                if (idx < _pts.Count && _pts[idx].Time == time) _pts[idx] = new PricePoint(time, price);
                else _pts.Insert(idx, new PricePoint(time, price));
            }

            if (time - _lastPrune > TimeSpan.FromSeconds(30))
            {
                _lastPrune = time;
                var cutoff = _pts[^1].Time - _retention;
                var n = 0;
                while (n < _pts.Count && _pts[n].Time < cutoff) n++;
                if (n > 0) _pts.RemoveRange(0, n);
            }
        }
    }

    public PricePoint? Latest
    {
        get { lock (_g) return _pts.Count > 0 ? _pts[^1] : null; }
    }

    /// <summary>t anında veya öncesindeki son fiyat; maxStale'den eskiyse null.</summary>
    public PricePoint? AtOrBefore(DateTimeOffset t, TimeSpan maxStale)
    {
        lock (_g)
        {
            if (_pts.Count == 0) return null;
            var idx = UpperBound(t) - 1;
            if (idx < 0) return null;
            var p = _pts[idx];
            return t - p.Time <= maxStale ? p : null;
        }
    }

    /// <summary>
    /// Üstel ağırlıklı (yarı ömür 60 sn) saniye başına log-getiri oynaklığı: sqrt(Σ w·r² / Σ w·dt).
    /// Yeterli örnek yoksa null.
    /// </summary>
    public double? SigmaPerSqrtSecond(int lookbackSeconds, int minSamples)
    {
        lock (_g)
        {
            if (_pts.Count < minSamples + 1) return null;
            var end = _pts[^1].Time;
            double num = 0, den = 0;
            var samples = 0;
            for (var i = _pts.Count - 1; i > 0; i--)
            {
                var age = (end - _pts[i].Time).TotalSeconds;
                if (age > lookbackSeconds) break;
                var dt = (_pts[i].Time - _pts[i - 1].Time).TotalSeconds;
                if (dt <= 0) continue;
                var r = Math.Log((double)_pts[i].Price / (double)_pts[i - 1].Price);
                var w = Math.Pow(0.5, age / 60.0);
                num += w * r * r;
                den += w * dt;
                samples++;
            }
            if (samples < minSamples || den <= 0) return null;
            return Math.Sqrt(num / den);
        }
    }

    /// <summary>
    /// Son lookbackSeconds'taki log getirilerden yarı-varyans skew: (RV+ - RV-) / (RV+ + RV-),
    /// RV+ = Σ max(r,0)², RV- = Σ min(r,0)². Veri yetersiz ya da aralıklıysa null.
    /// </summary>
    public double? SemivarianceSkew(int lookbackSeconds, int minReturns)
    {
        lock (_g)
        {
            if (_pts.Count < minReturns + 1) return null;
            var end = _pts[^1].Time;
            double up = 0, dn = 0, maxGap = 0;
            var n = 0;
            DateTimeOffset? first = null;
            for (var i = _pts.Count - 1; i > 0; i--)
            {
                if ((end - _pts[i].Time).TotalSeconds > lookbackSeconds) break;
                var dt = (_pts[i].Time - _pts[i - 1].Time).TotalSeconds;
                if (dt <= 0) continue;
                maxGap = Math.Max(maxGap, dt);
                var r = Math.Log((double)_pts[i].Price / (double)_pts[i - 1].Price);
                if (r > 0) up += r * r; else if (r < 0) dn += r * r;
                n++;
                first = _pts[i - 1].Time;
            }
            if (n < minReturns || maxGap > 10 || first is null || (end - first.Value).TotalSeconds < lookbackSeconds - 10 || up + dn <= 0) return null;
            return (up - dn) / (up + dn);
        }
    }

    private int LowerBound(DateTimeOffset t)
    {
        int lo = 0, hi = _pts.Count;
        while (lo < hi) { var m = (lo + hi) >>> 1; if (_pts[m].Time < t) lo = m + 1; else hi = m; }
        return lo;
    }

    private int UpperBound(DateTimeOffset t)
    {
        int lo = 0, hi = _pts.Count;
        while (lo < hi) { var m = (lo + hi) >>> 1; if (_pts[m].Time <= t) lo = m + 1; else hi = m; }
        return lo;
    }
}
