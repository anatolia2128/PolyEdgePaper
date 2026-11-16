namespace PolyEdge.Core;

/// <summary>
/// Paper broker: gerçek order book'u yürür (slippage), Polymarket taker fee'si ve gas uygular,
/// settlement'ta kasayı günceller. Gerçek para / emir YOKTUR.
/// </summary>
public sealed class PaperBroker
{
    private readonly object _g = new();
    private readonly ExecutionSettings _ex;
    private readonly List<PaperTrade> _trades = new();
    private int _nextId = 1;

    public decimal StartingBalance { get; private set; }
    public decimal Cash { get; private set; }

    public PaperBroker(ExecutionSettings ex, decimal startingBalance)
    {
        _ex = ex;
        StartingBalance = startingBalance;
        Cash = startingBalance;
    }

    public void Load(PersistedState s)
    {
        lock (_g)
        {
            StartingBalance = s.StartingBalance;
            Cash = s.Cash;
            _nextId = s.NextId;
            _trades.Clear();
            _trades.AddRange(s.Trades);
        }
    }

    public PersistedState Export()
    {
        lock (_g)
            return new PersistedState { StartingBalance = StartingBalance, Cash = Cash, NextId = _nextId, Trades = _trades.ToList() };
    }

    public IReadOnlyList<PaperTrade> OpenTrades()
    {
        lock (_g) return _trades.Where(t => t.IsOpen).ToList();
    }

    public IReadOnlyList<PaperTrade> AllTrades()
    {
        lock (_g) return _trades.ToList();
    }

    public decimal Equity()
    {
        lock (_g) return Cash + _trades.Where(t => t.IsOpen).Sum(t => t.Cost);
    }

    public BuyResult TryBuy(MarketWindow m, Side side, BookSnapshot book, decimal targetShares,
        decimal limitPrice, decimal bestAskAtDecision, TradeContext ctx, DateTimeOffset now)
    {
        lock (_g)
        {
            if (book.Asks.Count == 0) return new BuyResult(false, "Order book'ta ask yok.", null);

            var fill = BookMath.WalkAsks(book.Asks, targetShares, limitPrice, _ex);

            // Bakiye yetmiyorsa hedefi küçült (en fazla 2 deneme).
            for (var i = 0; i < 2 && fill.Shares > 0 && fill.Cost + fill.Fee + _ex.GasUsdPerTrade > Cash; i++)
            {
                var affordable = Math.Max(0m, Cash - _ex.GasUsdPerTrade) / Math.Max(0.0001m, fill.Cost + fill.Fee) * 0.995m;
                fill = BookMath.WalkAsks(book.Asks, BookMath.FloorShares(fill.Shares * affordable), limitPrice, _ex);
            }

            if (fill.Shares < _ex.MinOrderShares)
                return new BuyResult(false, $"Limit {limitPrice:0.000} içinde yeterli derinlik yok (dolan {fill.Shares:0.##} < min {_ex.MinOrderShares:0.##}).", null);

            var outlay = fill.Cost + fill.Fee + _ex.GasUsdPerTrade;
            if (outlay > Cash) return new BuyResult(false, "Yetersiz bakiye.", null);

            Cash -= outlay;

            var trade = new PaperTrade
            {
                Id = _nextId++,
                Slug = m.Slug,
                ConditionId = m.ConditionId,
                Side = side,
                StartUnix = m.StartUnix,
                EndUnix = m.EndUnix,
                DecisionAt = ctx.DecisionAt,
                FilledAt = now,
                Shares = fill.Shares,
                AvgPrice = Math.Round(fill.AvgPrice, 5),
                BestAskAtDecision = bestAskAtDecision,
                SlippageUsd = Math.Round((fill.AvgPrice - bestAskAtDecision) * fill.Shares, 4),
                Cost = Math.Round(fill.Cost, 4),
                Fee = fill.Fee,
                Gas = _ex.GasUsdPerTrade,
                ModelProb = ctx.ModelProb,
                FinalProb = ctx.FinalProb,
                EdgeAtDecision = ctx.EdgeAtDecision,
                FlowScore = ctx.FlowScore,
                RelMovePct = ctx.RelMovePct
            };
            _trades.Add(trade);
            return new BuyResult(true, "OK", trade);
        }
    }

    public void Settle(PaperTrade trade, bool upWon, DateTimeOffset at, string source)
    {
        lock (_g)
        {
            if (!trade.IsOpen) return;

            var won = (trade.Side == Side.Up) == upWon;
            var payout = won ? trade.Shares : 0m;
            var redeemGas = won ? _ex.RedeemGasUsd : 0m;
            var pnl = payout - trade.Cost - trade.Fee - trade.Gas - redeemGas;

            Cash += payout - redeemGas;

            trade.SettledAt = at;
            trade.Won = won;
            trade.Payout = payout;
            trade.RedeemGas = redeemGas;
            trade.Pnl = Math.Round(pnl, 4);
            trade.Roi = trade.TotalOutlay > 0 ? Math.Round(pnl / trade.TotalOutlay, 5) : 0m;
            trade.SettleSource = source;
        }
    }

    public PaperStats Stats()
    {
        lock (_g)
        {
            var settled = _trades.Where(t => !t.IsOpen).OrderBy(t => t.SettledAt).ToList();
            var open = _trades.Count - settled.Count;
            var wins = settled.Count(t => t.Won == true);
            var losses = settled.Count - wins;

            var gp = settled.Where(t => t.Pnl > 0).Sum(t => t.Pnl!.Value);
            var gl = -settled.Where(t => t.Pnl < 0).Sum(t => t.Pnl!.Value);
            var net = settled.Sum(t => t.Pnl!.Value);
            var equity = Cash + _trades.Where(t => t.IsOpen).Sum(t => t.Cost);

            decimal peak = StartingBalance, run = StartingBalance, maxDd = 0m;
            double maxDdPct = 0;
            foreach (var t in settled)
            {
                run += t.Pnl!.Value;
                if (run > peak) peak = run;
                var dd = peak - run;
                if (dd > maxDd) maxDd = dd;
                if (peak > 0) maxDdPct = Math.Max(maxDdPct, (double)(dd / peak) * 100.0);
            }

            double brier = settled.Count > 0
                ? settled.Average(t =>
                {
                    var p = t.FinalProb;           // seçilen tarafın kazanma olasılığı
                    var y = t.Won == true ? 1.0 : 0.0;
                    return (p - y) * (p - y);
                })
                : 0;

            return new PaperStats(
                StartingBalance, Cash, equity, net,
                StartingBalance > 0 ? (double)(net / StartingBalance) * 100.0 : 0,
                _trades.Count, open, wins, losses,
                settled.Count > 0 ? wins * 100.0 / settled.Count : 0,
                _trades.Sum(t => t.Fee), _trades.Sum(t => t.Gas) + settled.Sum(t => t.RedeemGas ?? 0m),
                _trades.Sum(t => t.SlippageUsd),
                gp, gl, gl > 0 ? (double)(gp / gl) : (gp > 0 ? 999.0 : 0),
                settled.Count > 0 ? settled.Average(t => (double)(t.Roi ?? 0m)) * 100.0 : 0,
                _trades.Count > 0 ? _trades.Average(t => t.EdgeAtDecision) : 0,
                _trades.Count > 0 ? _trades.Average(t => t.FinalProb) : 0,
                brier, maxDd, maxDdPct);
        }
    }
}
