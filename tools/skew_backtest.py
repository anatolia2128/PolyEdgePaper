#!/usr/bin/env python3
"""Momentum + yarı-varyans skew kuralı backtest'i (features.csv, resmi pencere sonuçları, fee dahil).

Kural (gönderideki tarifin birebir uygulaması; parametreler ÖNCEDEN sabit, sonuca bakıp değiştirme):
  momentum = (BTC_şimdi - BTC_30sn_önce) / BTC_30sn_önce * 100
  yarı-varyans: RV+ = Σ max(r,0)², RV- = Σ min(r,0)²   (r = log getiri; son 120 sn, features'taki 5 sn'lik örneklerle)
  skew = (RV+ - RV-) / (RV+ + RV-)
  momentum>0 VE skew>0 → Up al;  momentum<0 VE skew<0 → Down al;  aksi hâlde bekle.
Pencere başına İLK uygun satırda (30-240. sn), tarafın ask'ından al, çözüme kadar tut.
Karşılaştırma: yalnız momentum, yalnız skew, ve "yazı-tura" tabanı (aynı satırda rastgele taraf).

Kullanım: python3 tools/skew_backtest.py /var/lib/polyedge/flow
Sınır: BTC fiyatı 5 sn'de bir örneklenir (canlı bot 1 sn'lik seriyle hesaplar); fiyat için kayıtlı ask kullanılır (gecikme/slippage yok).
"""
import os, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import numpy as np
import pandas as pd
import analyze as a

d = sys.argv[1] if len(sys.argv) > 1 else "/var/lib/polyedge/flow"
MOM_S, LOOK_S, LO, HI = 30.0, 120.0, 30.0, 240.0
MINP, MAXP, MAXSPREAD, RATE = 0.10, 0.90, 0.06, 0.072
fee = lambda p: p * RATE * p * (1 - p)

t, f, w = a.load(d)
out, src = a.window_outcomes(f, w)
f = f.sort_values("time").reset_index(drop=True)
tt = f.time.astype("int64").to_numpy() / 1e9
px = f.binance.to_numpy(float)
n = len(f)
mom = np.full(n, np.nan); skew = np.full(n, np.nan)
for i in range(n):
    j = np.searchsorted(tt, tt[i] - MOM_S, side="right") - 1
    if j < 0 or tt[i] - tt[j] > MOM_S + 10 or px[j] <= 0 or np.isnan(px[i]) or np.isnan(px[j]):
        continue
    mom[i] = (px[i] - px[j]) / px[j] * 100
    k0 = np.searchsorted(tt, tt[i] - LOOK_S, side="left")
    seg_t, seg = tt[k0:i + 1], px[k0:i + 1]
    if len(seg) < 15 or seg_t[-1] - seg_t[0] < LOOK_S - 20 or np.diff(seg_t).max() > 15 or np.isnan(seg).any():
        continue
    r = np.diff(np.log(seg))
    up, dn = np.sum(np.maximum(r, 0) ** 2), np.sum(np.minimum(r, 0) ** 2)
    if up + dn > 0:
        skew[i] = (up - dn) / (up + dn)
f["mom"], f["skew"] = mom, skew
f = f.merge(out, left_on="slug", right_index=True, how="inner")
print(f"{d}: {f.slug.nunique()} pencere (sonuç kaynağı: {src})\n")

rng = np.random.default_rng(0)


def pick(r, strat):
    if np.isnan(r.mom):
        return None
    sm = 1 if r.mom > 0 else (-1 if r.mom < 0 else 0)
    ss = 0 if np.isnan(r.skew) else (1 if r.skew > 0 else (-1 if r.skew < 0 else 0))
    if strat == "momentum": s = sm
    elif strat == "skew": s = ss
    elif strat == "momentum+skew": s = sm if (sm == ss and sm != 0) else 0
    elif strat == "yazı-tura (taban)": s = (1 if rng.random() < 0.5 else -1) if sm != 0 else 0
    else: s = 0
    return None if s == 0 else ("Up" if s > 0 else "Down")


def run(strat):
    res = []
    for slug, g in f.sort_values(["slug", "elapsed_s"]).groupby("slug"):
        for r in g[(g.elapsed_s >= LO) & (g.elapsed_s <= HI)].itertuples():
            side = pick(r, strat)
            if side is None:
                continue
            ask, bid = (r.up_ask, r.up_bid) if side == "Up" else (r.down_ask, r.down_bid)
            if pd.isna(ask) or pd.isna(bid) or not (MINP <= ask <= MAXP) or ask - bid > MAXSPREAD:
                continue
            win = side == ("Up" if r.up == 1 else "Down")
            roi = ((1.0 if win else 0.0) - ask - fee(ask)) / (ask + fee(ask))
            res.append((ask, win, roi, r.elapsed_s)); break
    return pd.DataFrame(res, columns=["ask", "win", "roi", "sn"])


print(f"{'strateji':20s} {'n':>4s} {'kazanma':>8s} {'ort.fiyat':>9s} {'başabaş':>8s} {'fark':>7s} {'ROI':>8s} {'90%GA':>17s}")
for strat in ("momentum+skew", "momentum", "skew", "yazı-tura (taban)"):
    x = run(strat)
    if x.empty:
        print(f"{strat:20s} işlem yok"); continue
    be = (x.ask + x.ask.apply(fee)).mean(); lo_, hi_ = a.boot_ci(x.roi)
    print(f"{strat:20s} {len(x):4d} {x.win.mean():8.3f} {x.ask.mean():9.3f} {be:8.3f} {x.win.mean() - x.ask.mean():+7.3f} {x.roi.mean():+8.3f} [{lo_:+.3f},{hi_:+.3f}]")
print("\nOkuma: kazanma oranı ort. fiyat+fee'yi (başabaş) belirgin aşmıyorsa sinyalin net edge'i yoktur. 'yazı-tura' tabanı, maliyetin kendi başına ne kadar götürdüğünü gösterir.")
