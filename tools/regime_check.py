#!/usr/bin/env python3
"""Rejim kontrolü: pencere sonuçlarında trend/momentum var mı, bot önceki pencerenin yönünü izlediğinde ne oluyor?

Kullanım:  python3 tools/regime_check.py /var/lib/polyedge/flow
Bu bir HİPOTEZ ÜRETME aracıdır. Bulduğun şeyi aynı veride "kanıt" sayma; yalnızca yeni veride sına.
"""
import os, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import numpy as np
import pandas as pd
import analyze as a

d = sys.argv[1] if len(sys.argv) > 1 else "/var/lib/polyedge/flow"
t, f, w = a.load(d)
out, src = a.window_outcomes(f, w)
t, ncorr = a.correct_trades(t, w)
out["start"] = [int(s.split("-")[-1]) for s in out.index]
out = out.sort_values("start")
print(f"=== {os.path.basename(os.path.normpath(d))}: {len(out)} pencere (kaynak: {src}) ===")
print(f"Genel Up payı: {out.up.mean():.3f}")

# ---- ardışık pencereler: önceki yön sonrakini öngörüyor mu?
o = out.reset_index()
consec = o.start.diff() == 300
prev = o.up.shift(1)
pair = o[consec].copy(); pair["prev"] = prev[consec]
n1, n0 = (pair.prev == 1).sum(), (pair.prev == 0).sum()
p1 = pair[pair.prev == 1].up.mean(); p0 = pair[pair.prev == 0].up.mean()
se = np.sqrt(p1 * (1 - p1) / max(n1, 1) + p0 * (1 - p0) / max(n0, 1))
z = (p1 - p0) / se if se > 0 else float("nan")
print(f"\nArdışık pencere çifti: {len(pair)}")
print(f"  P(Up | önceki Up)   = {p1:.3f} (n={n1})")
print(f"  P(Up | önceki Down) = {p0:.3f} (n={n0})")
print(f"  fark = {p1 - p0:+.3f}   z = {z:+.2f}   (|z|>2 ≈ anlamlı; + = momentum, − = geri dönüş)")

# ---- 3 saatlik bloklarda Up payı (rejim kayması var mı)
o["time"] = pd.to_datetime(o.start, unit="s", utc=True)
o["blok"] = o.time.dt.floor("3h")
b = o.groupby("blok").agg(pencere=("up", "size"), up_payi=("up", "mean")).round(3)
print("\n3 saatlik bloklarda Up payı (UTC):")
print(b.to_string())

# ---- bot önceki pencerenin yönünü izlediğinde / tersine gittiğinde
prev_map = dict(zip(o.slug, prev)) if "slug" in o else {}
t["prev_up"] = t.slug.map(prev_map)
tt = t.dropna(subset=["prev_up"]).copy()
tt["izler"] = (tt.side == "Up") == (tt.prev_up == 1)
print(f"\nBot işlemleri önceki pencerenin yönüne göre (n={len(tt)}):")
for flag, g in tt.groupby("izler"):
    se = np.sqrt(0.25 / len(g))
    print(f"  {'önceki yönü İZLEYEN ' if flag else 'önceki yöne TERS     '}: n={len(g):3d} kazanma={g.won.mean():.3f} ort.fiyat={g.avg_price.mean():.3f} fark={g.won.mean() - g.avg_price.mean():+.3f} (±{se:.3f}) PnL={g.pnl.sum():+8.1f}")
