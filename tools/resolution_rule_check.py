#!/usr/bin/env python3
"""Çözüm kuralı kontrolü: Polymarket 5 dk marketleri gerçekte hangi kurala göre çözülüyor?

Bot, "kapanış Chainlink >= açılış Chainlink" (iki uç nokta) varsayıyor ve resmi sonuçla %~11 uyuşmuyor.
Bu script, kayıtlı Chainlink örnekleriyle (features.csv, 5 sn aralıklı) farklı açılış/kapanış tanımlarını resmi sonuçla karşılaştırır
(örn. kapanış = son 60 sn ortalaması / TWAP). En yüksek uyum, gerçek kurala en yakın tanımdır.

Kullanım: python3 tools/resolution_rule_check.py /var/lib/polyedge/flow
"""
import os, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import numpy as np
import pandas as pd
import analyze as a

d = sys.argv[1] if len(sys.argv) > 1 else "/var/lib/polyedge/flow"
t, f, w = a.load(d)
if w is None or "official_winner" not in w:
    sys.exit("windows.csv içinde official_winner yok.")
off = {s: o for s, o in zip(w.slug, w.official_winner) if o in ("Up", "Down")}
f = f[f.slug.isin(off)].dropna(subset=["chainlink"]).sort_values(["slug", "elapsed_s"])

OPEN = {"ilk satır (≈0-8 sn)": lambda g: g[g.elapsed_s <= 8].chainlink.iloc[0] if (g.elapsed_s <= 8).any() else np.nan,
        "ilk 15 sn ort.": lambda g: g[g.elapsed_s <= 15].chainlink.mean(),
        "ilk 30 sn ort.": lambda g: g[g.elapsed_s <= 30].chainlink.mean(),
        "ilk 60 sn ort.": lambda g: g[g.elapsed_s <= 60].chainlink.mean()}
CLOSE = {"son satır (≈295+ sn)": lambda g: g[g.elapsed_s >= 290].chainlink.iloc[-1] if (g.elapsed_s >= 290).any() else np.nan,
         "son 15 sn ort.": lambda g: g[g.elapsed_s >= 285].chainlink.mean(),
         "son 30 sn ort. (TWAP30)": lambda g: g[g.elapsed_s >= 270].chainlink.mean(),
         "son 60 sn ort. (TWAP60)": lambda g: g[g.elapsed_s >= 240].chainlink.mean()}

rows = []
for slug, g in f.groupby("slug"):
    if g.elapsed_s.max() < 290 or g.elapsed_s.min() > 8:
        continue
    rec = {"slug": slug, "off": off[slug]}
    for on, of in OPEN.items(): rec["o|" + on] = of(g)
    for cn, cf in CLOSE.items(): rec["c|" + cn] = cf(g)
    rows.append(rec)
R = pd.DataFrame(rows)
print(f"{d}: resmi sonuçlu ve tam kayıtlı pencere = {len(R)}\n")
res = {}
for on in OPEN:
    res[on] = {}
    for cn in CLOSE:
        pred = np.where(R["c|" + cn] >= R["o|" + on], "Up", "Down")
        ok = ~(R["c|" + cn].isna() | R["o|" + on].isna())
        res[on][cn] = float((pred[ok] == R.off[ok]).mean())
M = pd.DataFrame(res).T
print("Resmi sonuçla UYUM oranı (satır = açılış tanımı, sütun = kapanış tanımı):")
print((M * 100).round(1).astype(str).add("%").to_string())
best = M.stack().idxmax()
print(f"\nEn yüksek uyum: açılış='{best[0]}', kapanış='{best[1]}' → {M.stack().max():.1%}")
print("Not: 5 sn'lik örnekleme uyumu aşağı çeker; yine de tanımlar arası fark kuralı gösterir. TWAP60 belirgin üstteyse çözüm bir ortalamaya dayanıyor olabilir.")
