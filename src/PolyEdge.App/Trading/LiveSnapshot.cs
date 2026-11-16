using PolyEdge.Core;

namespace PolyEdge.App.Trading;

public sealed record LiveSnapshot(
    DateTimeOffset At,
    string Mode,
    string? Slug,
    double? ElapsedSec,
    double? SecondsLeft,
    decimal? BinancePrice,
    decimal? ChainlinkPrice,
    decimal? OpenBinance,
    double? RelMovePct,
    double? SigmaPerSqrtSec,
    FlowFeatures? Flow,
    double? PModel,
    double? PMarket,
    double? PFinal,
    decimal? UpBid, decimal? UpAsk, decimal? DownBid, decimal? DownAsk,
    double? EdgeUp, double? EdgeDown,
    string Status);
