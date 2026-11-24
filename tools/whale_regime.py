#!/usr/bin/env python3
"""Balina aktivitesi ↔ kayıp ilişkisi: kayıplar balina hareketlerinin yoğun olduğu anlarda mı toplanıyor?

Her işlemi, işlem anından önceki en yakın features.csv satırıyla (≤10 sn) eşler ve şunu karşılaştırır:
  sakin   : son 30 sn'de net balina akışı ≈ 0
  aktif   : net balina akışı != 0 (içinde düşük/orta/yüksek üçlüleri)
Ayrıca volatilite (sigma) üçlüleri ve zaman bölünmesi (1. yarı / 2. yarı) ile tutarlılık kontrolü yapılır.

Kullanım: python3 tools/whale_regime.py /var/lib/polyedge/sniper
UYARI: Bu bir HİPOTEZ ÜRETME/denetleme aracıdır. Gözlemden doğan bir kuralı aynı veride kanıt sayma; yeni veride (ileriye dönük) sına.
"""
import os, sys
from math import comb
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import numpy as np
import pandas as pd
import analyze as a

d = sys.argv[1] if len(sys.argv) > 1 else "/var/lib/polyedge/sniper"
t, f, w = a.load(d)
t, _ = a.correct_trades(t, w)
t["decision_at"] = pd.to_datetime(t.decision_at, utc=True)
f = f.sort_values("time")
t = t.sort_values("decision_at")
m = pd.merge_asof(t, f[["time", "whale30_net", "liq30_net", "cvd15", "depth_imb", "sigma"]],
                  left_on="decision_at", right_on="time", direction="backward", tolerance=pd.Timedelta("10s"))
m = m.dropna(subset=["whale30_net"]).copy()
m["aw"] = m.whale30_net.abs()
m["kayip"] = ~m.won.astype(bool)
print(f"=== {os.path.basename(os.path.normpath(d))}: işlem={len(t)}, eşleşen={len(m)} ===\n")


def fisher(a1, n1, a2, n2):
    """iki yönlü Fisher kesin test: grup1 kayıp a1/n1, grup2 kayıp a2/n2"""
    n = n1 + n2; k = a1 + a2
    pr = lambda x: comb(n1, x) * comb(n2, k - x) / comb(n, k)
    p0 = pr(a1)
    return sum(pr(x) for x in range(max(0, k - n2), min(n1, k) + 1) if pr(x) <= p0 + 1e-12)


def row(name, g):
    if len(g) == 0:
        print(f"  {name:28s} işlem yok"); return
    lo, hi = a.boot_ci(g.roi)
    print(f"  {name:28s} n={len(g):4d} kazanma={g.won.mean():.3f} ort.fiyat={g.avg_price.mean():.3f} kayıp={int(g.kayip.sum()):3d} ({g.kayip.mean():.1%}) ort.ROI={g.roi.mean():+.3f} [{lo:+.2f},{hi:+.2f}] PnL={g.pnl.sum():+9.1f}")


calm = m[m.aw < 1]
act = m[m.aw >= 1]
print("Balina aktivitesi (son 30 sn, net |USD|):")
row("sakin (net ≈ 0)", calm)
row("aktif (net ≠ 0)", act)
if len(act) >= 9:
    q = act.aw.quantile([1 / 3, 2 / 3]).to_numpy()
    row(f"aktif düşük  (< {q[0] / 1000:,.0f}k)", act[act.aw < q[0]])
    row(f"aktif orta   ({q[0] / 1000:,.0f}k-{q[1] / 1000:,.0f}k)", act[(act.aw >= q[0]) & (act.aw < q[1])])
    row(f"aktif yüksek (≥ {q[1] / 1000:,.0f}k)", act[act.aw >= q[1]])
if len(calm) and len(act):
    print(f"\n  Kayıp oranı farkı sakin ↔ aktif: {calm.kayip.mean():.1%} ↔ {act.kayip.mean():.1%}   Fisher p = {fisher(int(calm.kayip.sum()), len(calm), int(act.kayip.sum()), len(act)):.3f}")

print("\nVolatilite (sigma) üçlüleri (balina aktivitesinin yerine geçen bir açıklama olabilir):")
mm = m.dropna(subset=["sigma"]).copy()
if len(mm) >= 9:
    mm["vol"] = pd.qcut(mm.sigma, 3, labels=["düşük vol", "orta vol", "yüksek vol"])
    for k, g in mm.groupby("vol", observed=True):
        row(str(k), g)
    print("\nÇapraz (balina × volatilite) — aynı volatilitede balina farkı sürüyor mu?")
    for k, g in mm.groupby("vol", observed=True):
        c, ac = g[g.aw < 1], g[g.aw >= 1]
        print(f"  {str(k):12s} sakin n={len(c):3d} kayıp={c.kayip.mean() if len(c) else float('nan'):.1%}   aktif n={len(ac):3d} kayıp={ac.kayip.mean() if len(ac) else float('nan'):.1%}")

print("\nZaman bölünmesi (tutarlılık):")
h = len(m) // 2
for lab, g in (("1. yarı", m.iloc[:h]), ("2. yarı", m.iloc[h:])):
    c, ac = g[g.aw < 1], g[g.aw >= 1]
    print(f"  {lab}: sakin n={len(c):3d} kayıp={c.kayip.mean() if len(c) else float('nan'):.1%} | aktif n={len(ac):3d} kayıp={ac.kayip.mean() if len(ac) else float('nan'):.1%}")

print("\nKarşı-olgusal: aktif işlemler ATLANSAYDI")
for nm, g in (("hepsi", m), ("yalnız sakin", calm)):
    lo, hi = a.boot_ci(g.roi) if len(g) > 1 else (np.nan, np.nan)
    print(f"  {nm:14s} n={len(g):4d}  PnL={g.pnl.sum():+9.1f}  ort.ROI={g.roi.mean() if len(g) else float('nan'):+.3f} [{lo:+.2f},{hi:+.2f}]")
print("\nOkuma: sakin grubun kayıp oranı belirgin düşükse (p < 0.05) VE iki yarıda da aynı yöndeyse VE volatilite içinde de sürüyorsa hipotez güçlenir;")
print("yine de yeni veride sınanmadan 'istikrarlı kazanan' demek erkendir. Çok gruba bakıldığı için p-değerleri iyimsedir.")
