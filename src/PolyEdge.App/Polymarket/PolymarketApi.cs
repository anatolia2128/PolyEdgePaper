using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PolyEdge.App.Net;
using PolyEdge.Core;

namespace PolyEdge.App.Polymarket;

/// <summary>Sadece OKUMA: Gamma (market keşfi / çözüm) ve CLOB /book. Emir göndermez, kimlik bilgisi kullanmaz.</summary>
public sealed class PolymarketApi : IPolymarketApi
{
    private readonly FeedSettings _f;
    private readonly BotSettings _b;
    private readonly HttpClient _gamma;
    private readonly HttpClient _clob;
    private readonly ILogger<PolymarketApi> _log;

    public PolymarketApi(FeedSettings f, BotSettings b, ILogger<PolymarketApi> log)
    {
        _f = f; _b = b; _log = log;
        _gamma = NetUtil.CreateHttpClient(f.ProxyUrl, f.GammaUrl);
        _clob = NetUtil.CreateHttpClient(f.ProxyUrl, f.ClobUrl);
    }

    public static string Slug(string asset, long startUnix) => $"{asset.ToLowerInvariant()}-updown-5m-{startUnix}";

    public async Task<MarketWindow?> GetWindowAsync(long startUnix, CancellationToken ct)
    {
        var slug = Slug(_f.Asset, startUnix);
        try
        {
            using var resp = await _gamma.GetAsync($"events?slug={Uri.EscapeDataString(slug)}", ct);
            if (!resp.IsSuccessStatusCode) { _log.LogDebug("Gamma {Code} ({Slug})", (int)resp.StatusCode, slug); return null; }

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0) return null;

            var ev = doc.RootElement[0];
            if (!ev.TryGetProperty("markets", out var markets) || markets.GetArrayLength() == 0) return null;
            var m = markets[0];

            var tokens = ParseStringArray(m, "clobTokenIds");
            var outcomes = ParseStringArray(m, "outcomes");
            if (tokens.Count < 2) return null;

            var upIdx = outcomes.FindIndex(o => o.Equals("Up", StringComparison.OrdinalIgnoreCase));
            if (upIdx < 0) upIdx = 0;
            var downIdx = upIdx == 0 ? 1 : 0;

            var condition = m.TryGetProperty("conditionId", out var c) ? c.GetString() ?? "" : "";
            return new MarketWindow(startUnix, startUnix + _b.WindowSeconds, slug, condition, tokens[upIdx], tokens[downIdx]);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning("Gamma isteği başarısız ({Slug}): {Msg}  [Erişim sorunuysa README > \"Türk Telekom engeli\" bölümüne bak]", slug, ex.Message);
            return null;
        }
    }

    public async Task<BookSnapshot?> GetBookAsync(string tokenId, CancellationToken ct)
    {
        try
        {
            using var resp = await _clob.GetAsync($"book?token_id={Uri.EscapeDataString(tokenId)}", ct);
            if (!resp.IsSuccessStatusCode) return null;

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;
            return BookSnapshot.Create(tokenId, DateTimeOffset.UtcNow, ParseLevels(root, "bids"), ParseLevels(root, "asks"));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogDebug("CLOB /book başarısız: {Msg}", ex.Message);
            return null;
        }
    }

    public async Task<Resolution> GetResolutionAsync(string slug, string conditionId, double secondsSinceEnd, CancellationToken ct)
    {
        var details = new List<string>();

        // 1) CLOB: tokens[].winner — en güvenilir kaynak.
        if (!string.IsNullOrWhiteSpace(conditionId))
        {
            try
            {
                using var resp = await _clob.GetAsync($"markets/{Uri.EscapeDataString(conditionId)}", ct);
                if (resp.IsSuccessStatusCode)
                {
                    using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
                    var root = doc.RootElement;
                    var closed = root.TryGetProperty("closed", out var cl) && cl.ValueKind == JsonValueKind.True;
                    var info = new List<string>();
                    if (root.TryGetProperty("tokens", out var tokens) && tokens.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var t in tokens.EnumerateArray())
                        {
                            var outcome = t.TryGetProperty("outcome", out var o) ? o.GetString() ?? "" : "";
                            var won = t.TryGetProperty("winner", out var w) && w.ValueKind == JsonValueKind.True;
                            info.Add($"{outcome}:{(won ? "WIN" : "-")}");
                            if (won)
                            {
                                var side = SideOf(outcome);
                                if (side is not null) return new Resolution(side, "polymarket-clob", "CLOB winner");
                            }
                        }
                    }
                    details.Add($"clob closed={closed} [{string.Join(' ', info)}]");
                }
                else details.Add($"clob HTTP {(int)resp.StatusCode}");
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { details.Add("clob hata: " + ex.Message); }
        }

        // 2) Gamma: closed / umaResolutionStatus / outcomePrices
        try
        {
            using var resp = await _gamma.GetAsync($"markets?slug={Uri.EscapeDataString(slug)}", ct);
            if (resp.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
                if (doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0)
                {
                    var m = doc.RootElement[0];
                    var closed = m.TryGetProperty("closed", out var cl) && cl.ValueKind == JsonValueKind.True;
                    var uma = m.TryGetProperty("umaResolutionStatus", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null;
                    var prices = ParseStringArray(m, "outcomePrices")
                        .Select(x => decimal.TryParse(x, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : -1m).ToList();
                    var outcomes = ParseStringArray(m, "outcomes");
                    details.Add($"gamma closed={closed} uma={uma ?? "-"} prices=[{string.Join(',', prices)}]");

                    if (prices.Count >= 2 && outcomes.Count >= 2)
                    {
                        var resolved = closed || string.Equals(uma, "resolved", StringComparison.OrdinalIgnoreCase);
                        var winIdx = prices.FindIndex(p => p >= 0.99m);
                        if (winIdx >= 0 && resolved)
                        {
                            var side = SideOf(outcomes[winIdx]);
                            if (side is not null) return new Resolution(side, "polymarket-gamma", "Gamma closed/resolved");
                        }

                        // Yedek: bitişten çok sonra hâlâ net fiyat (>=0.995 / <=0.005) varsa kabul et (doğrulanmamış).
                        var sure = prices.FindIndex(p => p >= 0.995m);
                        if (sure >= 0 && secondsSinceEnd >= 240 && prices.Where((_, i) => i != sure).All(p => p <= 0.005m))
                        {
                            var side = SideOf(outcomes[sure]);
                            if (side is not null) return new Resolution(side, "gamma-prices(dogrulanmamis)", "Net fiyat, resmi kapanış bekleniyor");
                        }
                    }
                }
                else details.Add("gamma: market bulunamadı");
            }
            else details.Add($"gamma HTTP {(int)resp.StatusCode}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { details.Add("gamma hata: " + ex.Message); }

        return new Resolution(null, "", string.Join(" | ", details));
    }

    private static Side? SideOf(string outcome) =>
        outcome.Equals("Up", StringComparison.OrdinalIgnoreCase) ? Side.Up
        : outcome.Equals("Down", StringComparison.OrdinalIgnoreCase) ? Side.Down : null;

    /// <summary>Gamma bazı alanları JSON dizisi yerine "JSON içeren string" olarak döndürür.</summary>
    private static List<string> ParseStringArray(JsonElement obj, string prop)
    {
        var list = new List<string>();
        if (!obj.TryGetProperty(prop, out var e)) return list;

        JsonElement arr = e;
        JsonDocument? inner = null;
        if (e.ValueKind == JsonValueKind.String)
        {
            var s = e.GetString();
            if (string.IsNullOrWhiteSpace(s)) return list;
            inner = JsonDocument.Parse(s);
            arr = inner.RootElement;
        }

        if (arr.ValueKind == JsonValueKind.Array)
            foreach (var x in arr.EnumerateArray())
                list.Add(x.ValueKind == JsonValueKind.String ? x.GetString() ?? "" : x.GetRawText());

        inner?.Dispose();
        return list;
    }

    private static IEnumerable<BookLevel> ParseLevels(JsonElement root, string prop)
    {
        if (!root.TryGetProperty(prop, out var arr) || arr.ValueKind != JsonValueKind.Array) yield break;
        foreach (var lv in arr.EnumerateArray())
        {
            if (!decimal.TryParse(lv.GetProperty("price").GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var p)) continue;
            if (!decimal.TryParse(lv.GetProperty("size").GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var s)) continue;
            yield return new BookLevel(p, s);
        }
    }
}
