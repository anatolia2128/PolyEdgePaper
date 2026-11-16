using PolyEdge.Core;

namespace PolyEdge.App;

/// <summary>Feed'lerin yazdığı, botun okuduğu ortak piyasa durumu.</summary>
public sealed class MarketData
{
    public FlowState Flow { get; } = new();
    /// <summary>Binance Futures son işlem fiyatı (saniyeye indirgenmiş).</summary>
    public PriceSeries Binance { get; } = new(TimeSpan.FromMinutes(20), collapseToSeconds: true);
    /// <summary>Polymarket RTDS Chainlink BTC/USD (kaynak zaman damgalı tick'ler). Marketlerin çözüm kaynağı.</summary>
    public PriceSeries Chainlink { get; } = new(TimeSpan.FromMinutes(20), collapseToSeconds: false);
    public FeedHealth Health { get; } = new();
}

public sealed class FeedHealth
{
    private long _binanceTicks, _chainlinkTicks, _bookTicks;
    public DateTimeOffset? BinanceAt => From(Interlocked.Read(ref _binanceTicks));
    public DateTimeOffset? ChainlinkAt => From(Interlocked.Read(ref _chainlinkTicks));
    public DateTimeOffset? BookAt => From(Interlocked.Read(ref _bookTicks));
    public void MarkBinance() => Interlocked.Exchange(ref _binanceTicks, DateTimeOffset.UtcNow.UtcTicks);
    public void MarkChainlink() => Interlocked.Exchange(ref _chainlinkTicks, DateTimeOffset.UtcNow.UtcTicks);
    public void MarkBook() => Interlocked.Exchange(ref _bookTicks, DateTimeOffset.UtcNow.UtcTicks);
    private static DateTimeOffset? From(long ticks) => ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
}
