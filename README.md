# PolyEdge Paper

Binance Futures'taki balina / akış hareketlerini izleyip Polymarket **5 dakikalık BTC Up/Down** marketlerinde
**paper (sanal) pozisyon** alan bot. **Gerçek emir göndermez, cüzdan/API anahtarı istemez.**

## Visual Studio'da çalıştırma
1. Visual Studio 2022 (17.8+) ve .NET 8 SDK gerekir.
2. `PolyEdgePaper.sln` dosyasını aç. Başlangıç projesi `PolyEdge.App`.
3. Çalıştırma profilini seç:
   - **PolyEdge.App (Simulated offline)** – sahte veriyle altyapıyı dener, internet/engel gerektirmez.
   - **PolyEdge.App (Live paper)** – gerçek Binance + Polymarket verisi, sanal işlem.
4. F5. Panel: http://localhost:5080  (log'lar konsol penceresinde).

NuGet paketi yoktur; ek kurulum gerekmez.

## Türk Telekom engeli (polymarket.com)
Engel senin internet hattında. Bot yerelde çalışırken veri çekmek için erişimin açılması gerekir.
Sırasıyla dene:
1. **DNS-over-HTTPS (genelde yeter):** Windows 11 → Ayarlar → Ağ ve internet → (Wi-Fi/Ethernet) → Donanım özellikleri →
   DNS sunucu ataması → Düzenle → Elle → IPv4 aç → Tercih edilen `1.1.1.1`, alternatif `1.0.0.1` →
   "DNS over HTTPS": **Açık (otomatik şablon)** → Kaydet. Sonra `ipconfig /flushdns`.
2. **VPN:** DNS yetmezse güvenilir, ücretli bir VPN'i tüm sistem için aç (Türkiye dışı sunucu).
   Ücretsiz/bilinmeyen VPN kullanma.
3. **Yalnızca botu yönlendirmek:** `appsettings.json` → `Feeds:ProxyUrl` (örn. `http://127.0.0.1:8080` veya `socks5://127.0.0.1:1080`).
   Hem HTTP hem WebSocket bu proxy'den geçer.

Test: tarayıcıda `https://gamma-api.polymarket.com/events?slug=btc-updown-5m-<ts>` açılıyor mu? Bot logunda
"Gamma isteği başarısız" görmüyorsan erişim tamamdır.

> Paper modda emir gönderilmediği için Polymarket'in bölge kısıtlaması işlem açısından devreye girmez; sadece okuma yapılır.
> Gerçek emire geçilirse emri gönderen IP'nin bulunduğu ülke Polymarket'in izinli listesinde olmalıdır
> (sunucu bölgesini buna göre seç; engeli konum gizleyerek aşmaya çalışma).

## Nasıl çalışır
1. **Pencere:** Her 5 dk'da `btc-updown-5m-<unix>` marketi bulunur (Gamma). Pencere açılış fiyatı Binance'ten kaydedilir.
2. **Sinyal (Binance):** tek işlemde ≥ `WhaleNotionalUsd` olan "balina" işlemleri (30 sn net), CVD (15 sn), defter dengesizliği (ilk 10 seviye),
   likidasyonlar → ağırlıklı **akış skoru** (-1..+1).
3. **Model:** `P(Up) = Φ( ln(S/Açılış)/(σ√τ) + FlowWeight·skor·√(τ/T) )`. σ, Binance 1 sn getirilerinden üstel ağırlıkla hesaplanır.
   Final olasılık = `ModelWeight·model + (1-ModelWeight)·piyasa`.
4. **Edge:** `P(kazanma) − (ask + taker fee/pay)`. `MinEdge` altında işlem yok.
5. **Boyut:** kesirli Kelly (`KellyFraction`) + üst sınırlar.
6. **Dolum (paper):** `LatencyMs` kadar bekler, **o andaki gerçek order book'u** (CLOB `/book`) ask tarafından yürür → gerçek slippage.
   Limit fiyat = karar ask + `MaxSlippage`.
7. **Maliyet:** Polymarket taker fee formülü `pay·p·FeeRate·(p(1-p))^FeeExponent` (kripto için varsayılan 0.072 / 1, tepe ≈ %1.8).
   **Gas:** CLOB emirleri off-chain eşleşir, işlem başına gas normalde 0'dır; `GasUsdPerTrade` ve `RedeemGasUsd` ile stres testi yapabilirsin.
8. **Sonuç:** Pencere bitince Polymarket'in resmi çözümü (Gamma `closed` + `outcomePrices`) beklenir; 3 dk gecikirse Chainlink açılış/kapanış
   (marketin çözüm kaynağı) ile yaklaşık sonuç. Kazanan pay = 1 USD, kaybeden = 0. Bakiye, PnL, ROI güncellenir.

## Çıktılar (`src/PolyEdge.App/data/`)
- `state.json` – bakiye + tüm işlemler (yeniden başlatınca devam eder; sıfırlamak için `Bot:ResetState=true`).
- `trades.csv` – kapanan işlemler (fiyat, slippage, fee, PnL, ROI, olasılıklar).
- `features.csv` – her 5 sn'de sinyal/olasılık/fiyat kaydı → modeli **kalibre etmek ve backtest** için.
- `windows.csv` – işlem açılsın açılmasın **her pencere** için Chainlink açılış/kapanış ve Polymarket'in **resmi sonucu** (`official_winner`, `resolution_source`).

## Dürüst notlar
- Simülasyon modundaki kâr/zarar hiçbir edge'i kanıtlamaz; sadece altyapıyı doğrular.
- Canlı veri akışları (Binance/RTDS/Gamma/CLOB) kodu belgelenmiş formatlara göre yazıldı; ilk çalıştırmada loglardan
  "bağlandı / abone olundu" satırlarını ve panelin üstündeki yeşil noktaları kontrol et.
- Binance Futures URL'leri `Feeds` altında ayarlanabilir. Bağlanmazsa her ikisini de `wss://fstream.binance.com/stream` yap.
- Fee oranını `https://clob.polymarket.com/fee-rate?token_id=...` ile doğrula; Polymarket kategori oranlarını değiştirebiliyor.
- Paper sonuçlar iyimser kalır: kuyruk önceliği, diğer botların tepkisi ve daha yüksek gecikme modellenmez. Önce `features.csv` ile
  birkaç yüz işlem biriktir, Brier skoru ve ROI'yi gör; sonra eşikleri (MinEdge, FlowWeight, WhaleNotionalUsd) ayarla.
- Sunucuya taşırken: `data/` klasörünü kalıcı diske bağla, ayarları ortam değişkeniyle ver (`Strategy__MinEdge=0.05`), paneli herkese açma
  (kimlik doğrulaması yok).


## Ubuntu sunucuya kurulum (AWS EC2)
Kodda gizli bilgi yok (API anahtarı kullanılmaz); özel GitHub deposu kullanabilirsin.

1. Projeyi GitHub'a it (`bin/`, `obj/`, `data/` `.gitignore`'da).
2. Sunucuda (EC2 Instance Connect terminali), özel depo için deploy key:
   ```bash
   ssh-keygen -t ed25519 -N "" -f ~/.ssh/id_ed25519 && cat ~/.ssh/id_ed25519.pub
   ```
   Çıkan satırı GitHub → depo → Settings → Deploy keys → Add (yazma izni VERME).
3. ```bash
   git clone git@github.com:KULLANICI/DEPO.git ~/polyedge-src
   bash ~/polyedge-src/deploy/install.sh
   ```
   Varsayılan olarak iki örnek kurar: `flow` (FlowWeight 0.5, port 5080) ve `noflow` (FlowWeight 0, port 5081). Aynı pencerelerde yan yana çalışırlar,
   böylece balina/akış sinyalinin katkısı karşılaştırılabilir. Tek örnek için: `bash deploy/install.sh flow`.
4. Güncelleme: kodu GitHub'a it, sunucuda `cd ~/polyedge-src && bash deploy/install.sh` (veri klasörlerine dokunmaz).
5. Ayar değiştirme: `/etc/polyedge/<ornek>.env` dosyasına `Strategy__MinEdge=0.08` gibi satırlar ekle, `sudo systemctl restart polyedge@<ornek>`.
6. Panel: `ssh -L 5080:localhost:5080 -L 5081:localhost:5081 -i anahtar.pem ubuntu@SUNUCU-IP` → http://localhost:5080 ve :5081.
7. Veriyi çekme: `scp -r -i anahtar.pem ubuntu@SUNUCU-IP:/var/lib/polyedge ./sunucu-data`

## Analiz
```
pip install pandas numpy
python tools/analyze.py sunucu-data/flow sunucu-data/noflow
```
Brier (bot vs piyasa), sinyal katkısı, tarafsız backtest ve iki örneğin pencere bazlı PnL farkını (güven aralığıyla) yazar.


## Sniper modu (üçüncü örnek)
`Strategy:Mode = Sniper` olan örnek olasılık modeli kullanmaz: bir tarafın **best ask'ı 0.95'e ulaşınca** (ve aynı tarafın bid'i ≥ 0.90 ise) o tarafı alır ve **çözüme kadar tutar**.
Fee, gecikme (`LatencyMs`) ve gerçek order book slippage'ı normal şekilde uygulanır. Varsayılan giriş penceresi **150-296. saniye**; pencere başına en fazla bir işlem, sabit stake (`SniperStakeUsd`). Emir pencere bitişinden sonra ulaşacaksa iptal edilir.
**Neden son 10 sn değil:** kayıtlı defterlerde son ~10 saniyede kazanma ihtimali yüksek tarafta satıcı kalmıyor (ask boş, ~%0,5 dolu); `python3 tools/sniper_diag.py <klasör>` bunu ölçer. Sonuçlar `tools/analyze.py` çıktısındaki "giriş saniyesine göre kırılım" ile ayrı ayrı değerlendirilir (birincil hipotez: 240-296. sn).
Ayarlar: `SniperTriggerPrice`, `SniperMaxPrice`, `SniperMinBid`, `SniperMinSeconds`, `SniperMaxSeconds`, `SniperMaxBookAgeMs`, `SniperStakeUsd`.
Kurulum (mevcut `flow`/`noflow` örneklerine dokunmadan): `bash deploy/install.sh sniper` → port 5082, veri `/var/lib/polyedge/sniper`.
Başa baş koşulu: kazanma oranı > ortalama giriş fiyatı + fee + slippage. Bu fiyatlarda bir kayıp ~19 kazancı siler; anlamlı sonuç için yüzlerce işlem gerekir.


## Sniper v2 (doğrulama, EV, kayıt) ve bağımsız replay
- **Düzeltmeler (tüm sniper örnekleri):** pay adedi, dolum anındaki GERÇEK defterden bütçeye (fee dahil) göre hesaplanır; taze defter alınamazsa emir iptal edilir
  (bayat deftere dönülmez); dolumdan hemen önce tetik şartı (bid ≥ minbid, ask bant içinde) yeniden doğrulanır.
- **İsteğe bağlı (varsayılan kapalı):** `Strategy:SniperRequireConfirm` (Chainlink z ≥ `SniperMinZ` ve Binance aynı yönde),
  `Strategy:SniperUseEv` (p_kazanma = min(Φ(z_chainlink), 1-`SniperTailProb`); EV/maliyet ≥ `SniperMinEv`). Bunlar HİPOTEZDİR, kanıtlanmış değildir.
- `bash deploy/install.sh sniperv2` → doğrulama + EV + `Bot__RecordBooks=true` açık örnek (port otomatik).
- `Bot:RecordBooks=true` → `books.csv` (ilk 5 seviye, BookPollMs aralığıyla, 100. saniyeden sonra). Bağımsız backtest:
  `python3 tools/sniper_replay.py /var/lib/polyedge/sniperv2` (A tetik / B +doğrulama / C +EV / D EV, aynı pencerelerde).


## Skew backtest
`python3 tools/skew_backtest.py /var/lib/polyedge/flow` — "momentum (30 sn) + yarı-varyans skew (120 sn)" kuralını kayıtlı pencerelerde (resmi sonuç, fee dahil)
yalnız-momentum, yalnız-skew ve yazı-tura tabanıyla karşılaştırır. Parametreler dosyanın başında sabittir; sonuca bakıp ayarlama.


## Skew modu (canlı paper)
`Strategy:Mode = Skew` (örnek: `bash deploy/install.sh skew`): momentum = (BTC_şimdi − BTC_30sn_önce)/BTC_30sn_önce·100 ve yarı-varyans skew = (RV+ − RV−)/(RV+ + RV−)
(Binance 1 sn'lik seri, son 120 sn). İkisi de > 0 → Up, ikisi de < 0 → Down; pencere başına İLK uyuşmada (30-240. sn), ask 0.10-0.90 ve spread ≤ 0.06 ise
taze defterle, gecikme + fee + gerçek derinlik modeliyle alır, çözüme kadar tutar (50 USD bütçe). `trades.csv`'de `flow_score` = skew, `rel_move_pct` = momentum (%).
Parametreler (`SkewMomentumSeconds`, `SkewLookbackSeconds`, eşikler) önceden sabittir; sonuca bakıp ayarlama. 120 sn pencere tarifte yoktur, varsayımdır.
