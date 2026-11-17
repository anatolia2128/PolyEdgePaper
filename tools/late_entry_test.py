#!/usr/bin/env python3
"""Geç giriş (180-270 sn) hipotezi: veriyi zamana göre ikiye bölüp her yarıda tarafsız backtest çalıştırır.

Kullanım:
  python3 tools/late_entry_test.py                      # /var/lib/polyedge/flow ve noflow
  python3 tools/late_entry_test.py DIR1 [DIR2 ...]      # kendi klasörlerin
Bir kez çalıştır; sonuca bakıp parametre ayarlama (yoksa test bozulur).
"""
import os, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import analyze as a

LO, HI = 180, 270
dirs = sys.argv[1:] or ["/var/lib/polyedge/flow", "/var/lib/polyedge/noflow"]

for d in dirs:
    name = os.path.basename(os.path.normpath(d))
    t, f, w = a.load(d)
    out, src = a.window_outcomes(f, w)
    slugs = sorted(out.index, key=lambda s: int(s.split("-")[-1]))
    half = len(slugs) // 2
    for label, part in (("1. yarı", slugs[:half]), ("2. yarı", slugs[half:])):
        print(f"--- {name} {label} ({len(part)} pencere), giriş {LO}-{HI} sn")
        a.unbiased_backtest(f[f.slug.isin(part)], out.loc[part], thresholds=(0.04, 0.06, 0.08), lo=LO, hi=HI)
