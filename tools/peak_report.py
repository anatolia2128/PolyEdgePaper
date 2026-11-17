#!/usr/bin/env python3
"""Tepe (peak) raporu: bakiye eğrisi, yükselişin kaynağı ve "şans mı?" simülasyonu.

Kullanım:  python3 tools/peak_report.py /var/lib/polyedge/flow
Gerekli:  pandas, numpy. Klasörde trades.csv olmalı; windows.csv / features.csv varsa piyasa rejimi de raporlanır.

UYARI: Bu rapor TEŞHİS içindir. Yükselişteki kalıpları görüp botu o döneme göre ayarlamak aşırı uyum (overfitting) yaratır;
bulduğun hipotezleri yalnızca YENİ veride (ileriye dönük) sına.
"""
import os, sys
import numpy as np
import pandas as pd

RNG = np.random.default_rng(1)
d = sys.argv[1] if len(sys.argv) > 1 else "/var/lib/polyedge/flow"
t = pd.read_csv(os.path.join(d, "trades.csv"))
t["settled_at"] = pd.to_datetime(t["settled_at"], utc=True)
t["decision_at"] = pd.to_datetime(t["decision_at"], utc=True)
t = t.sort_values("settled_at").reset_index(drop=True)
t["cum"] = t.pnl.cumsum()
t["start"] = t.slug.str.extract(r"(\d+)$")[0].astype("int64")
t["elapsed"] = (t.decision_at - pd.to_datetime(t.start, unit="s", utc=True)).dt.total_seconds()
t["hour"] = t.settled_at.dt.hour

print(f"=== {os.path.basename(os.path.normpath(d))}: {len(t)} işlem, {t.settled_at.min():%m-%d %H:%M} → {t.settled_at.max():%m-%d %H:%M} UTC ===")
ipk = int(t.cum.idxmax())
itr = int(t.cum.iloc[: ipk + 1].idxmin()) if ipk > 0 else 0
print(f"Şu an kümülatif PnL: {t.cum.iloc[-1]:+.0f}   |  tepe: {t.cum.iloc[ipk]:+.0f} ({t.settled_at[ipk]:%m-%d %H:%M} UTC, işlem #{ipk + 1})   |  tepeden sonra: {t.cum.iloc[-1] - t.cum.iloc[ipk]:+.0f}")
print(f"Maks. düşüş: {(t.cum.cummax() - t.cum).max():.0f} USD")

# ---- yükseliş dönemi (dipten tepeye) ve geri kalan
seg = t.iloc[itr + 1: ipk + 1] if ipk > itr else t.iloc[0:0]
rest = t.drop(seg.index)

def summary(x, label):
    if len(x) == 0:
        print(f"  {label}: işlem yok"); return
    top5 = x.pnl.sort_values(ascending=False).head(5).sum()
    print(f"  {label}: n={len(x):3d} kazanma={x.won.mean():.3f} ort.fiyat={x.avg_price.mean():.3f} PnL={x.pnl.sum():+8.1f}  "
          f"en iyi 5={top5:+7.1f} (onlarsız {x.pnl.sum() - top5:+8.1f})  ort.ROI={x.roi.mean():+.3f}")

print("\nYükseliş dönemi (dip → tepe) ve geri kalan:")
summary(seg, "yükseliş ")
summary(rest, "geri kalan")

if len(seg):
    print("\nYükseliş dönemi kırılımı:")
    seg = seg.copy()
    seg["fiyat"] = pd.cut(seg.avg_price, [0, 0.2, 0.35, 0.5, 0.65, 0.9])
    print(seg.groupby("fiyat", observed=True).agg(n=("won", "size"), kazanma=("won", "mean"), pnl=("pnl", "sum")).round(2).to_string())
    seg["süre"] = pd.cut(seg.elapsed, [0, 30, 60, 120, 180, 270])
    print(seg.groupby("süre", observed=True).agg(n=("won", "size"), kazanma=("won", "mean"), pnl=("pnl", "sum")).round(2).to_string())
    print(seg.groupby("side").agg(n=("won", "size"), kazanma=("won", "mean"), pnl=("pnl", "sum")).round(2).to_string())

# ---- saatlik PnL + piyasa rejimi
print("\nSaatlik PnL (UTC; Türkiye = UTC+3):")
h = t.groupby("hour").agg(n=("pnl", "size"), pnl=("pnl", "sum")).round(1)
wp = os.path.join(d, "windows.csv")
if os.path.exists(wp):
    try:
        w = pd.read_csv(wp)
        w["hour"] = pd.to_datetime(w.start_utc, utc=True).dt.hour
        w["absmove"] = w.chainlink_move_bp.abs()
        h["pencere_ort_|hareket|_bp"] = w.groupby("hour").absmove.mean().round(2)
    except Exception as e:
        print("  (windows.csv okunamadı:", e, ")")
print(h.to_string())

# ---- şans simülasyonu: fiyatlar adilse (kazanma olasılığı = giriş fiyatı) böyle bir tepe ne sıklıkta görülür?
N = 5000
sh, ap, fee, cost = t.shares.values, t.avg_price.values, t.fee.values, t.cost.values
wins = RNG.random((N, len(t))) < ap
pnl = np.where(wins, sh, 0.0) - cost - fee - t.gas.values
cum = pnl.cumsum(axis=1)
peak_obs, final_obs = t.cum.iloc[ipk], t.cum.iloc[-1]
print(f"\nŞans testi ({N} simülasyon): her işlemin kazanma olasılığı = giriş fiyatı (piyasa adil), fee ödenir.")
print(f"  Simüle tepe: medyan {np.median(cum.max(axis=1)):+.0f}, %95 {np.percentile(cum.max(axis=1), 95):+.0f}   |  gerçek tepe {peak_obs:+.0f}  → P(simüle tepe ≥ gerçek) = {(cum.max(axis=1) >= peak_obs).mean():.1%}")
print(f"  Simüle son: medyan {np.median(cum[:, -1]):+.0f}, %95 {np.percentile(cum[:, -1], 95):+.0f}   |  gerçek son {final_obs:+.0f}  → P(simüle son ≥ gerçek) = {(cum[:, -1] >= final_obs).mean():.1%}")
print("  (Olasılık yüksekse yükseliş şansla da açıklanabilir; çok düşükse piyasa fiyatlarından sapan gerçek bir şey olabilir.)")
