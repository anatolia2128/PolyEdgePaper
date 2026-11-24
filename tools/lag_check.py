#!/usr/bin/env python3
"""Zaman kayması kontrolü: resmi sonuç, kayıtlı Chainlink akışının X sn kaydırılmış hâline daha mı iyi uyuyor?

Her pencere için tahmin = Chainlink(bitiş + δ_kapanış) >= Chainlink(başlangıç + δ_açılış); resmi sonuçla uyum oranı hesaplanır.
δ negatif = akışta daha ESKİ bir an, pozitif = daha YENİ bir an. Uyum belirli bir kaymada belirgin yükseliyorsa, bot'un kullandığı akış
(RTDS crypto_prices_chainlink) ile marketin çözümde kullandığı akış arasında o kadar zaman farkı var demektir.

Kullanım: python3 tools/lag_check.py /var/lib/polyedge/flow
Sınır: features.csv Chainlink'i 5 sn aralıkla kaydeder; kaymalar 5 sn'nin katlarıyla taranır.
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

f = f.dropna(subset=["chainlink"]).sort_values("time")
tt = f.time.astype("int64").to_numpy() / 1e9
cc = f.chainlink.to_numpy(float)


def price_at(x, tol=6.0):
    i = np.searchsorted(tt, x, side="right") - 1
    return cc[i] if i >= 0 and x - tt[i] <= tol else np.nan


wins = []
for slug, o in off.items():
    try:
        s = int(slug.split("-")[-1])
    except ValueError:
        continue
    wins.append((s, s + 300, o))
SH = list(range(-20, 25, 5))
print(f"{d}: resmi sonuçlu pencere = {len(wins)}\n")
G = pd.DataFrame(index=[f"açılış {x:+d}s" for x in SH], columns=[f"kapanış {x:+d}s" for x in SH], dtype=float)
for dop in SH:
    for dcl in SH:
        ok = n = 0
        for s, e, o in wins:
            po, pc = price_at(s + dop), price_at(e + dcl)
            if np.isnan(po) or np.isnan(pc):
                continue
            n += 1
            ok += (("Up" if pc >= po else "Down") == o)
        G.loc[f"açılış {dop:+d}s", f"kapanış {dcl:+d}s"] = ok / n if n else np.nan
print("Resmi sonuçla uyum oranı (%):")
print((G * 100).round(1).to_string())
base = G.loc["açılış +0s", "kapanış +0s"]
mx = G.stack().idxmax()
print(f"\nKaymasız (0,0): {base:.1%}   En yüksek: {mx[0]} / {mx[1]} → {G.stack().max():.1%}")
print("Yorum: en yüksek uyum ~%95+ ve (0,0)'dan belirgin yüksekse çözüm akışı bizimkinden o kadar kaymıştır; hiçbir hücre yükselmiyorsa fark başka bir akıştan/kuraldan gelir.")
