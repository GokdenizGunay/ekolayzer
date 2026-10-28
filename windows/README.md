# Ekolayzer (Windows)

Sistem geneli 10 bantlı ekolayzer ve limiter, Windows 10/11 için. Tüm uygulamaların ses çıkışına uygulanır.
Linux (Pardus/ETAP) sürümüyle aynı özellikler ve aynı ayar biçimi.

- 10 bant (31 Hz – 16 kHz, ±12 dB) + ön kazanç, canlı frekans yanıtı eğrisi
- Hazır ön ayarlar ve kendi ön ayarların
- Her zaman açık, ileri bakışlı limiter: eşik, oran, diz, atak, bırakma, tavan (döner düğmeler) ve kazanç azaltma göstergesi
- Hızlı stereo çıkış göstergesi
- Oturum açılışında arka planda başlar; ekolayzer açıkken bildirim alanında simgesi görünür
- Otomatik güncelleme: yeni sürüm çıkınca popup ("Tekrar gösterme" seçeneğiyle) ve sol altta "Güncelle" düğmesi

## Kurulum

`ekolayzer-setup-<sürüm>.exe` dosyasını çalıştır, "Kur"a bas ve yönetici onayını ver. Ek bir çalışma zamanı
gerekmez (Windows'ta hazır gelen .NET Framework 4.8 kullanılır). Kurulum sırasında ses birkaç saniye kesilir.

Kaldırmak için: **Ayarlar › Uygulamalar › Ekolayzer › Kaldır**. Windows'un özgün ses efektleri geri yüklenir.

## Nasıl çalışır

| Parça | Görev |
|---|---|
| `EkolayzerApo.dll` | Windows ses motoruna (audiodg) yüklenen ses işleme nesnesi (APO). Karıştırma sonrası efekt (MFX/GFX) yuvasına girer; aygıtın kendi efektini (ör. Windows ses geliştirmeleri) içinden çalıştırmaya devam eder. |
| `Ekolayzer.exe` | Arayüz, kurulum, güncelleme ve oturum açılışı görevi tek dosyada. |
| `%ProgramData%\Ekolayzer\engine.bin` | Arayüz ile ses motoru arasındaki ortak bellek: ayarlar anında motora, seviyeler arayüze geçer. |
| `%APPDATA%\Ekolayzer\settings.json` | Kişisel ayarlar (Linux sürümüyle aynı biçim). |

Ses motoru, ses efekti destekleyen tüm çıkış aygıtlarına eklenir. Elgato Wave Link gibi sanal karıştırıcıların
girişleri atlanır: ses, karıştırıcının çıktığı donanım aygıtında bir kez işlenir. Sonradan takılan aygıtlar
oturum açılışında kendiliğinden eklenir ("Ekolayzer Ses Motoru" zamanlanmış görevi).

## Derleme

Visual Studio (C++ masaüstü iş yükü) ve .NET SDK gerekir:

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1
```

Çıktı: `ekolayzer-setup-<sürüm>.exe` ve SHA-256 özeti. Sürüm `app/Core.cs` içindeki `Version` değeridir.

## Lisans

GPL-3.0-or-later — bkz. depo kökündeki [LICENSE](../LICENSE).
