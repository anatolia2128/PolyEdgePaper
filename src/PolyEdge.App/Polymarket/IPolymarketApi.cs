using PolyEdge.Core;

namespace PolyEdge.App.Polymarket;

public interface IPolymarketApi
{
    /// <summary>Verilen pencere başlangıcı (unix, 300'ün katı) için market bilgisini getirir. Henüz yoksa null.</summary>
    Task<MarketWindow?> GetWindowAsync(long startUnix, CancellationToken ct);

    /// <summary>Token'ın anlık order book'u (herkese açık CLOB endpoint'i).</summary>
    Task<BookSnapshot?> GetBookAsync(string tokenId, CancellationToken ct);

    /// <summary>
    /// Market çözümünü sorgular. Winner null ise henüz çözülmemiştir; Detail neden bekleniyor bilgisini taşır (log için).
    /// secondsSinceEnd: pencere bitişinden beri geçen süre (fiyat tabanlı yedek karar için).
    /// </summary>
    Task<Resolution> GetResolutionAsync(string slug, string conditionId, double secondsSinceEnd, CancellationToken ct);
}

public sealed record Resolution(Side? Winner, string Source, string Detail);
