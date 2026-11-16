using System.Text.Json.Serialization;

namespace PolyEdge.Core;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum Side { Up, Down }

public readonly record struct BookLevel(decimal Price, decimal Size);

public sealed class BookSnapshot
{
    public string TokenId { get; init; } = "";
    public DateTimeOffset At { get; init; }
    /// <summary>Fiyata göre azalan.</summary>
    public IReadOnlyList<BookLevel> Bids { get; init; } = Array.Empty<BookLevel>();
    /// <summary>Fiyata göre artan.</summary>
    public IReadOnlyList<BookLevel> Asks { get; init; } = Array.Empty<BookLevel>();

    public decimal? BestBid => Bids.Count > 0 ? Bids[0].Price : null;
    public decimal? BestAsk => Asks.Count > 0 ? Asks[0].Price : null;
    public decimal? Mid => BestBid is { } b && BestAsk is { } a ? (a + b) / 2m : null;
    public decimal? Spread => BestBid is { } b && BestAsk is { } a ? a - b : null;

    public static BookSnapshot Create(string tokenId, DateTimeOffset at, IEnumerable<BookLevel> bids, IEnumerable<BookLevel> asks) =>
        new()
        {
            TokenId = tokenId,
            At = at,
            Bids = bids.Where(l => l.Size > 0).OrderByDescending(l => l.Price).ToArray(),
            Asks = asks.Where(l => l.Size > 0).OrderBy(l => l.Price).ToArray()
        };
}

public readonly record struct PricePoint(DateTimeOffset Time, decimal Price);

public sealed record MarketWindow(
    long StartUnix,
    long EndUnix,
    string Slug,
    string ConditionId,
    string UpToken,
    string DownToken)
{
    public DateTimeOffset Start => DateTimeOffset.FromUnixTimeSeconds(StartUnix);
    public DateTimeOffset End => DateTimeOffset.FromUnixTimeSeconds(EndUnix);
    public string TokenFor(Side side) => side == Side.Up ? UpToken : DownToken;
}

public sealed record TradeTick(DateTimeOffset Time, decimal Price, decimal Qty, bool BuyerIsMaker)
{
    public decimal Notional => Price * Qty;
    /// <summary>+ = agresif alıcı, - = agresif satıcı.</summary>
    public decimal SignedNotional => BuyerIsMaker ? -Notional : Notional;
}

/// <summary>Side = likide edilen emrin yönü. BUY = short pozisyon likide oldu (yukarı yönlü baskı).</summary>
public sealed record LiquidationTick(DateTimeOffset Time, string Side, decimal Price, decimal Qty)
{
    public decimal Notional => Price * Qty;
}

public sealed record FlowFeatures(
    DateTimeOffset At,
    decimal Whale5sNet,
    decimal Whale30sNet,
    int WhaleCount30s,
    double CvdRatio15s,
    decimal Volume15s,
    double DepthImbalance,
    decimal LiqNet30s,
    double WhaleComponent,
    double CvdComponent,
    double DepthComponent,
    double LiqComponent,
    double Score);

public sealed record BuyFill(decimal Shares, decimal Cost, decimal Fee, decimal AvgPrice, decimal BestAsk);

public sealed record TradeContext(double ModelProb, double FinalProb, double EdgeAtDecision, double FlowScore, double RelMovePct, DateTimeOffset DecisionAt);

public sealed record BuyResult(bool Ok, string Reason, PaperTrade? Trade);

public sealed class PaperTrade
{
    public int Id { get; set; }
    public string Slug { get; set; } = "";
    public string ConditionId { get; set; } = "";
    public Side Side { get; set; }
    public long StartUnix { get; set; }
    public long EndUnix { get; set; }
    public DateTimeOffset DecisionAt { get; set; }
    public DateTimeOffset FilledAt { get; set; }
    public decimal Shares { get; set; }
    public decimal AvgPrice { get; set; }
    public decimal BestAskAtDecision { get; set; }
    /// <summary>(AvgPrice - karar anı best ask) * Shares. Hem derinlik hem gecikme kaynaklı kayma.</summary>
    public decimal SlippageUsd { get; set; }
    public decimal Cost { get; set; }
    public decimal Fee { get; set; }
    public decimal Gas { get; set; }
    public double ModelProb { get; set; }
    public double FinalProb { get; set; }
    public double EdgeAtDecision { get; set; }
    public double FlowScore { get; set; }
    public double RelMovePct { get; set; }

    public DateTimeOffset? SettledAt { get; set; }
    public bool? Won { get; set; }
    public decimal? Payout { get; set; }
    public decimal? RedeemGas { get; set; }
    public decimal? Pnl { get; set; }
    public decimal? Roi { get; set; }
    public string? SettleSource { get; set; }

    [JsonIgnore] public bool IsOpen => SettledAt is null;
    [JsonIgnore] public decimal TotalOutlay => Cost + Fee + Gas;
}

public sealed class PersistedState
{
    public decimal StartingBalance { get; set; }
    public decimal Cash { get; set; }
    public int NextId { get; set; } = 1;
    public List<PaperTrade> Trades { get; set; } = new();
}

public sealed record PaperStats(
    decimal StartingBalance,
    decimal Cash,
    decimal Equity,
    decimal NetPnl,
    double RoiPct,
    int TotalTrades,
    int OpenTrades,
    int Wins,
    int Losses,
    double WinRatePct,
    decimal FeesPaid,
    decimal GasPaid,
    decimal SlippagePaid,
    decimal GrossProfit,
    decimal GrossLoss,
    double ProfitFactor,
    double AvgTradeRoiPct,
    double AvgEdgeAtDecision,
    double AvgModelProb,
    double BrierScore,
    decimal MaxDrawdownUsd,
    double MaxDrawdownPct);
