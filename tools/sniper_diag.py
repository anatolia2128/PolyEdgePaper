#!/usr/bin/env python3
"""Sniper teşhisi: pencerenin hangi saniyelerinde 95-99 sent aralığında ALINABİLİR fiyat var?

Kullanım:  python3 tools/sniper_diag.py /var/lib/polyedge/sniper   (herhangi bir örneğin features.csv'si yeterli)
Sonuçlara (kazanç/kayıp) bakmaz; yalnızca defterin doluluğunu ve tetik şartının ne zaman sağlandığını ölçer.
"""
import os, sys
import numpy as np
import pandas as pd

d = sys.argv[1] if len(sys.argv) > 1 else "/var/lib/polyedge/sniper"
f = pd.read_csv(os.path.join(d, "features.csv"))
W = f.slug.nunique()
cols = ["up_bid", "up_ask", "down_bid", "down_ask"]
print(f"{d}: {W} pencere, {len(f)} satır\n")

print("1) Defterde boş taraf oranı (zamana göre):")
for lo, hi in [(0, 60), (60, 180), (180, 240), (240, 270), (270, 290), (290, 301)]:
    s = f[(f.elapsed_s >= lo) & (f.elapsed_s < hi)]
    if len(s):
        print(f"   {lo:3d}-{hi:3d}s: satır={len(s):5d}  up_ask boş={s.up_ask.isna().mean():4.0%}  down_ask boş={s.down_ask.isna().mean():4.0%}  en az bir taraf boş={s[cols].isna().any(axis=1).mean():4.0%}")

rows = []
for r in f.itertuples():
    for side, ask, bid in (("Up", r.up_ask, r.up_bid), ("Down", r.down_ask, r.down_bid)):
        if pd.notna(ask) and pd.notna(bid) and 0.95 <= ask <= 0.99 and bid >= 0.90:
            rows.append((r.slug, r.elapsed_s))
a = pd.DataFrame(rows, columns=["slug", "sn"])
print(f"\n2) Sniper şartı (ask 0.95-0.99 ve aynı tarafın bid ≥ 0.90) en az bir kez sağlanan pencere: {a.slug.nunique()} / {W} ({a.slug.nunique() / max(W, 1):.0%})")
if len(a):
    print("   ilk görüldüğü saniye (medyan):", int(a.groupby('slug').sn.min().median()), " son görüldüğü saniye (medyan):", int(a.groupby('slug').sn.max().median()))
    print("   Aralığa göre şartın sağlandığı pencere oranı:")
    for lo, hi in [(120, 150), (150, 200), (200, 240), (240, 270), (270, 290), (290, 301)]:
        m = a[(a.sn >= lo) & (a.sn < hi)].slug.nunique()
        print(f"     {lo:3d}-{hi:3d}s: {m:4d} pencere ({m / max(W, 1):.0%})")

late = f[f.elapsed_s >= 290]
fav = [pd.notna(ask) for r in late.itertuples() for ask, bid in ((r.up_ask, r.up_bid), (r.down_ask, r.down_bid)) if pd.notna(bid) and bid >= 0.90]
if fav:
    print(f"\n3) Son 10 sn'de bid ≥ 0.90 olan (favori) tarafın ask'ı dolu mu? {np.mean(fav):.1%} (n={len(fav)})")
    print("   Düşükse: favori tarafta satıcı kalmıyor, 'son 10 sn' kuralı dolum üretemez.")
