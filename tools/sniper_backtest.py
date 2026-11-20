#!/usr/bin/env python3
"""Sniper backtest (kayıtlı defterlerle, resmi pencere sonuçlarıyla).

Kural: pencerede belirtilen saniyeden sonra, bir tarafın best ask'ı 0.95-0.99 ve AYNI tarafın bid'i >= 0.90 olan İLK satırda
o tarafı ask'tan al, çözüme kadar tut. Fee dahil. İsteğe bağlı ek slippage (sent) ile duyarlılık.

Kullanım:  python3 tools/sniper_backtest.py /var/lib/polyedge/flow
ÖNEMLİ: Birincil (önceden belirlenmiş) hipotez = giriş >= 240 sn. Diğer başlangıç saniyeleri keşif amaçlıdır; en iyisini seçme.
Sınırlar: defter 5 sn'de bir örneklenir; gerçek dolum karar anından 0.4-1.5 sn sonradır (ask kaybolabilir/oynayabilir).
"""
import os, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import numpy as np
import pandas as pd
import analyze as a

d = sys.argv[1] if len(sys.argv) > 1 else "/var/lib/polyedge/flow"
t, f, w = a.load(d)
out, src = a.window_outcomes(f, w)
f = f.merge(out, left_on="slug", right_index=True).sort_values(["slug", "elapsed_s"])
RATE = 0.072
fee = lambda p: p * RATE * p * (1 - p)
print(f"{d}: {len(out)} pencere (sonuç kaynağı: {src})\n")


def run(lo, hi=296, slip=0.0, trig=0.95, cap=0.99, minbid=0.90):
    res = []
    for slug, g in f.groupby("slug"):
        for r in g[(g.elapsed_s >= lo) & (g.elapsed_s <= hi)].itertuples():
            c = [(s, x, b) for s, x, b in (("Up", r.up_ask, r.up_bid), ("Down", r.down_ask, r.down_bid))
                 if pd.notna(x) and pd.notna(b) and trig <= x <= cap and b >= minbid]
            if c:
                s, x, b = max(c, key=lambda z: z[1])
                px = min(x + slip, 0.999)
                win = (s == "Up") == (r.up == 1)
                res.append((px, win, ((1.0 if win else 0.0) - px - fee(px)) / (px + fee(px)), r.elapsed_s))
                break
    return np.array(res, float) if res else np.empty((0, 4))


def line(lo, slip):
    x = run(lo, slip=slip)
    if len(x) == 0:
        return f"  giriş >= {lo:3d}s, ek slippage {slip * 100:.0f}¢: işlem yok"
    n = len(x); wr = x[:, 1].mean(); px = x[:, 0].mean(); roi = x[:, 2]
    lo_, hi_ = a.boot_ci(roi)
    se = np.sqrt(wr * (1 - wr) / n)
    return (f"  giriş >= {lo:3d}s, ek slippage {slip * 100:.0f}¢: n={n:3d} kazanma={wr:.3f} ort.fiyat={px:.3f} fark={wr - px:+.3f} (±{se:.3f}) "
            f"kayıp={int((1 - x[:, 1]).sum()):3d} ort.ROI={roi.mean():+.4f} 90%GA[{lo_:+.3f},{hi_:+.3f}]")


print("BİRİNCİL hipotez (önceden belirlenmiş): giriş >= 240 sn")
for slip in (0.0, 0.01):
    print(line(240, slip))
print("\nKeşif amaçlı (en iyisini seçme):")
for lo in (150, 180, 210, 270):
    for slip in (0.0, 0.01):
        print(line(lo, slip))

# günlük tutarlılık (birincil)
x = run(240)
if len(x):
    sl = [s for s, g in f.groupby("slug")]
    print("\nNot: ROI'nin güven aralığı sıfırı kapsıyorsa ya da negatifse bu kural net kârlı değildir.")
    print("Bir kayıp, kazanç başına ~0.05 USD'ye karşı ~0.95 USD siler; başa baş için kazanma oranı > ort.fiyat + fee olmalıdır.")
