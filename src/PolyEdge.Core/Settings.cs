namespace PolyEdge.Core;

public sealed class BotSettings
{
    /// <summary>"Live" = gerçek Binance + Polymarket verisi (emir YOK, sadece paper). "Simulated" = çevrimdışı sahte veri.</summary>
    public string Mode { get; set; } = "Live";
    public decimal StartingBalanceUsd { get; set; } = 1000m;
    public string DataDirectory { get; set; } = "data";
    /// <summary>true ise açılışta önceki paper state silinir ve bakiye sıfırdan başlar.</summary>
    public bool ResetState { get; set; }
    /// <summary>Polymarket'in kripto marketleri 300 sn'dir. Sadece Simulated modda değiştirmeye değer.</summary>
    public int WindowSeconds { get; set; } = 300;
    public int LoopMs { get; set; } = 250;
    public int FeatureLogSeconds { get; set; } = 5;
    public bool IsSimulated => Mode.Equals("Simulated", StringComparison.OrdinalIgnoreCase);
}

public sealed class FeedSettings
{
    public string Asset { get; set; } = "btc";
    public string BinanceSymbol { get; set; } = "btcusdt";
    public string BinancePublicUrl { get; set; } = "wss://fstream.binance.com/public/stream";
    public string BinanceMarketUrl { get; set; } = "wss://fstream.binance.com/market/stream";
    public string RtdsUrl { get; set; } = "wss://ws-live-data.polymarket.com";
    public string RtdsSymbol { get; set; } = "btc/usd";
    public string GammaUrl { get; set; } = "https://gamma-api.polymarket.com";
    public string ClobUrl { get; set; } = "https://clob.polymarket.com";
    /// <summary>Opsiyonel. Örn. "http://127.0.0.1:8080" veya "socks5://127.0.0.1:1080". Boşsa doğrudan bağlanır.</summary>
    public string ProxyUrl { get; set; } = "";
    public int BookPollMs { get; set; } = 1000;
    public int IdleTimeoutSeconds { get; set; } = 20;
    public int ReconnectDelaySeconds { get; set; } = 3;
}

public sealed class FlowSettings
{
    /// <summary>Tek bir aggTrade bu notional'ı geçerse "balina" sayılır.</summary>
    public decimal WhaleNotionalUsd { get; set; } = 250_000m;
    public decimal WhaleScaleUsd { get; set; } = 2_000_000m;
    public decimal LiqScaleUsd { get; set; } = 500_000m;
    public decimal MinVolume15sUsd { get; set; } = 2_000_000m;
    public double WeightWhale { get; set; } = 0.40;
    public double WeightCvd { get; set; } = 0.30;
    public double WeightDepth { get; set; } = 0.15;
    public double WeightLiq { get; set; } = 0.15;
}

public sealed class StrategySettings
{
    /// <summary>"Model" = olasılık modeli + edge (flow/noflow örnekleri). "Sniper" = bir tarafın ask'ı tetik fiyata (örn. 0.95) ulaşınca al ve çözüme kadar tut.</summary>
    public string Mode { get; set; } = "Model";
    public bool IsSniper => Mode.Equals("Sniper", StringComparison.OrdinalIgnoreCase);
    /// <summary>Sniper: bir tarafın best ask'ı bu fiyata ulaşınca/aşınca alım yapılır.</summary>
    public decimal SniperTriggerPrice { get; set; } = 0.95m;
    /// <summary>Sniper: bu fiyatın üstünde alım yapılmaz (limit fiyat tavanı).</summary>
    public decimal SniperMaxPrice { get; set; } = 0.99m;
    /// <summary>Sniper: aynı tarafın best bid'i en az bu olmalı (gerçek mutabakat; ince/çökmüş defteri eler).</summary>
    public decimal SniperMinBid { get; set; } = 0.90m;
    /// <summary>Sniper yalnızca pencerenin SON saniyelerinde işlem açar: elapsed >= bu değer (300 sn pencerede 290 = son 10 sn).</summary>
    public double SniperMinSeconds { get; set; } = 290;
    /// <summary>Emrin pencere bitmeden ulaşması için gecikme payı bırakır (karar bu saniyeden sonra verilmez).</summary>
    public double SniperMaxSeconds { get; set; } = 296;
    /// <summary>Sniper için order book yaşı üst sınırı (son saniyelerde bayat defterle karar verilmesin).</summary>
    public int SniperMaxBookAgeMs { get; set; } = 1500;
    /// <summary>Sniper: işlem başına sabit stake (USD). Kelly kullanılmaz.</summary>
    public decimal SniperStakeUsd { get; set; } = 50m;
    public double EntryMinSeconds { get; set; } = 20;
    public double EntryMaxSeconds { get; set; } = 240;
    /// <summary>Net edge (olasılık - maliyet) bu eşiğin altındaysa işlem yok.</summary>
    public double MinEdge { get; set; } = 0.04;
    public decimal MinEntryPrice { get; set; } = 0.10m;
    public decimal MaxEntryPrice { get; set; } = 0.90m;
    public decimal MaxSpread { get; set; } = 0.06m;
    public int MaxBookAgeMs { get; set; } = 4000;
    /// <summary>Final olasılık = w*model + (1-w)*piyasa. 1 = sadece model.</summary>
    public double ModelWeight { get; set; } = 0.70;
    /// <summary>Akış skorunun (-1..1) z-skoruna katkısı.</summary>
    public double FlowWeight { get; set; } = 0.50;
    public double SigmaFloor { get; set; } = 0.00001;
    public int SigmaLookbackSeconds { get; set; } = 300;
    public int MinSigmaSamples { get; set; } = 30;
    public double KellyFraction { get; set; } = 0.25;
    public decimal MaxStakeFractionOfEquity { get; set; } = 0.05m;
    public decimal MaxStakeUsd { get; set; } = 50m;
    public decimal MinStakeUsd { get; set; } = 1m;
    public int MaxAttemptsPerWindow { get; set; } = 3;
}

public sealed class ExecutionSettings
{
    /// <summary>Karar anı ile emrin matching engine'e ulaşması arasındaki simüle gecikme.</summary>
    public int LatencyMs { get; set; } = 400;
    /// <summary>Karar anındaki best ask'a göre kabul edilen maksimum fiyat sapması (limit fiyat).</summary>
    public decimal MaxSlippage { get; set; } = 0.03m;
    public decimal MinOrderShares { get; set; } = 5m;
    /// <summary>fee = shares * price * FeeRate * (price*(1-price))^FeeExponent. Kripto marketleri için 0.072 / 1.</summary>
    public double FeeRate { get; set; } = 0.072;
    public double FeeExponent { get; set; } = 1.0;
    /// <summary>CLOB emirleri off-chain eşleşir; işlem başına gas normalde 0'dır. Stres testi için artırabilirsin.</summary>
    public decimal GasUsdPerTrade { get; set; } = 0m;
    /// <summary>Kazanan payları redeem ederken ödenen on-chain maliyet (USD).</summary>
    public decimal RedeemGasUsd { get; set; } = 0m;
}
