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
    /// <summary>true ise her defter okumasında (BookPollMs) ilk 5 seviye + fiyatlar books.csv'ye yazılır → bağımsız replay/backtest için (tools/sniper_replay.py).</summary>
    public bool RecordBooks { get; set; }
    public int RecordBooksFromSeconds { get; set; } = 100;
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
    /// <summary>"Skew": momentum (30 sn) + yarı-varyans skew aynı yöndeyse o tarafı al, çözüme kadar tut.</summary>
    public bool IsSkew => Mode.Equals("Skew", StringComparison.OrdinalIgnoreCase);
    /// <summary>Skew: momentum = (BTC_şimdi - BTC_N_sn_önce) / BTC_N_sn_önce * 100.</summary>
    public double SkewMomentumSeconds { get; set; } = 30;
    /// <summary>Skew: yarı-varyans penceresi (sn). Tarifte belirtilmemiş; backtest ile aynı (120) sabitlendi.</summary>
    public int SkewLookbackSeconds { get; set; } = 120;
    /// <summary>Skew: sinyal eşikleri. 0 = tarifteki gibi yalnızca işaret (momentum > 0 ve skew > 0).</summary>
    public double SkewMinMomentumPct { get; set; } = 0.0;
    public double SkewMinAbsSkew { get; set; } = 0.0;
    public double SkewMinSeconds { get; set; } = 30;
    public double SkewMaxSeconds { get; set; } = 240;
    public decimal SkewStakeUsd { get; set; } = 50m;
    /// <summary>Sniper: bir tarafın best ask'ı bu fiyata ulaşınca/aşınca alım yapılır.</summary>
    public decimal SniperTriggerPrice { get; set; } = 0.95m;
    /// <summary>Sniper: bu fiyatın üstünde alım yapılmaz (limit fiyat tavanı).</summary>
    public decimal SniperMaxPrice { get; set; } = 0.99m;
    /// <summary>Sniper: aynı tarafın best bid'i en az bu olmalı (gerçek mutabakat; ince/çökmüş defteri eler).</summary>
    public decimal SniperMinBid { get; set; } = 0.90m;
    /// <summary>Sniper giriş penceresinin başı (pencere başından saniye). NOT: kayıtlı defterlerde son ~10 sn'de favori tarafta satıcı kalmıyor (ask boş), bu yüzden 290 gibi değerler dolum üretmez. Varsayılan 150.</summary>
    public double SniperMinSeconds { get; set; } = 150;
    /// <summary>Emrin pencere bitmeden ulaşması için gecikme payı bırakır (karar bu saniyeden sonra verilmez).</summary>
    public double SniperMaxSeconds { get; set; } = 296;
    /// <summary>Sniper için order book yaşı üst sınırı (son saniyelerde bayat defterle karar verilmesin).</summary>
    public int SniperMaxBookAgeMs { get; set; } = 1500;
    /// <summary>Sniper: işlem başına sabit stake (USD). Kelly kullanılmaz. Dolum, GERÇEK defter üzerinden bu bütçeyi (fee dahil) aşmayacak şekilde boyutlanır.</summary>
    public decimal SniperStakeUsd { get; set; } = 50m;
    /// <summary>Sniper: dolumdan hemen önce taze defter bu bant içinde tetik şartını hâlâ sağlamalı (ask >= tetik - bant).</summary>
    public decimal SniperRevalidateBand { get; set; } = 0.03m;
    /// <summary>Sniper doğrulaması: Chainlink (çözüm kaynağı) VE Binance seçilen tarafı desteklemeli; z = hareket / (σ·√kalan süre).</summary>
    public bool SniperRequireConfirm { get; set; }
    public double SniperMinZ { get; set; } = 2.0;
    public double SniperChainlinkMaxAgeSec { get; set; } = 5;
    /// <summary>Sniper beklenen net getiri filtresi: p_kazanma = min(Φ(z_chainlink), 1 - SniperTailProb); EV/maliyet >= SniperMinEv.</summary>
    public bool SniperUseEv { get; set; }
    public double SniperMinEv { get; set; } = 0.005;
    public double SniperTailProb { get; set; } = 0.005;
    /// <summary>EV hesabında beklenen ek slippage payı (fiyat birimi).</summary>
    public decimal SniperSlipBuffer { get; set; } = 0.003m;
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
