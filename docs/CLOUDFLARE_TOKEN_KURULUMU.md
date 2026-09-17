# ALAZ OPSDECK — iki Cloudflare hesabını bağlama

Kontrol tarihi: 14 Eylül 2026. Bu rehber mevcut salt-okunur host içindir.
Hesap oluşturma, ödeme, deployment, veritabanı sorgusu veya AI inference yetkisi gerekmez.

## Her hesap için gerekli bilgiler
- Görünen profil adı: VetaKeep / Diğer Projeler (yerel etiket).
- Account ID: o Cloudflare hesabının 32 karakterli kimliği; Zone ID değildir.
- API Token: aynı hesapla sınırlı ayrı bir Custom API Token.
- İsteğe bağlı: izlenecek herkese açık HTTPS adresleri; her satıra bir URL.
E-posta, giriş şifresi, Global API Key, Origin CA Key ve R2 S3 anahtar çifti istenmez.

## Önerilen token türü
Bu PC üzerindeki ilk kurulum için basit yol: My Profile > API Tokens > Create Token >
Create Custom Token üzerinden bir User API Token oluştur. Account Resources bölümünde
Include > Specific account > yalnız ilgili hesabı seç [S2]. Her hesap için ayrı token kullan.
Account API Token da uyumlu bir alternatiftir; kullanıcıdan bağımsız kalıcı entegrasyon
sağlar, ancak oluşturulması Super Administrator gerektirir [S1]. OpsDeck için sırf bu
nedenle rol yükseltme. Bu role zaten sahipsen Manage Account > Account API Tokens yolunu
aynı dar Read izinleriyle kullanabilirsin. Her iki tür de Bearer API tokenıdır.
Önerilen adlar: ALAZ-OPSDECK-VetaKeep-ReadOnly ve ALAZ-OPSDECK-Projects-ReadOnly.

## İzinler — tamamı Account düzeyinde Read
| Kategori | İzin adı | Erişim | Mevcut kullanım |
|---|---|---|---|
| Account | Account Analytics | Read | Workers/D1 GraphQL ölçümleri [S3] |
| Account | Workers Scripts | Read | Worker listesini okuma [S4] |
| Account | D1 | Read | Veritabanı envanteri [S5] |
| Account | Workers R2 Storage | Read | R2 envanteri/ölçümleri [S6,S7] |
| Account | Pages / Cloudflare Pages | Read | Pages projeleri ve dağıtım bilgisi [S8] |
| Account | Billing | Read | İsteğe bağlı kullanım ücretleri [S9] |

Read dışındaki Edit/Write/Run izinlerini ekleme. Read All Resources ve Edit Cloudflare Workers
şablonları gereğinden geniştir. Zone/DNS, User Details, API Tokens Edit, Account Settings,
Workers AI, Queues veya AI Gateway izinleri mevcut çağrılar için gerekmez; sonraki
adaptörlere gerçekten ihtiyaç doğarsa ayrı değerlendirilir. Read tokenları da sırdır.
Pages adı arayüze göre Cloudflare Pages veya Pages olarak görünebilir [S7,S8].
R2 yönetim API'si için Workers R2 Storage Read kullanılır; Object Read only S3 tokenı değil [S6,S11].

## Kapsam ve oluşturma
Hesap kapsamı alanı varsa Include > Specific account > ilgili hesabı seç.
VetaKeep tokenı VetaKeep hesabına; diğer token ikinci hesaba ait olmalı.
All accounts seçme. Bu sürüm için Zone Resources/User izinleri ekleme.
Client IP filtresi isteğe bağlıdır. Sabit genel çıkış IP'n varsa sınırlayabilirsin;
değişken IP'de kısıtlı token ağ değişiminde çalışmayabilir. Yerel 192.168.x.x adresi yazma.
Öneri: ilk kurulumda 90 günlük sona erme tarihi belirle; süre dolunca manuel yenile.
Continue to summary ile izinleri gözden geçir, sonra Create Token.
Token yalnız bir kez gösterilir; güvenli parola yöneticisinde sakla [S2].
Token değerini sohbete, kaynak koda, ekran görüntüsüne veya loga ekleme.

## Account ID nereden alınır?
Doğru hesabı seç; Cloudflare aramasında Ctrl+K > Copy account ID komutunu kullan.
Alternatif: Workers & Pages > Account Details > Account ID [S10].
İkinci hesaba geçip aynı işlemi ayrı yap. Account ID tek başına API anahtarı değildir.

## Yerel uygulamaya giriş
`OPSDECK_BASLAT.cmd` > Bilgisayar / İki Cloudflare hesabı.
VetaKeep sekmesine kendi Account ID/tokenını; Diğer Projeler sekmesine ötekini gir.
Her profilde “Bu hesabı salt-okunur izle” seçeneğini aç. Billing Read eklediysen
“Billable Usage sorgula (ayrı yetki gerekebilir)” kutusunu da aç.
Kaydet ve bağlantıları yenile; ardından Kaynaklar / Projeler > Kaynakları keşfet / yenile.

Anahtar CurrentUser DPAPI ile ilgili Account ID'ye bağlı yerel dosyada korunur;
panele, kaynak paketine ve uygulama loglarına gönderilmez. Global API Key kullanma.
Oluşturduğun tokenın aktif olması her servise yetkisi olduğunu kanıtlamaz.
401/403 halinde hesap eşleşmesi/Read izinleri; 429 halinde sorgu sıklığı incelenir.
Bir hata için otomatik Write/Edit ekleme. Her API yetkisi ayrı doğrulanır.

## Maliyetle ilgili sınır
Mevcut kod GET /accounts/{account_id}/billable-usage v1 arayüzünü kullanır.
API referansında v1 “Alpha” etiketlidir [S12]; 3 Ağustos 2026 tarihli resmî duyuru,
/billable-usage endpointinin self-serve hesaplara açıldığını ve verinin günlük
güncellendiğini belirtir [S13]. Bu nedenle “Alpha = hiçbir hesap kullanamaz” sonucu çıkarılmaz.
14 Eylül M4.3 oturumunda iki hesabın Workers, D1, R2 ve kullanım ücreti okumaları
gözlendi. Pages için aynı tokenlarla per_page=10 HTTP 200 verdi; mevcut istemcinin
per_page=50 sorgusu HTTP 400 veriyor. Bu istemci hatası henüz düzeltilmedi; daha
yüksek token yetkisi gerekmediği karşılaştırmayla doğrulandı. Ayrıntı: M4_3_DURUM.md. Kullanım bedeli
kesin fatura, sabit plan ücreti, vergi veya aylık tahmin değildir.
Billing'i kapalı bırakmak PC/ajan ve diğer kaynak okumalarını engellemez.
R2 keşfi şu an default jurisdiction kapsamındadır; farklı kapsamlar henüz eklenmedi.

## Resmî kaynaklar
[S1] https://developers.cloudflare.com/fundamentals/api/get-started/account-owned-tokens/
[S2] https://developers.cloudflare.com/fundamentals/api/get-started/create-token/
[S3] https://developers.cloudflare.com/analytics/graphql-api/getting-started/authentication/api-token-auth/
[S4] https://developers.cloudflare.com/api/resources/workers/subresources/scripts/methods/list/
[S5] https://developers.cloudflare.com/api/resources/d1/subresources/database/methods/list/
[S6] https://developers.cloudflare.com/api/resources/r2/subresources/buckets/methods/list/
[S7] https://developers.cloudflare.com/fundamentals/api/reference/permissions/
[S8] https://developers.cloudflare.com/api/resources/pages/subresources/projects/methods/list/
[S9] https://developers.cloudflare.com/billing/understand/billing-permissions/
[S10] https://developers.cloudflare.com/fundamentals/account/find-account-and-zone-ids/
[S11] https://developers.cloudflare.com/r2/api/tokens/
[S12] https://developers.cloudflare.com/api/python/resources/billing/subresources/usage/methods/get_account_usage_v1/

[S13] https://blog.cloudflare.com/billable-usage-api/

M4.3 panel: Projects sekmesi, hesap seçimi, proje satırına dokunma ile kaynak ayrıntısı;
ALL RESOURCES bütün kaynakları listeler. Oklar sayfalar arasında geçer. Panel sayfalaması
Cloudflare API sorgusu başlatmaz. Listeyi Windows penceresinden yenile; eşlemeleri kaydet.
