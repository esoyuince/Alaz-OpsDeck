# ALAZ OPSDECK — opsdeck.inventory.v1 (M4.3)

## Amaç ve yetki sınırı
Host belleğindeki salt-okunur keşif anlık görüntüsünü panelde sayfalar. Panel isteği
Cloudflare API çağrısı, SQL, kaynak değişikliği veya işletim sistemi komutu değildir.
USB bağlantısı fiziksel olarak güvenilir kabul edilir; bu protokol WSS kimlik doğrulaması
veya uzaktan yönetim protokolü olarak kullanılamaz. Tokenlar hiçbir çerçevede bulunmaz.

## İstek yönü: panelden host'a
`opsdeck.ui: INVENTORY_REQUEST slot=0 view=0 page=0 group=all request=1`

- slot: 0–1; view: 0 proje / 1 kaynak; page: 0–1999; request: 1–Int32.MaxValue.
- group: all veya 16 küçük hexadecimal karakter. Proje görünümünde yalnız all.
- Dokunmatik seçimi request kimliğini artırır ve önceki görünümü geçersiz kılar.
- Her 5 saniyede aynı isteğin kimliği korunarak yeniden yayımlanması, host/USB yeniden
  bağlantısında seçili sayfanın geri alınmasını sağlar; görünümü boşaltmaz.
- Host değişen istekleri en sık saniyede bir işler; sayfalama API kotası tüketmez.

## Yanıt yönü: host'tan panele
JSON type: opsdeck.inventory.v1. UTF-8 çerçeve bütçesi 3000 bayt; her sayfa en fazla 4 satır.

| Alan | Anlam |
|---|---|
| generation | Host oturumunun 8 karakterli küçük hexadecimal etiketi |
| test | Zorunlu boolean; test çerçeveleri ekranda TEST DATA olarak ayrılır |
| slot / view / requested_page / group / request_id | Aktif panel isteği ile tam eşleşmelidir |
| page / total_pages / total_rows | Sıfır tabanlı gerçek sayfa, sayfa sayısı, seçili satır sayısı |
| known_resources | -1 bilinmiyor; 0 yalnız doğrulanmış boş envanter; pozitif kısmi alt sınır olabilir |
| complete_sources / source_count | Tam listelenmiş tür sayısı / 4 (Workers, D1, R2, Pages) |
| state / age_s | Kaynak durumu ve en eski keşif kaydının yaşı; -1 yaş bilinmiyor |
| map_ok | Yerel kaydedilmiş proje eşlemelerinin okunup okunamadığı |
| account_name / scope | Panele uygun ASCII görünür etiketler |
| rows | key, label, detail alanlarından oluşan en fazla 4 satır |

Sayfa sayısı ceil(total_rows/4). Son sayfa istenenden küçükse page son geçerli sayfaya
kırpılır; requested_page özgün isteği korur. Boş görünüm total_pages=0 ve page=0 kullanır.
Etiketler 48, ayrıntılar 96, hesap adı 22 ASCII karakterle sınırlıdır. Uzun etiketler ~
ile kısaltılır; tam ad ve kaynak kimliği Windows'ta kalır. Türkçe harfler Latin karşılıklarına
çevrilir. Grup kimliği görünen etiketten değil özgün proje adından türetilir.

## Tutarlılık ve güvenli gösterim
Kaynak kimliği hesap + tür + kapsam + ID. Hesaplar ayrı işlenir; iki boş Account ID
birleştirilmez. Atanmamış grup ile “Unassigned” adlı gerçek proje farklı kimliklere sahiptir.
Yalnız kaydedilmiş eşlemeler host tarafından yayımlanır. Windows'taki kaydedilmemiş
filtre/düzenleme taslakları panele gönderilmez. Eşleme dosyası okunamazsa projeler uydurulmaz.
Windows oturumu kilitliyken proje/kaynak adları yerine boş, Private işaretli görünüm gönderilir.

Tam kaynak kapsamı yalnız sorgulanmış kapsamlardır. R2 keşfi default jurisdiction ile
sınırlıdır; EU/US gibi diğer kapsamlar için tamlık iddia edilmez. Liste varlığı kaynak
sağlığını, deployment başarısını veya global uptime'ı kanıtlamaz.

Keşif 900 saniyeden eskiyse STALE; çerçeve 16 saniye gelmezse ayrıca taşıma bayatlığı
uygulanır. Seri çerçevelerin yenilenmesi keşif yaşını sıfırlamaz. Kaynak keşfi Windows
penceresindeki yenileme düğmesiyle yapılır; panelde gezmek keşfi yenilemez.

## Ayrıştırıcı sınırları
Ana UART alıcısı: 4096 bayt satır tamponu, en fazla 8 iç içe JSON seviyesi, yinelenen JSON
anahtarlarını ve satır taşmasını ret. Yeni ayrıştırıcı sonlu tamsayı, boolean, ASCII,
hexadecimal kimlik, satır/sayfa/kapsam tutarlılığı ve aktif istek eşleşmesini doğrular.
Yanlış hesap, proje, önceki istek veya sayfa yanıtı mevcut görünümü değiştiremez.
Her kabul edilen yanıt INVENTORY_RX ile bildirilir; reddedilen yanıtta ACK verilmez.

## İlgili dosyalar
- host/OpsDeck.Core/InventoryPaging.cs — saf sayfalama ve protokol modeli.
- host/OpsDeck.Core/PanelInventoryEngine.cs — kayıtlı eşleme / oturum kilidi / host entegrasyonu.
- host/OpsDeck.Core/AppEngine.cs — mevcut USB akışında yanıt ve istek yönetimi.
- firmware/panel/main/opsdeck_inventory.c — gerçek C ayrıştırıcısı ve seçili istek.
- firmware/panel/main/opsdeck_inventory_ui.c — native LVGL sayfası.
- host/OpsDeck.Tests/M43Tests.cs — 55 yeni host kontrolü.
- tools/native-tests-m43/inventory_test.c — 52 gerçek C kaynak testi (OS/zaman shim'leri).
- tools/inventory_protocol_smoke_m43.py — gerçek COM9 üzerinde açık TEST DATA testi.

Fiziksel LVGL görünümü, yeni sekmenin dokunma hedefleri ve uzun süreli kararlılık,
yalnız ayrıştırıcı veya UART ACK testiyle kapanmış sayılmaz. Güncel sonuç: M4_3_DURUM.md.
