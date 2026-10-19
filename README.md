# Ekolayzer

Pardus için sistem geneli ses ekolayzeri ve limiter. Pardus 25 (GNOME, XFCE), **Pardus ETAP 23 ve ETAP 25** (etkileşimli tahtalar) ve MEB sürümlerinde çalışır. Tüm uygulamaların ses çıkışına (tarayıcı, müzik ve video oynatıcılar, oyunlar…) otomatik olarak uygulanır ve tek bir `.deb` dosyasıyla kurulur.

![Ekolayzer ekran görüntüsü](docs/ekran-goruntusu.png)

## Özellikler

- **10 bantlı ekolayzer** (31 Hz – 16 kHz, ±12 dB), ön kazanç ve canlı frekans yanıtı eğrisi
- **13 hazır ön ayar** (Bas Güçlendirme, Vokal, Rock, Gece Modu…) ve kendi ön ayarlarını kaydetme
- **Limiter** (her zaman açık): stereo bağlı, ileri bakışlı (lookahead) tepe sınırlayıcı. Eşik, oran (∞:1'e kadar), diz, atak, bırakma ve tavan döner düğmelerle ayarlanır; çıkış hiçbir zaman tavanı aşmaz, yani kırpılma olmaz
- **Göstergeler**: hızlı tepki veren stereo çıkış seviyesi (tepe tutma ile) ve kazanç azaltma göstergesi
- Ayar değişiklikleri **sesi kesmeden anında** uygulanır
- Ses motoru **her oturum açılışında arka planda otomatik başlar**; pencereyi açman gerekmez
- Kurulumda **masaüstü kısayolu** eklenir
- **Hafif**: ses motoru çalarken tek çekirdeğin ~%0,75'ini kullanır; sessizlikte %0. Arayüz açıkken ek olarak ~%1,75 CPU ve ~80 MB bellek

## Kurulum

[Releases](../../releases) sayfasından son `ekolayzer_<sürüm>_amd64.deb` dosyasını indir, sonra çift tıklayıp Pardus Paket Kurucu ile kur ya da terminalde şunu çalıştır:

```bash
sudo apt install ./ekolayzer_1.4.0_amd64.deb
```

Desteklenen tüm sürümlerde gereken her şey sistemde zaten kurulu olduğundan **internet bağlantısı gerekmez**.

ETAP'ta kurulum ve güncelleme `etapadmin` parolasını ister; kurulumdan sonra ses motoru `ogretmen` dahil tüm hesaplarda çalışır ve her hesabın masaüstüne kısayol eklenir.

Kurulumdan sonra ses motoru hemen devreye girer. Masaüstündeki veya uygulama menüsündeki **Ekolayzer** simgesiyle ayar penceresini açabilirsin.

### Gereksinimler

| Sürüm | Taban | Ses altyapısı | Durum |
|---|---|---|---|
| Pardus 25 GNOME / XFCE | Debian 13 | PipeWire 1.4, WirePlumber 0.5 | ✅ |
| Pardus ETAP 25, MEB 25 | Debian 13 | PipeWire 1.4, WirePlumber 0.5 | ✅ |
| Pardus ETAP 23 | Debian 12 | PipeWire 0.3.65, WirePlumber 0.4 | ✅ |

Genel olarak: amd64, PipeWire ≥ 0.3.65, WirePlumber ≥ 0.4.13, GTK 3 (`gir1.2-gtk-3.0`), Python ≥ 3.9.

### Otomatik güncelleme

Ekolayzer her açılışta GitHub'daki son sürümü denetler: oturum açılışında arka planda, uygulama elle açıldığında ve kurulum/güncelleme sonrasında. Daha yeni bir sürüm varsa, ana pencere kapalı olsa bile bir popup çıkar:

- **Güncelle**: paket indirilir, SHA-256 özeti doğrulanır ve sistem parola penceresinden (polkit) onay alınarak kurulur. Ses motoru yeni sürümle kendiliğinden yeniden başlar.
- **Sonra**: bir sonraki açılışta yeniden sorulur.
- **Tekrar gösterme** kutusu işaretlenirse o sürüm için açılışta popup çıkmaz; daha yeni bir sürüm yayımlanınca yeniden sorulur.

Güncelleme olduğu sürece ana pencerenin sol alt köşesinde **Güncelle** düğmesi görünür; tıklayınca güncelleme doğrudan indirilip kurulur.

Güncelleme yoksa denetim yaklaşık 0,3 saniyede, arayüz yüklenmeden biter. Menüdeki **Güncellemeleri denetle** ile elle de denetleyebilirsin. Kurulum yardımcısı yalnızca `ekolayzer` adlı ve yüklü sürümden yeni bir paketi kabul eder; eski sürüme düşürmeye izin vermez.

## Kullanım

| Denetim | İşlev |
|---|---|
| Sol üstteki anahtar | Ekolayzeri tamamen açar/kapatır |
| Kaydırıcılar | Ön kazanç ve 10 bandın kazancı |
| Ön ayar menüsü | Hazır veya kaydedilmiş ön ayarı seçer; 💾 ile geçerli ayarı kaydeder |
| Çıkış göstergesinin yanındaki ok | Limiter panelini açar/kapatır |
| Döner düğmeler | Sürükle (Shift: ince ayar), fare tekerleği veya ok tuşları; çift tıklama varsayılana döndürür |

Ayarlar `~/.config/ekolayzer/settings.json` dosyasında kullanıcı başına saklanır. Yedeklemek veya başka bir bilgisayara taşımak için bu dosyayı kopyalaman yeterli.

## Nasıl çalışır?

- Ses işleme, ayrı bir PipeWire sürecinde **filter-chain** modülüyle yapılır.
  - **WirePlumber 0.5+** (Pardus 25, ETAP 25): filtre, *smart filter* özelliğiyle varsayılan çıkış aygıtının önüne kendiliğinden eklenir.
  - **WirePlumber 0.4** (ETAP 23): küçük bir denetleyici filtreyi varsayılan çıkış yapar ve çıkışını gerçek aygıta bağlar. Ayarlardan başka bir aygıt seçilirse filtreyi oraya yönlendirir; Ekolayzer kapatılınca eski varsayılan aygıtı geri yükler.
  - Her iki durumda da tüm uygulamalar etkilenir ve çıkış aygıtı değişince filtre onu izler.
- Ekolayzer bantları PipeWire'ın yerleşik biquad süzgeçleridir.
- Limiter ve seviye ölçümü, pakette gelen küçük bir C **LADSPA eklentisidir** (`src/dyn/ekolayzer_dyn.c`). Ölçümler arayüze paylaşılan bellek üzerinden iletilir; ek süreç veya kayıt akışı gerekmez.
- Ses motoru `ekolayzer.service` adlı bir systemd kullanıcı hizmetidir. Arayüz (GTK 3) yalnızca ayarları değiştirir; pencere kapalıyken de ses işlenmeye devam eder.

## Kaynaktan derleme

Debian/Pardus üzerinde:

```bash
sudo apt install gcc libc6-dev
./build-deb.sh
```

Sonuç: `ekolayzer_<sürüm>_<mimari>.deb`.

## Kaldırma

```bash
sudo apt remove ekolayzer
```

Kaldırınca ayar dosyan silinmez; yeniden kurarsan ayarların geri gelir. Tamamen temizlemek için `~/.config/ekolayzer` klasörünü sil.

## Lisans

[GPL-3.0-or-later](LICENSE) © 2026 Gökdeniz G.
