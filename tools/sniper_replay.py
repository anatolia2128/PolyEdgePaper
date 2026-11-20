#!/usr/bin/env python3
"""Sniper replay/backtest — botun kaydettiği books.csv (ilk 5 seviye, 1-2 Hz) + windows.csv (resmi sonuç) üzerinden,
bot kodundan BAĞIMSIZ olarak dört kuralı yan yana (aynı pencereler, aynı dolum modeli) çalıştırır:

  A  tetik                 : bir tarafın ask'ı [tetik, tavan] ve aynı tarafın bid'i >= minbid
  B  tetik + doğrulama     : A + Binance aynı yönde VE Chainlink z >= minz
  C  tetik + doğr. + EV    : B + beklenen net getiri >= minev
  D  tetik + EV            : A + EV (doğrulamasız)

Dolum modeli: karar anından `latency` sn sonraki ilk defter satırı; tetik şartı o satırda yeniden doğrulanır; pay adedi,
ask tarafını limit fiyata kadar yürüyerek bütçeyi (fee dahil) aşmayacak şekilde belirlenir; kayıtlı yalnızca ilk 5 seviye görünür.

Kullanım:
  1) Botu Bot__RecordBooks=true ile çalıştır (sniperv2 örneği bunu zaten yapar) → <klasör>/books.csv
  2) python3 tools/sniper_replay.py /var/lib/polyedge/sniperv2
Simülasyon testi için:  --window 60 --lo 15 --hi 56

UYARI: Parametreleri sonuçlara bakarak ayarlama; öntanımlılar önceden belirlenmiştir. Birincil sonuç: B/C'nin A'ya göre farkı.
"""
import argparse, math, os, sys
import numpy as np
import pandas as pd

ap = argparse.ArgumentParser()
ap.add_argument("dir")
ap.add_argument("--window", type=float, default=300)
ap.add_argument("--lo", type=float, default=150)
ap.add_argument("--hi", type=float, default=296)
ap.add_argument("--trigger", type=float, default=0.95)
ap.add_argument("--maxprice", type=float, default=0.99)
ap.add_argument("--minbid", type=float, default=0.90)
ap.add_argument("--band", type=float, default=0.03)
ap.add_argument("--latency", type=float, default=0.4)
ap.add_argument("--maxslip", type=float, default=0.03)
ap.add_argument("--stake", type=float, default=50.0)
ap.add_argument("--minshares", type=float, default=5.0)
ap.add_argument("--minz", type=float, default=2.0)
ap.add_argument("--minev", type=float, default=0.005)
ap.add_argument("--tail", type=float, default=0.005)
ap.add_argument("--slipbuf", type=float, default=0.003)
ap.add_argument("--rate", type=float, default=0.072)
ap.add_argument("--attempts", type=int, default=3)
ap.add_argument("--sigmafloor", type=float, default=1e-5)
A = ap.parse_args()

bpath = os.path.join(A.dir, "books.csv")
if not os.path.exists(bpath):
    sys.exit(f"{bpath} yok. Önce botu Bot__RecordBooks=true ile çalıştır (sniperv2 örneği bunu yapar).")

Phi = lambda z: 0.5 * (1 + math.erf(z / math.sqrt(2)))
fee_ps = lambda p: p * A.rate * p * (1 - p)


def levels(s, desc):
    if not isinstance(s, str) or not s:
        return []
    out = []
    for x in s.split("|"):
        p, q = x.split(":")
        out.append((float(p), float(q)))
    return sorted(out, reverse=desc)


b = pd.read_csv(bpath)
w = pd.read_csv(os.path.join(A.dir, "windows.csv"), on_bad_lines="skip")
win = w["official_winner"].where(w["official_winner"].isin(["Up", "Down"]), w["chainlink_winner"])
outcome = dict(zip(w.slug, win))
b = b[b.slug.isin([s for s, o in outcome.items() if o in ("Up", "Down")])]
print(f"{A.dir}: books.csv satır={len(b)}, sonuçlu pencere={b.slug.nunique()}\n")


def parse_rows(g):
    rows = []
    for r in g.itertuples():
        rows.append(dict(
            t=float(r.elapsed_s),
            up_b=levels(r.up_bids, True), up_a=levels(r.up_asks, False),
            dn_b=levels(r.down_bids, True), dn_a=levels(r.down_asks, False),
            bin=r.binance, cl=r.chainlink, bino=r.binance_open, clo=r.chainlink_open, sig=r.sigma))
    return rows


def trigger(r):
    best = None
    for side, bids, asks in (("Up", r["up_b"], r["up_a"]), ("Down", r["dn_b"], r["dn_a"])):
        if not bids or not asks:
            continue
        a, bd = asks[0][0], bids[0][0]
        if A.trigger <= a <= A.maxprice and bd >= A.minbid and (best is None or a > best[1]):
            best = (side, a)
    return best


def zscores(r, side):
    vals = (r["bin"], r["cl"], r["bino"], r["clo"], r["sig"])
    if any(pd.isna(v) for v in vals):
        return None
    left = max(A.window - r["t"], 1.0)
    sd = max(float(r["sig"]), A.sigmafloor) * math.sqrt(left)
    sgn = 1.0 if side == "Up" else -1.0
    return sgn * math.log(r["cl"] / r["clo"]) / sd, sgn * math.log(r["bin"] / r["bino"]) / sd


def passes(r, side, ask, need_confirm, need_ev):
    if not (need_confirm or need_ev):
        return True
    z = zscores(r, side)
    if z is None:
        return False
    zcl, zbin = z
    if need_confirm and (zbin <= 0 or zcl < A.minz):
        return False
    if need_ev:
        pw = min(Phi(zcl), 1 - A.tail)
        cost = ask + fee_ps(ask) + A.slipbuf
        if (pw - cost) / cost < A.minev:
            return False
    return True


def fill(asks, limit, budget):
    shares = cost = fee = 0.0
    rem = budget
    for p, q in asks:
        if p > limit:
            break
        per = p + fee_ps(p)
        take = min(q, rem / per)
        if take <= 0:
            break
        shares += take; cost += take * p; fee += take * fee_ps(p); rem -= take * per
    sh = math.floor(shares * 100) / 100
    if shares > 0 and sh < shares:
        k = sh / shares; cost *= k; fee *= k
    return sh, cost, fee


STRATS = {"A tetik": (False, False), "B tetik+doğrulama": (True, False), "C tetik+doğr.+EV": (True, True), "D tetik+EV": (False, True)}
res = {k: [] for k in STRATS}
calib = []
misses = {k: 0 for k in STRATS}
nwin = 0

for slug, g in b.sort_values(["slug", "elapsed_s"]).groupby("slug"):
    rows = parse_rows(g)
    nwin += 1
    truth = outcome[slug]
    first_trig_logged = False
    for name, (nc, ne) in STRATS.items():
        attempts = 0
        for i, r in enumerate(rows):
            if attempts >= A.attempts:
                break
            if not (A.lo <= r["t"] <= A.hi):
                continue
            tg = trigger(r)
            if tg is None:
                continue
            side, ask = tg
            if name == "A tetik" and not first_trig_logged:
                first_trig_logged = True
                z = zscores(r, side)
                if z is not None:
                    calib.append((Phi(z[0]), (side == truth), z[0]))
            if not passes(r, side, ask, nc, ne):
                continue
            attempts += 1
            j = next((k for k in range(i + 1, len(rows)) if rows[k]["t"] >= r["t"] + A.latency), None)
            if j is None or rows[j]["t"] >= A.window:
                misses[name] += 1; continue
            f = rows[j]
            bids, asks = (f["up_b"], f["up_a"]) if side == "Up" else (f["dn_b"], f["dn_a"])
            if not bids or not asks or bids[0][0] < A.minbid or asks[0][0] > A.maxprice or asks[0][0] < A.trigger - A.band:
                misses[name] += 1; continue
            sh, cost, fee = fill(asks, min(A.maxprice, ask + A.maxslip), A.stake)
            if sh < A.minshares:
                misses[name] += 1; continue
            won = side == truth
            pnl = (sh if won else 0.0) - cost - fee
            res[name].append(dict(slug=slug, t=r["t"], side=side, px=cost / sh, won=won, pnl=pnl, roi=pnl / (cost + fee), fee=fee, ask=ask))
            break


def boot(v, n=3000):
    v = np.asarray(v, float)
    if len(v) < 2:
        return (float("nan"), float("nan"))
    rng = np.random.default_rng(0)
    m = [rng.choice(v, len(v)).mean() for _ in range(n)]
    return np.percentile(m, 5), np.percentile(m, 95)


print(f"Pencere (sonuçlu, defter kaydı olan): {nwin}   giriş penceresi: {A.lo:.0f}-{A.hi:.0f} sn   gecikme: {A.latency}s   bütçe: {A.stake:.0f} USD\n")
print(f"{'strateji':22s} {'n':>4s} {'kaçan':>5s} {'kazanma':>8s} {'ort.fiyat':>9s} {'başabaş':>8s} {'kayıp':>5s} {'ROI':>8s} {'90%GA':>17s} {'net PnL':>9s}")
for name, v in res.items():
    if not v:
        print(f"{name:22s} {0:4d} {misses[name]:5d}  işlem yok"); continue
    d = pd.DataFrame(v)
    be = (d.px + d.px.apply(fee_ps)).mean()
    lo_, hi_ = boot(d.roi)
    print(f"{name:22s} {len(d):4d} {misses[name]:5d} {d.won.mean():8.3f} {d.px.mean():9.3f} {be:8.3f} {int((~d.won).sum()):5d} {d.roi.mean():+8.3f} [{lo_:+.3f},{hi_:+.3f}] {d.pnl.sum():+9.1f}")

edges = [0.0, 0.5, 0.6, 0.8, 1.01]
print("\nGiriş zamanına göre (pencerenin oranı):")
for name, v in res.items():
    if not v:
        continue
    d = pd.DataFrame(v); d["f"] = pd.cut(d.t / A.window, edges)
    g = d.groupby("f", observed=True).agg(n=("won", "size"), kazanma=("won", "mean"), fiyat=("px", "mean"), pnl=("pnl", "sum")).round(3)
    print(f"  {name}:\n" + "\n".join("    " + l for l in g.to_string().split("\n")))

if calib:
    c = pd.DataFrame(calib, columns=["p", "won", "z"])
    c["bin"] = pd.cut(c.p, [0, 0.9, 0.95, 0.98, 0.99, 1.0001])
    g = c.groupby("bin", observed=True).agg(n=("won", "size"), gerçekleşen=("won", "mean"), tahmin=("p", "mean")).round(3)
    print("\nKalibrasyon: ilk tetik anında Φ(z_chainlink) tahmini ↔ gerçekleşen kazanma (n küçükse güvenme):")
    print(g.to_string())
print("\nYorum: B/C, A'dan belirgin biçimde daha iyi VE güven aralığı alt sınırı > 0 değilse doğrulamanın değeri kanıtlanmamıştır.")
