#!/usr/bin/env python3
"""Price to Beat (Chainlink pencere açılışı) kontrolü.

Sorular:
  1) Bot'un kullandığı Binance-bazlı uzaklık (z_binance) ile marketin gerçek referansı olan Chainlink-bazlı uzaklık (z_chainlink)
     sonucu tahmin etmede ne kadar farklı? (Brier ↔ düşük iyi)
  2) İki işaret ne sıklıkla ters yönü gösteriyor (referansa göre Up/Down kararı değişir)?
  3) Bizim yaklaşık Chainlink sonucumuz (windows.csv chainlink_winner) resmi sonuçla ne sıklıkla uyuşmuyor?

Kullanım: python3 tools/ptb_check.py /var/lib/polyedge/flow
"""
import os, sys, math
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import numpy as np
import pandas as pd
import analyze as a

d = sys.argv[1] if len(sys.argv) > 1 else "/var/lib/polyedge/flow"
t, f, w = a.load(d)
out, src = a.window_outcomes(f, w)
f = f.sort_values(["slug", "elapsed_s"])
Phi = np.vectorize(lambda z: 0.5 * (1 + math.erf(z / math.sqrt(2))))

# Chainlink açılış: windows.csv'deki değer, yoksa pencerenin ilk satırındaki Chainlink
clo = {}
if w is not None and "chainlink_open" in w:
    clo = dict(zip(w.slug, pd.to_numeric(w.chainlink_open, errors="coerce")))
first = f.groupby("slug").first()
for s in first.index:
    if pd.isna(clo.get(s, np.nan)) and first.loc[s, "elapsed_s"] <= 8:
        clo[s] = first.loc[s, "chainlink"]
f = f.merge(out, left_on="slug", right_index=True)
f["clo"] = f.slug.map(clo)
f = f.dropna(subset=["clo", "chainlink", "sigma", "rel_move_pct"])
print(f"{d}: {f.slug.nunique()} pencere (sonuç kaynağı: {src})\n")

rows = []
for lo, hi in [(20, 60), (60, 120), (120, 180), (180, 240), (240, 285)]:
    s = f[(f.elapsed_s >= lo) & (f.elapsed_s < hi)].sort_values("elapsed_s").groupby("slug").first()
    if len(s) < 20:
        continue
    sd = s.sigma * np.sqrt(np.maximum(300 - s.elapsed_s, 1))
    zb = (s.rel_move_pct / 100) / sd
    zc = np.log(s.chainlink / s.clo) / sd
    y = s.up.values
    br = lambda p: float(((np.clip(p, 1e-3, 1 - 1e-3) - y) ** 2).mean())
    rows.append((f"{lo}-{hi}s", len(s), round(br(Phi(zb)), 4), round(br(Phi(zc)), 4),
                 f"{(np.sign(zb) != np.sign(zc)).mean():.1%}", f"{np.abs(np.log(s.chainlink / s.clo) * 100 - s.rel_move_pct).median():.4f}"))
print("Brier: Binance-bazlı z ile Chainlink-bazlı z (price to beat referansı) — tek başına fiyat uzaklığı modeli")
print(pd.DataFrame(rows, columns=["geçen süre", "n", "brier_binance", "brier_chainlink", "ters_işaret", "medyan |fark| (% puan)"]).to_string(index=False))

if w is not None and {"chainlink_winner", "official_winner"} <= set(w.columns):
    x = w[w.official_winner.isin(["Up", "Down"]) & w.chainlink_winner.isin(["Up", "Down"])]
    bad = (x.official_winner != x.chainlink_winner)
    mv = pd.to_numeric(x.chainlink_move_bp, errors="coerce").abs()
    print(f"\nYaklaşık Chainlink sonucu ↔ resmi sonuç: {int(bad.sum())} / {len(x)} uyuşmuyor ({bad.mean():.1%})")
    if bad.any():
        print(f"  uyuşmayanlarda |Chainlink hareketi| medyanı: {mv[bad].median():.2f} bp (çok küçükse yakın kapanış/zamanlama farkı)")
else:
    print("\n(windows.csv'de chainlink_winner/official_winner yok; 3. kontrol atlandı)")
print("\nOkuma: brier_chainlink belirgin düşükse referansı Chainlink'e taşımak modeli iyileştirir; benzerse Binance proxy'si yeterlidir.")
