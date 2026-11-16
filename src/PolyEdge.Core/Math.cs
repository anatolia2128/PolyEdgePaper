namespace PolyEdge.Core;

public static class FeeModel
{
    /// <summary>Taker fee (USDC). Polymarket formülü: C * p * feeRate * (p*(1-p))^exponent, 4 hane yuvarlama.</summary>
    public static decimal Fee(decimal shares, decimal price, ExecutionSettings s)
    {
        if (shares <= 0 || price <= 0 || price >= 1) return 0m;
        var p = (double)price;
        var fee = (double)shares * p * s.FeeRate * Math.Pow(p * (1 - p), s.FeeExponent);
        return Math.Round((decimal)fee, 4, MidpointRounding.AwayFromZero);
    }

    public static decimal FeePerShare(decimal price, ExecutionSettings s) => Fee(1m, price, s);
}

public static class BookMath
{
    /// <summary>
    /// Ask tarafını yürüyerek (artan fiyat) hedef pay adedini limit fiyata kadar alır.
    /// Gerçek derinlikten gelen kaymayı ve seviye bazlı fee'yi hesaplar.
    /// </summary>
    public static BuyFill WalkAsks(IReadOnlyList<BookLevel> asks, decimal targetShares, decimal limitPrice, ExecutionSettings ex)
    {
        decimal remaining = targetShares, shares = 0, cost = 0, fee = 0;
        var bestAsk = asks.Count > 0 ? asks[0].Price : 0m;

        foreach (var level in asks)
        {
            if (remaining <= 0 || level.Price > limitPrice) break;
            var take = Math.Min(remaining, level.Size);
            shares += take;
            cost += take * level.Price;
            fee += FeeModel.Fee(take, level.Price, ex);
            remaining -= take;
        }

        var avg = shares > 0 ? cost / shares : 0m;
        return new BuyFill(shares, cost, fee, avg, bestAsk);
    }

    public static decimal FloorShares(decimal v) => Math.Floor(v * 100m) / 100m;
}

public static class ProbabilityModel
{
    public static double NormalCdf(double z) => 0.5 * (1.0 + Erf(z / Math.Sqrt(2.0)));

    // Abramowitz & Stegun 7.1.26 (|hata| < 1.5e-7)
    private static double Erf(double x)
    {
        var sign = Math.Sign(x);
        x = Math.Abs(x);
        const double a1 = 0.254829592, a2 = -0.284496736, a3 = 1.421413741, a4 = -1.453152027, a5 = 1.061405429, p = 0.3275911;
        var t = 1.0 / (1.0 + p * x);
        var y = 1.0 - (((((a5 * t + a4) * t) + a3) * t + a2) * t + a1) * t * Math.Exp(-x * x);
        return sign * y;
    }

    /// <summary>
    /// P(pencere sonunda fiyat >= pencere açılışı).
    /// relMove = ln(S_şimdi / S_açılış), sigma = saniye başına log-getiri std (√sn cinsinden),
    /// flowScore -1..1 (balina/akış baskısı). Akışın etkisi kalan süre azaldıkça √(τ/T) ile söner.
    /// </summary>
    public static double ProbUp(double relMove, double sigmaPerSqrtSec, double secondsLeft, double windowSeconds, double flowScore, StrategySettings s)
    {
        var tau = Math.Max(secondsLeft, 0.5);
        var sd = Math.Max(sigmaPerSqrtSec, s.SigmaFloor) * Math.Sqrt(tau);
        var z = relMove / sd + s.FlowWeight * flowScore * Math.Sqrt(Math.Min(1.0, tau / windowSeconds));
        return Math.Clamp(NormalCdf(z), 0.01, 0.99);
    }
}
