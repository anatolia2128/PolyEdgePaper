#!/usr/bin/env python3
"""PolyEdge Paper analiz scripti.

Kullanım:
  python tools/analyze.py data                 # tek veri klasörü
  python tools/analyze.py data_flow data_noflow   # iki örnek: yan yana karşılaştırma

Gerekli: pip install pandas numpy
Her klasörde trades.csv ve features.csv olmalı; windows.csv varsa gerçek sonuçlar oradan alınır.
"""
import sys, os
import numpy as np
import pandas as pd

RNG = np.random.default_rng(0)


def load(d):
    t = pd.read_csv(os.path.join(d, "trades.csv"))
    f = pd.read_csv(os.path.join(d, "features.csv"))
    f["time"] = pd.to_datetime(f["time"], utc=True)
    wpath = os.path.join(d, "windows.csv")
    w = pd.read_csv(wpath) if os.path.exists(wpath) else None
    return t, f, w


def window_outcomes(f, w):
    """slug -> up(0/1). windows.csv'deki resmi sonuç varsa onu, yoksa features'tan Chainlink ile yaklaşık sonucu kullanır."""
    if w is not None and len(w):
        x = w.copy()
        x["winner"] = x["official_winner"].where(x["official_winner"].isin(["Up", "Down"]), x["chainlink_winner"])
        x = x[x["winner"].isin(["Up", "Down"])]
        src = "windows.csv (resmi sonuç öncelikli)"
        return (x.set_index("slug")["winner"] == "Up").astype(int).rename("up").to_frame(), src
    g = f.sort_values("time").groupby("slug")
    a, b = g.first(), g.last()
    x = pd.DataFrame({"open": a.chainlink, "close": b.chainlink, "e0": a.elapsed_s, "e1": b.elapsed_s})
    x = x[(x.e0 <= 8) & (x.e1 >= 285)]
    x["up"] = (x.close >= x.open).astype(int)
    return x[["up"]], "features.csv'den yaklaşık (5 sn örnekleme; çok yakın kapanışlarda hatalı olabilir)"


def boot_ci(v, n=3000):
    v = np.asarray(v, float)
    if len(v) < 2:
        return (np.nan, np.nan)
    m = [RNG.choice(v, len(v)).mean() for _ in range(n)]
    return np.percentile(m, 5), np.percentile(m, 95)


def correct_trades(t, w):
    """windows.csv'de resmi sonuç varsa, chainlink-fallback ile kapanmış işlemlerin sonucunu/PnL'ini resmi sonuca göre düzeltir."""
    if w is None or "official_winner" not in w:
        return t, 0
    off = w[w["official_winner"].isin(["Up", "Down"])].set_index("slug")["official_winner"]
    t = t.copy()
    t["official"] = t.slug.map(off)
    off_won = (t.side == t.official)
    diff = t.official.notna() & (off_won != t.won.astype(bool))
    n = int(diff.sum())
    if n:
        redeem = t.get("redeem_gas", 0)
        payout = np.where(off_won, t.shares, 0.0)
        pnl = payout - t.cost - t.fee - t.gas - np.where(off_won, redeem, 0.0)
        t.loc[diff, "won"] = off_won[diff]
        t.loc[diff, "pnl"] = pnl[diff]
        t.loc[diff, "roi"] = (pnl / (t.cost + t.fee + t.gas))[diff]
    return t, n


def trade_summary(name, t):
    n = len(t)
    if n == 0:
        print(f"[{name}] işlem yok"); return
    win = t.won.mean()
    se = np.sqrt(0.25 / n)
    top5 = t.pnl.sort_values(ascending=False).head(5).sum()
    lo, hi = boot_ci(t.roi)
    print(f"[{name}] n={n}  kazanma={win:.3f}  ort.giriş fiyatı={t.avg_price.mean():.3f}  fark={win - t.avg_price.mean():+.3f} (±{se:.3f} hata payı)")
    print(f"        net PnL={t.pnl.sum():+.1f}  en iyi 5 işlem={top5:+.1f}  (onlarsız {t.pnl.sum() - top5:+.1f})  ort. işlem ROI={t.roi.mean():+.3f} 90%GA[{lo:+.2f},{hi:+.2f}]")
    print(f"        fee={t.fee.sum():.2f}  slippage={t.slippage_usd.sum():.2f}  Brier(final)={((t.final_prob - t.won.astype(float))**2).mean():.4f}  Brier(giriş fiyatı)={((t.avg_price - t.won.astype(float))**2).mean():.4f}")
    try:
        st = pd.to_datetime(t.slug.str.extract(r"(\d+)$")[0].astype("int64"), unit="s", utc=True)
        el = (pd.to_datetime(t.decision_at, utc=True) - st).dt.total_seconds()
        print(f"        giriş zamanı (pencere başından sn): min={el.min():.0f} medyan={el.median():.0f} maks={el.max():.0f}")
    except Exception:
        pass
    try:
        tb = pd.cut(el, [0, 30, 60, 120, 180, 240, 270, 300])
        g = t.assign(giris=tb).groupby("giris", observed=True).agg(n=("won", "size"), kazanma=("won", "mean"), ort_fiyat=("avg_price", "mean"), pnl=("pnl", "sum")).round(3)
        g["fark"] = (g.kazanma - g.ort_fiyat).round(3)
        print("        giriş saniyesine göre kırılım:")
        print("        " + g.to_string().replace("\n", "\n        "))
    except Exception:
        pass
    if "settle_source" in t:
        print("        çözüm kaynakları:", t.settle_source.value_counts().to_dict())


def model_vs_market(f, outcomes):
    f = f.merge(outcomes, left_on="slug", right_index=True)
    rows = []
    for lo, hi in [(10, 30), (30, 60), (60, 120), (120, 180), (180, 240), (240, 285)]:
        s = f[(f.elapsed_s >= lo) & (f.elapsed_s < hi)].sort_values("elapsed_s").groupby("slug").first()
        if len(s) == 0:
            continue
        y = s.up.values
        br = lambda p: float(((np.clip(p, 1e-3, 1 - 1e-3) - y) ** 2).mean())
        rows.append((f"{lo}-{hi}s", len(s), round(br(s.p_model.values), 4), round(br(s.p_market.values), 4), round(br(s.p_final.values), 4),
                     round(float(((s.p_model > .5) == (y == 1)).mean()), 3), round(float(((s.p_market > .5) == (y == 1)).mean()), 3)))
    print(pd.DataFrame(rows, columns=["geçen süre", "n", "brier_model", "brier_piyasa", "brier_final", "yön_model", "yön_piyasa"]).to_string(index=False))


def irls(X, y, it=50):
    b = np.zeros(X.shape[1])
    for _ in range(it):
        p = 1 / (1 + np.exp(-X @ b)); W = p * (1 - p) + 1e-9
        b = b + np.linalg.solve(X.T @ (X * W[:, None]), X.T @ (y - p))
    cov = np.linalg.inv(X.T @ (X * W[:, None]))
    return b, np.sqrt(np.diag(cov))


def flow_regression(f, outcomes):
    f = f.merge(outcomes, left_on="slug", right_index=True).sort_values(["slug", "elapsed_s"])
    for c in (30, 60, 120):
        s = f[(f.elapsed_s >= c - 8) & (f.elapsed_s < c + 8)].sort_values("elapsed_s").groupby("slug").first().dropna(subset=["sigma"])
        if len(s) < 30:
            continue
        z = (s.rel_move_pct / 100) / (s.sigma * np.sqrt(300 - s.elapsed_s))
        X = np.column_stack([np.ones(len(s)), z, s.flow_score]); y = s.up.values.astype(float)
        b, se = irls(X, y)
        print(f"  ~{c}s n={len(s)}: fiyat hareketi t={b[1] / se[1]:+.1f}   flow_score t={b[2] / se[2]:+.1f}   (|t|>2 ≈ anlamlı)")


def unbiased_backtest(f, outcomes, thresholds=(0.04, 0.06, 0.08, 0.10), lo=20, hi=240):
    f = f.merge(outcomes, left_on="slug", right_index=True).sort_values(["slug", "elapsed_s"])
    for thr in thresholds:
        out = []
        for slug, d in f.groupby("slug"):
            for r in d[(d.elapsed_s >= lo) & (d.elapsed_s <= hi)].itertuples():
                c = []
                for side, ask, bid, edge in (("Up", r.up_ask, r.up_bid, r.edge_up), ("Down", r.down_ask, r.down_bid, r.edge_down)):
                    if pd.notna(edge) and edge >= thr and 0.10 <= ask <= 0.90 and (ask - bid) <= 0.06:
                        c.append((edge, side, ask))
                if c:
                    e, side, ask = max(c); win = (side == "Up") == (r.up == 1)
                    fee = ask * 0.072 * ask * (1 - ask)
                    out.append((ask, win, ((1 if win else 0) - ask - fee) / (ask + fee))); break
        if out:
            a = np.array(out, float); lo_, hi_ = boot_ci(a[:, 2])
            print(f"  MinEdge {thr:.2f}: n={len(a):3d} kazanma={a[:, 1].mean():.3f} ort.ask={a[:, 0].mean():.3f} ort.ROI={a[:, 2].mean():+.3f} 90%GA[{lo_:+.2f},{hi_:+.2f}]")


def paired(ta, tb, na, nb):
    a = ta.groupby("slug").pnl.sum(); b = tb.groupby("slug").pnl.sum()
    idx = a.index.union(b.index)
    d = (a.reindex(idx).fillna(0) - b.reindex(idx).fillna(0))
    lo, hi = boot_ci(d)
    print(f"  Pencere başına PnL farkı ({na} - {nb}): ort={d.mean():+.2f} USD  90%GA[{lo:+.2f},{hi:+.2f}]  (n={len(d)} pencere; 0'ı kapsıyorsa fark kanıtlanmamış)")
    both = a.index.intersection(b.index)
    print(f"  Her ikisinin de işlem açtığı pencere: {len(both)}")


def main(dirs):
    data = [(os.path.basename(os.path.normpath(d)), *load(d)) for d in dirs]
    for name, t, f, w in data:
        print(f"\n=== {name} ===")
        t, ncorr = correct_trades(t, w)
        if ncorr:
            print(f"  NOT: {ncorr} işlemin sonucu windows.csv'deki resmi sonuca göre düzeltildi (bot yaklaşık Chainlink sonucuyla kapatmıştı).")
        trade_summary(name, t)
        outcomes, src = window_outcomes(f, w)
        print(f"  pencere sonuçları: {len(outcomes)} pencere, kaynak: {src}")
        print("\n  Model vs piyasa (tüm pencereler):"); model_vs_market(f, outcomes)
        print("\n  Sinyal katkısı (lojistik regresyon):"); flow_regression(f, outcomes)
        print("\n  Tarafsız backtest (ilk sinyal, fee dahil, gecikme/slippage hariç):"); unbiased_backtest(f, outcomes)
    if len(data) == 2:
        print("\n=== Karşılaştırma ===")
        paired(data[0][1], data[1][1], data[0][0], data[1][0])


if __name__ == "__main__":
    if len(sys.argv) < 2:
        print(__doc__); sys.exit(1)
    main(sys.argv[1:])
