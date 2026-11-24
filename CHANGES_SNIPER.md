# Sniper kod incelemesi ve değişiklikler

## Sniper kodu nerede?

Çalışan Sniper karar akışı `src/PolyEdge.App/Trading/BotService.cs` içindedir:
- `DecideAndActAsync`: `Strategy:Mode=Sniper` ise `DecideSniperAsync` çağırır.
- `DecideSniperAsync`: tetik, fiyat, bid, spread ve risk durdurucularını kontrol eder.
- `SniperConfirm`: Chainlink/Binance yön teyidi ve EV filtresini hesaplar.
- `PlaceSniperAsync`: gecikmeden sonra yeni CLOB defteri alır, tekrar kontrol eder ve paper dolumunu yapar.

`tools/sniper_*.py` dosyaları çalışan strateji değil; geçmiş kayıtlar için teşhis/replay/backtest araçlarıdır.

## Yapılan korumalar

1. Ask tavanı 0.97; spread tavanı 0.02.
2. Stake 10 USD ve özsermayenin en fazla %1'i.
3. Günlük gerçekleşmiş net zarar özsermayenin %2'sine ulaşırsa yeni girişleri durdur.
4. Art arda 2 kayıpta yeni girişleri durdur.
5. Chainlink ve Binance aynı yönü desteklemeli; Chainlink z-skoru en az `SniperMinZ` (varsayılan 2.0).
6. EV filtresi varsayılan açık. Gecikme sonrası yeni defterden bulunan ortalama gerçekleşebilir fiyat, fee ve gas maliyetleriyle yeniden kontrol edilir.
7. Emir limiti eski karar ask'ına değil gecikme sonrası taze ask'a göre hesaplanır.
8. Gecikme sonrası defter yaşı, bid, ask bandı ve spread tekrar kontrol edilir.
9. Limit içinde bütçenin en az %90'ı dolabilecek kadar ask derinliği yoksa işlem iptal edilir.

## Önemli sınırlamalar

- Bu proje paper moddur; gerçek emir göndermez.
- Chainlink z-skorundan normal dağılımla hesaplanan kazanma olasılığı henüz kalibre edilmiş bir tahmin değildir. EV filtresi bir risk kapısıdır, kârlılık garantisi değildir.
- Bu nedenle değişiklikler önce paper ortamında, aynı pencereler üzerinde eski/yeni replay ile karşılaştırılmalıdır.
- Gerçek trade logları (dolum fiyatı, fee, slippage, sonuç, PnL) olmadan 97% win-rate'in net beklentisi doğrulanamaz.
- .NET SDK bu çalışma ortamında bulunmadığından `dotnet build` çalıştırılamadı. JSON ve Python araçları için sözdizimi kontrolleri başarılı oldu.
