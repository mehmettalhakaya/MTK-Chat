# MTK Chat

Her düzeltmenin belirtisi, nedeni ve doğrulaması [değişiklik günlüğünde](docs/DEGISIKLIK_GUNLUGU.md) tutulur.

Bu depo yalnız kaynak kodu, testleri ve gerekli tasarım varlıklarını içerir. Derlenmiş uygulama, test çıktıları, gerçek `.env`, API anahtarları, cihaz anahtarları, veritabanı ve lisanslı DevExpress DLL'leri Git'e eklenmez. Yapılandırma örneği [`.env.example`](.env.example) içindedir; `YOURAPIKEY` ve diğer `YOUR...` değerleri gerçek sır değildir. Örnek dosya otomatik yüklenmez; sunucuya ortam değişkenleri veya yerel geliştirmede .NET User Secrets ile değer verin. DevExpress paket kaynağınızın erişim anahtarını kaynak depoya yazmayın.

Kaynaktan derlemek için Windows, .NET SDK `9.0.314` (uyumlu son yama) ve lisanslı DevExpress WinForms `26.1.5` paketine erişim gerekir:

```powershell
dotnet build MTKChat.slnx -c Release
dotnet test MTKChat.slnx -c Release --no-build
node tools/VerifyInviteWeb.mjs
```

Bu komutlar normalde yeniden `bin/obj` üretir; bunlar Git tarafından yok sayılır. Proje dizinini temiz tutmak için build/test komutlarına aynı proje-dışı `--artifacts-path` dizinini verebilirsiniz. Gerçek site hesabıyla giriş ve üretim veritabanı ayrıca yapılandırılmalıdır; yerel testler canlı hesap gerektirmez. Depoyu görünür yapmak DevExpress ticari lisansı veya uygulama için ek bir açık kaynak lisansı sağlamaz.

MTK Chat; `mtkaya.me` kullanıcılarının, birbirlerinin ve açıkça sohbete eklenmiş yapay zekâ agentlarının uçtan uca şifreli mesajlaşması için hazırlanmış C# prototipidir.

## Şu anda çalışanlar

- DevExpress WinForms 26.1 bileşenleriyle yeniden tasarlanmış koyu temalı sohbet, giriş ve kullanıcı yönetimi ekranları
- Türkçe aramalı/kategorili emoji paneli; 71 özgün renkli vektör emoji, yazı kutusunda renkli çizim ve metinle karışık gelen/giden mesajlarda satır içi emoji
- `mtkaya.me/api/login` üzerinden gerçek site hesabı doğrulaması
- Site veritabanındaki bütün etkin kullanıcıların ve `admin`/`user` rollerinin otomatik eşitlenmesi; demo hesabı yoktur
- Mevcut Lounge üyelerini koruyan, yeni normal hesapları otomatik gruba almayan davetli üyelik
- Tüm gruplar için mtkaya.me üzerinden çoklu davet yönetimi; listeleme/kopyalama, özel veya sınırsız geçerlilik, seçili bağlantıyı silme ve açık katılım onayı
- Grup yöneticisinden normal üye/Mod çıkarma; hesap banlamadan ve site admini yetkisi vermeden üyelik sonlandırma
- Profilim içinde ad-soyad gösterimi; DevExpress İsim/Soyisim alanlarıyla kendi profil adını ekleme/düzenleme
- İstemci tarafında P-256 ECDH + HKDF-SHA256 + AES-256-GCM şifreleme
- Gönderen ECDSA imzası ve alıcı başına ayrı şifreli zarf
- Windows DPAPI ile kullanıcı hesabına bağlı cihaz özel anahtarı saklama
- Metin ve 5 MB'a kadar görsel mesaj
- Mikrofonla 45 saniyeye kadar sesli mesaj; göndermeden önce dinleme/kaldırma ve sohbet içinde oynatma
- Sohbet başlığındaki avatardan grup fotoğrafı seçme, değiştirme ve kaldırma
- Panodan Ctrl+V ile görsel yapıştırma ve gönderim öncesi kaldırılabilir önizleme
- MTK uygulama/pencere ikonu (`tools/GenerateMtkIcon.ps1` ile yeniden üretilebilir)
- 30 saniye, 5 dakika, 1 saat ve 1 gün süreli mesajlar
- “Benden sil” ve gönderenin ilk 15 dakika içinde kullanabildiği “Herkesten sil”
- Kullanıcıya özel sohbet/mesaj temizleme, sohbet kartı sağ tık işlemleri ve gruptan çıkma
- Tümü / Okunmamış / Favoriler / Kişiler / Gruplar / Arşiv filtreleri; gerçek okunmamış sayısına göre boş durum ve tüm sohbetlere dönüş
- Hesaba/cihaza özel sohbet arşivi; grup ve kişi sohbetlerinde 8 saat / 1 hafta / süresiz bildirim susturma
- Yalnız arka plandaki yeni okunmamış, arşivlenmemiş/sessiz olmayan sohbet için ses çıkarmayan görev çubuğu bildirimi
- Yalnız açıkça seçilen kişilere 24 saatlik uçtan uca şifreli metin/görsel durumları; kendi durumunu silme
- Kişisel favori sohbetler ve mesaj yıldızlama; sohbet menüsünden yıldızlı mesaj görünümü ve asıl mesaja dönüş
- Gönderilen mesajın sağ tık menüsünden temaya uyumlu Mesaj bilgisi paneli; gerçek alıcıların okundu, teslim ve bekleyen durumları/tarihleri, özel sohbet ve gruplarda aynı akış
- Ortak mesaj sabitleme: sohbet başlığının altında en fazla 3 mesaj, 24 saat / 7 gün / 30 gün süre ve asıl mesaja dönüş
- Yenilenen gelen/giden sesli arama ekranı: kişi fotoğrafı, katılımcı durumları, görüşme süresi, mikrofon/hoparlör kapatma ve karşılaştırılabilir güvenlik kodu
- Kullanıcı engelleme ve Ayarlar → Engellenenler listesinden engel kaldırma
- Admin panelinden admin yetkisi verme/alma, susturma, banlama ve geri alma
- Seçili sohbet için ayrıca susturma ve yazma yasağı; sağda çevrimiçi/çevrimdışı kullanıcı listesi
- Mesaj ve dosya içerikleri için sunucuda yalnız şifreli saklama; profil/rol/üyelik bilgileri ayrı metaveridir
- Süresi dolan şifreli verileri kaldıran arka plan görevi
- `@gemini`, `@groq`, `@agents` ve `/roundtable` agent komutları
- Doğal dille kontrollü agent sohbeti ve “Gemini dur”, “Groq dur”, “ikiniz de durun” komutları
- Sohbet bazında “Gemini sadece sen konuş” / “Groq sadece sen konuş” tercihini sonraki mesajlarda koruma
- Token sınırında otomatik devam ve geçici 429/5xx hatalarında retry/backoff
- Gemini önizleme modeli kotaya girerse kararlı Flash-Lite modele otomatik geçiş

## Çalıştırma

### Güncel paket — Yazarken ve mesaj içinde renkli emojiler (8 Ekim 2026)

8 Ekim kaynak temizliğiyle güncel uygulama proje dışındaki masaüstü `MTK Chat 2026.10.8.1` klasörüne çıkarıldı: kökte `MTKChat.Desktop.exe` ve `MTKChat.zip` bulunur. ZIP'i tamamen bir klasöre çıkarın. Dosya sürümü `2026.10.8.1`, ürün sürümü `2026.10.08-inline-emojis-key-status`. Hedef Windows bilgisayarda .NET 9 Windows Desktop Runtime gerekir; yerel pakette DevExpress runtime bileşenleri bulunur. Eski açık uygulamayı kapatıp bu EXE'yi açın. Derlenmiş paket bu kaynak depoya yüklenmez; aşağıdaki eski `artifacts/...` yolları yalnız tarihsel doğrulama kayıtlarıdır, artık proje içinde mevcut değildir. Tam geçmiş test kanıtları proje dışındaki temizlik yedeğinde korunur; masaüstü paketinin `Dogrulama` klasörü runtime manifestini ve paket doğrulama özetini içerir.

Mesaj kutusundaki **emoji** düğmesi, Türkçe arama ve **Yüzler / Sevgi / İşaretler / Kutlama / Doğa / Nesneler** kategorileri olan yeni paneli açar. Seçim, aramaya geçmeden önceki imleç konumuna veya seçili metnin yerine eklenir; **Enter** seçim yapar, mesaj göndermez; **Escape** taslağı koruyarak paneli kapatır. Sohbet/hesap/taslak değişmişse eski panel seçimi yeni taslağa uygulanmaz.

Katalogdaki 71 simge özgün MTK vektör çizimidir; WhatsApp görselleri kopyalanmamıştır. Yazı kutusunda gerçek DevExpress editörünün içinde renkli çizilir; odaklı ve yazılabilir kutuda uygun simgeler ortak zamanlayıcıyla hareket eder. Gelen/giden metinle karışık mesajlar ve dört veya daha fazla emoji de desteklenen simgeler için **statik renkli satır içi** çizim kullanır. Yalnız desteklenen **1–3 emojiden** oluşan mesajlar büyük görünür; 57 hareketli simgede yüz mimikleri veya uygun hareket vardır. Botların sık kullandığı `☺` ve `☺️` aynı özgün artwork'ü kullanır. Kopyalama, sohbet önizlemesi, yıldızlama, sabitleme ve şifreli gönderim hâlâ özgün Unicode kullanır; karşı tarafın yeni sunumu görmesi için yeni istemci gerekir.

“Mesajınızı yazın” ipucu yalnız kutu boş ve odaksızken görünür; tıklanınca gerçek editör odaklanır ve ipucu caret/IME alanından çekilir. İpucu mesaj verisi değildir. Boş görsel taslağının bir piksellik siyah çizgi bırakması da görünürlük yaşam döngüsü düzeltilerek giderildi.

Katalog dışı ten tonu/ZWJ/bayrak dizileri ve RTL paragraflar anlamı/sırası değiştirilmeden native çizimde kalır. Ek UI yükünü sınırlamak için 4.096 UTF-16 biriminden uzun taslaklar özel katalog animasyonu yerine native DirectX emoji sunumuna, 16.384 birimden uzun karma mesajlar eski native metin çizimine döner; içerik kesilmez. Gerçek ikinci fiziksel PC, monitörler arası DPI ve IME ayrıca denenmelidir.

Başlıktaki **Alıcı eksik** bir model API anahtarı hatası değildir: bir katılımcının chat cihazı genel şifreleme anahtarı henüz kayıtlı değildir. Uyarıya tıklayın veya üzerinde bekleyin; ilgili adlar ve giriş gereği açıklanır. Rapor hesap/sohbet/seçime bağlıdır; başka sohbetin uyarısı taşınmaz. Bilinen eksikler sınırlı arka plan kontrolüyle yeniden sorgulanır. Normal grup/özel sohbette eksik anahtar gönderimi durdurur; mevcut Lounge istisnasında yalnız anahtarı olanlar şifreli zarf alır. Anahtar sonradan hazır olunca önceki mesaj kendiliğinden yeniden gönderilmez. Anahtar silme/yenileme veya şifresiz gönderim yapılmadı; VDS değişmedi. Ayrı dosya-gönderim yolu bu yeni durum raporuna bağlanmadı.

Panel yalnız vurgulanan hücreyi hareketlendirir; mesajlar ortak bir UI zamanlayıcısı kullanır. Gizli, kırpılmış, arka plandaki veya küçültülmüş pencerelerde ve Windows animasyonları kapalıyken hareket durur. Windows tercihi salt okunur [SPI_GETCLIENTAREAANIMATION](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-systemparametersinfow) ile izlenir; sistem ayarı değiştirilmez. Yerel emoji regresyonu **`--verify-emojis`**, tüm arayüz regresyonu **`--snapshot`** ile çalıştırılır; sentetik hesap/mesaj kullanır, gerçek mesaj göndermez veya mikrofon açmaz. İkinci fiziksel bilgisayar, gerçek ekran okuyucu ve monitörler arası DPI geçişi ayrıca denenmelidir.

Bu paketin birim/yerel HTTP doğrulaması **387/387**, web VM **15 senaryo/192 assertion** ve aynı yayımlanmış DLL ile **tam arayüz regresyonu** geçti (0 hata/uyarı build, boş stderr). Gerçek MemoEdit QA penceresinin native yakalaması renkli emoji pikselleri içerir; Unicode, seçim, Undo, boş ipucu, read-only/dispose ve alıcı durum yarışı denetlenir. Geçici gerçek kripto anahtarlarıyla sahte HTTP gönderiminin alıcı zarfları çözülüp özgün metinle eşleştirildi; gerçek hesaba mesaj gönderilmedi. Birleşik son UI koşusu ve 74 runtime/ZIP dosyasının hash kanıtı `artifacts/current/validation/PACKAGE_VERIFICATION.md`, görseller ve TRX `artifacts/current/tests` içindedir. Windows hareket politikası kapalıysa native QA statik kare tekrar kullanımını doğrular; gerçek hareket gözlemlenmiş gibi yazılmaz.

Mesajı sağ tıklayıp **Sabitle** seçin; **24 saat / 7 gün / 30 gün** arasından süre belirleyin. Sohbetin üstündeki sabit mesaj şeridi **1/3** gibi sayacıyla diğer sabitlere geçer; mesaja tıklamak asıl iletiye kaydırır. En fazla 3 aktif sabitleme vardır. Özel sohbette iki insan katılımcı, grupta üye site admini/grup yöneticisi/Mod sabitleyebilir veya kaldırabilir; normal grup üyesi yalnız görür. Gruba üye olmayan site admini erişim kazanmaz. Kişisel silme diğer üyelerin sabitlemesini kaldırmaz; herkes için silinen veya süresi dolan mesaj şeritten de kalkar. Önizleme yalnız cihazda doğrulanan/çözülen mesajdan gelir; sunucuda yeni açık metin veya anahtar tutulmaz. Sabitleme kimlikleri/süreleri yönlendirme metaverisidir ve diğer üyelere eşitlenir; kişisel yıldızlardan farklıdır.

Başlıktaki **telefon** birebir sohbette karşı tarafı doğrudan arar; grupta modern kişi seçicisinden en fazla **5 kişiyi** davet edin (siz dahil 6 kişi). Gelen arama açıkça kabul edilmeden mikrofon başlamaz. Yuvarlak simgelerin altındaki **Mikrofon / Hoparlör / Bitir** kontrolleri ve katılımcı kartları arama durumunu gösterir. Süre yalnız doğrulanmış bir eş güvenli bağlantıya katıldığında başlar; yeniden bağlanmada sıfırlanmaz, bitişte durur. **Kod** üzerinden karşı tarafla güvenlik kodunu karşılaştırabilirsiniz. Yanıtlanmayan davet 60 saniyede biter; bağlantı kaybı sınırlı toparlanma yapar, kullanıcıya gerçek hata nedeni gösterilir. Kamera/video araması eklenmedi.

Ses aktarımı mevcut imzalı geçici anahtarlar ve alıcıya özel AES-GCM şifreli PCM/HTTPS relay yolunu kullanır; sunucu sesin açık içeriğini/özel anahtarı almaz, ancak katılımcı/zaman metaverisini bilir. Bu sürüm WebRTC/Opus veya akustik yankı giderme değildir; WhatsApp ile eşdeğer güvenlik denetimi ya da fiziksel ses kalitesi testi yapılmış gibi sunulmaz. İki gerçek bilgisayarda mikrofon/hoparlör ve internet koşulları ayrıca denenmelidir.

Sabitleme/arama sunucu sürümü önceki turda VDS'deki ayrı chat servisinde etkinleştirildi; bu emoji güncellemesi sunucuya değişiklik gerektirmez. Önceki binary, chat snapshot ve davet anahtarları yedekli tutuldu; site/nginx/servis ayarları ve site kullanıcı tablosu değiştirilmedi. Önceden var olan 20 snapshot alanının yedek/canlı SHA-256 karşılaştırması eşit, yalnız yeni boş Pins alanı eklendi. Önceki sabitleme/arama turunun doğrulaması: 339/339 birim/yerel HTTP testi; o paketle tam UI exit 0/boş stderr (37 pin, 96 arama UI denetimi dahil); web VM 15 senaryo/192 denetim. Gerçek hesaplarla canlı sabitleme yazması ve iki gerçek cihaz araması denenmedi.

Önceki menü tasarım turu korunur: sohbet kartına sağ tık veya başlıktaki **…** kompakt, vektör simgeli bir menü açar. Favori/yıldız/arşiv/sessiz tercihleri üstte, üye bilgisi ve **Grup seçenekleri** ortada, kişisel mesaj silme/sohbet silme/gruptan çıkma ayrı kırmızı bölümde yer alır. **Grup seçenekleri → Grup adını değiştir / Davet bağlantılarını yönet / Grup fotoğrafını düzenle / Grup yönetimi** aynı yetki kurallarını korur; erişilemeyen işlemler yine gösterilmez. Alt menüler aynı koyu kart yüzeyi ve tek simge/onay sütununu kullanır. Yerel menü regresyonu `--verify-conversation-menu`, yeni sabitleme/arama arayüz regresyonu `--verify-pins-calls` ile çalışır; ikisi de tam `--snapshot` içindedir.

Arşiv dahil altı sohbet filtresi gerçek çizim fontuyla ölçülür: geniş menüde tek satır; dar pencere veya büyük yazıda dengeli 3+3 / 2+2+2 / birer sütun. Yazı küçültülmez, filtre gizlenmez ve Arşiv tek başına taşma satırına düşmez. Arşiv sayısı, font, sidebar genişliği ve gizli menüden dönüş değiştiğinde yerleşim yenilenir; tüm satırlar sohbet listesinin üzerinde kendi alanını ayırır. `%100–200` font büyütme ve sidebar genişlik eşiklerinin çevresi yerel test edilir; bu ikinci fiziksel PC/DPI geçişi garantisi değildir. Önceki arşiv, sessiz, durum ve engel listesi özellikleri korunur.

Sohbet kartını sağ tıklayıp **Sohbeti arşivle / Arşivden çıkar** kullanın. **Arşiv** filtresi kişisel arşivi açar; gelen mesaj sohbeti kendiliğinden arşivden çıkarmaz. Aynı menüde **Bildirimleri sessize al → 8 saat / 1 hafta / Süresiz** ve **Sessizi kaldır** bulunur. Bu tercihler yalnız bu Windows/chat hesabının DPAPI dosyasında saklanır, diğer kişilerin mesajlarını veya grup üyeliğini değiştirmez. Gelen aramalar susturulmaz. Arka plandaki yeni mesaj görev çubuğunu üç kez sessizce yanıp söndürür; ilk yükleme, arşiv/sessiz sohbetler ve ön plan mesajları bildirim üretmez. Uygulama odağa gelince yanıp sönme durur.

Soldaki halka simgesi **Durumlar** alanını açar. **Yeni durum** ile metin veya görsel ekleyip **yalnız görebilecek kişileri açıkça seçin**; varsayılan alıcı yoktur. En fazla 50 kişi seçilebilir; alıcının uygulamaya giriş yapmış bir cihaz anahtarı gerekir. Metin 2.000 karakter, görsel küçültülmüş 512 KB şifreli gövde sınırındadır; aynı anda en fazla 5 kendi durumunuz bulunabilir. Paylaşım sunucunun kabulünden 24 saat sonra erişimden kalkar; kendi durumunuzu daha erken silebilirsiniz. Listede yalnız kimlik/tür/zaman metaverisi alınır; içerik ancak **Göster** ile indirilip cihazda çözülür. Yazar ve seçilmiş alıcı dışında site admini de içerik alamaz. Engel/ban erişimi keser; önceden yetkili olarak indirilen içeriği uzaktan geri çağırma garantisi yoktur. GIF/video/ses durumu ve izleyen listesi henüz eklenmedi.

**Ayarlar → Engellenenler** yalnız kendi engel listenizi gösterir. **Engeli kaldır** başarılı sunucu yanıtından sonra satırı kaldırır; hata halinde yerinde tekrar denenebilir. Alt sayfanın geri düğmesi Ayarlar'a döner. Sosyal bölmeler açık sohbeti ve gönderilmemiş taslağı korur. Sohbet kartını listenin başına sabitleme, alıntılı yanıt ve mesaj arama ayrı önerilerdir; bu sürümde eklenen, sohbet içindeki ortak mesaj sabitlemedir.

Mevcut MTK Lounge üyeleri kalır; yeni normal site hesapları gruba kendiliğinden alınmaz. Grup kartına sağ tık veya başlıktaki **… → Grup seçenekleri → Davet bağlantılarını yönet** kullanın. Açılış mevcut bağlantıları okur, yeni bağlantı üretmez. Oluşturulma/son kullanım zamanı ve aktif/süresi dolmuş durumları listelenir. Hazır süre veya **Özel süre → dakika/saat/gün** ile 5 dakika–365 gün seçin; **Yeni bağlantı** mevcut davetleri bozmaz. Bir satır seçip **Kopyala**, **Süreyi uygula** veya **Davet Linkini Sil** kullanın. Yeni süre işlem anından başlar, adres değişmez; süresi dolmuş davet de açık işlemle yenilenebilir. **Tüm Davet Linklerini Sil** ayrı hedef ve onay penceresi kullanır; hiçbir davet silme işlemi mevcut üyeleri çıkarmaz. Yenileme, silme ve kapatma işlemleri temaya uygun, dinlenme halinde de görünen dolgulu/çerçeveli butonlardır; riskli eylemlerin yazısı kırmızıdır. Bir grupta en fazla 50 kayıt saklanır, artık kullanılmayan kayıtlar silinebilir. Yalnız grubun üye yöneticisi veya site admini yönetebilir; Mod/User yetkili değildir.

**Grup yönetimi → üyeyi seç → Gruptan çıkar**, normal üyeyi veya Mod'u açık onayla çıkarır. Site admini diğer grup yöneticisini de çıkarabilir; bir grup yöneticisi başka grup yöneticisini, site adminini, botu veya kendini bu yolla çıkaramaz. Çıkarma hesabı ve diğer sohbetleri silmez; çıkarılan kişi yeni mesajları/üyeleri okuyamaz, aktif grup aramasından çıkarılır. Sonradan geçerli davetle yeniden katılabilir, fakat eski yerel yetki veya önceki mesaj geçmişi geri verilmez. Tekrar katılmasını engellemek istiyorsanız mevcut grup yasaklama işlemini kullanın.

Davet alan kişi bağlantıyı tarayıcıda açıp kendi `mtkaya.me` hesabıyla giriş yaptıktan sonra **Gruba katıl** ile onaylar. Alternatif olarak uygulamadaki **Yeni Sohbet → Davet bağlantısıyla katıl** kullanılır. Önizleme üyelik vermez; üye listesi, grup fotoğrafı ve mesajlar önizleme API'sinde bulunmaz. Yeni üyeler katılımdan önceki mesaj/dosya/anahtarları alamaz; sonradan gönderilecek mesajlar normal şifreleme yoluyla onlara da gönderilir. Var olan üyelerin geçmiş erişimi değişmez; banlar ve 50 kişilik grup sınırı uygulanır. Katılım sırasında mevcut metin/medya taslağı varsa sohbet listesi yenilenir fakat taslak başka gruba taşınmaz.

Davet adresi sabit HTTPS `mtkaya.me/chat/invite/` altında, sır yalnız `#` parçasındadır; katılım API'sinde JSON POST gövdesi kullanılır. Yeni bağlantılarda veritabanı SHA256 özetiyle birlikte ASP.NET Data Protection ile korunmuş değer saklar, açık adres/token saklamaz. Üretim anahtar halkası ayrı chat hesabına özel `/etc/mtk-chat/invite-keys` dizininde, sürüm klasörü dışında tutulur; `InviteProtection__KeyRingPath` ile özelleştirilebilir. Linux'ta anahtar dosyalarını ayrıca sertifikayla şifreleme uygulanmamıştır; erişim 0700 dizin ve servis hesabıyla korunur. Bu dizini chat veritabanı yedeğiyle birlikte güvenli biçimde yedekleyin. Anahtar kaybında adres tekrar gösterilemez; özetle katılım/süre/silme işlemi korunur. Önceki sürümün yalnız özet saklanan adresi geri üretilemez; URL olmadan yönetilir ve mevcut geçerliliği bozulmaz. Tarayıcı sayfası parola/oturumu kalıcı depolamaya yazmaz; başarılı katılım/hesap değişimi sonrasında yalnız kendi geçici oturumunu kapatır. Bağlantıyı bilen etkin site hesabı katılabilir; tek kullanımlık/kişiye bağlı davet veya yönetici onay kuyruğu henüz yoktur.

**Geçerlilik → Sınırsız**, yeni davet için **Yeni bağlantı** ile uygulanır; mevcut daveti seçip **Süreyi uygula** derseniz aynı adresin zaman sınırı kaldırılır. Sonradan aynı daveti yeniden süreli yapabilirsiniz. Süreli davetler hâlâ 5 dakika–365 gün aralığındadır; sınırsız davet kendiliğinden zaman aşımına uğramaz ve silinene kadar geçerlidir. Liste ve katılım önizlemesi tarih yerine **Sınırsız** gösterir. Yalnız açık bayrak bu davranışı verir; çok uzak tarih veya özel sürede sıfır yazmak sınırsız sayılmaz. Sunucu `NeverExpires` değerini kalıcı saklar; eski istemcilerin JSON okumasını bozmamak için `ExpiresAt` dolu kalır, yeni istemci bu uyumluluk tarihini ekrana basmaz. Yetki, ban, kapasite ve geçmiş mesaj erişimi kuralları aynıdır; bağlantıyı bilen etkin hesap katılabileceği için gerektiğinde silin.

Durum desteği ayrı VDS chat servisinde etkin; önceki profil/sınırsız davet özellikleri korunur. Bu güncellemede mevcut 2 sohbet, 26 mesaj, 4 cihaz ve 1 davet korundu; sohbet/üyelik, mesaj, cihaz, davet ve profil-adı özetleri önce/sonra eşleşti. Site/nginx dosyaları ve site kullanıcı tablosu değiştirilmedi; yeni tablo oluşturulmadı. Chat snapshot ve özel davet anahtar halkası yedeklenerek yalnız chat servisi yeniden başlatıldı. HTTPS sağlık ucu 200; oturumsuz durum/engel listesi 401/`no-store` verdi. Gerçek kullanıcı profili, durum, davet veya üyelik test amacıyla değiştirilmedi; yazma davranışları yerel HTTP/sentetik hesap testleriyle sınandı. Eski açık uygulamada yeniden giriş gerekebilir.

Sohbet kartında sağ üst tarih kaldırılmıştır; son mesajın yerel `HH:mm` saati alt satırda mesaj önizlemesiyle aynı hizadadır. Okunmamış rozet üst satırdadır. Son mesaj silinirse önceki görünür mesajın saati gösterilir; görünür mesaj kalmadığında veya önizleme yerel olarak süresi dolduğunda saat de temizlenir. Avatarın daire dışındaki köşeleri saydamdır ve hover/seçim sırasında kartın gerçek zeminini izler. `--verify-conversation-cards` kart yerleşimi/saat ve gerçek native avatar-hover regresyonlarını çalıştırır; `--verify-avatar-hover` yalnız çizim kısmını sınar. İkisi de canlı hesap kullanmaz; tam `--snapshot` içine de eklenmiştir.

Önceki sosyal özellik turunun Release build/publish sonucu 0 hata/0 uyarı; birim/yerel HTTP testleri 270/270 idi. `--verify-social` ve `--verify-status-publish` arşiv/sessiz/bildirim, engel listesi, açık alıcı seçimi, gerçek şifreli sahte POST, 503 sonrası birebir tekrar, bozuk başarılı yanıt ve uzun metinde gizli kaydırma çubuğuyla caret erişimini sınar; güncel tam `--snapshot` içinde de korunur. O turun ilk yayın QA'sı pencerenin ikinci kez imhasında kaynak hatası yakaladı; temizleme tek seferlik yapılıp tekrar koşusu geçti. Node VM web regresyonu 15 senaryo/192 denetim içerir. Yerel 120 DPI (%125), dar/geniş bölmeler incelenir; gerçek iki hesapla durum yayını, ikinci fiziksel PC, gerçek görev çubuğu animasyonu veya monitör geçişi denenmiş sayılmaz. Güncel doğrulama kayıtları `docs/DEGISIKLIK_GUNLUGU.md` ve `artifacts/current/validation` içindedir.

Sohbet listesinde son erişilebilir mesaj cihazda çözülerek tek satır gösterilir; boş sohbetlerde önizleme satırı boştur. Fotoğraf, ses ve dosya kısa tür/ad etiketleri kullanır. Önizlemeler yalnız hesap-bağlı bellektedir; sunucuya veya tercihlere açık metin yazılmaz, arka plan önizlemesi mesajı okundu yapmaz. İlk açılışta veya anahtar yüklenemediğinde satır çözülene kadar boş kalabilir. Eski son görülme tarihleri `01.10.2026 09:07` biçimindedir; bugün/dün korunur. Profil şeridinin native boyamasında enabled/idle geçişlerinden kalan avatar kenar pikselleri temizlenir.

Soldaki dar şeridin en altındaki **Profilim**, girişten itibaren kendi profil fotoğrafınızı/GIF'inizi gösterir; fotoğraf yoksa adınızın baş harfleri görünür. **Yönetim paneli** yalnız site admini hesaplarında, **Ayarlar** simgesinin üzerinde yer alır. Grup yöneticisi/mod yetkisi bu erişimi sağlamaz; panel açılırken sunucu yetkisi yeniden doğrulanır. Sohbet listesinin altındaki eski hesap kartı ve ayarlar/yönetim düğmeleri kaldırılmıştır.

Sol şeritte **Ayarlar** veya profil fotoğrafınıza basarak **Profilim** alanını açın; aynı düğmeye tekrar basmak veya **Escape** sohbet listesine döner. **Ctrl+K** paneli kapatıp aramaya odaklanır. Profil fotoğrafı/GIF değişiklikleri yalnız **Kaydet** ile yüklenir. Ayarlar → **Gizlilik** altında son görülme ve okundu paylaşımı ayrı kaydedilir; yüklenemeyen tercihler varsayılan değerlerle ezilmez. Panel açılırken sohbet, taslak ve kaydırma konumu korunur.

**Profilim → Kişisel bilgiler → Düzenle** kendi adınızı/soyadınızı gösterir ve iki DevExpress **Ad / Soyad** alanıyla düzenler. Okuma görünümünde **Ad ve Soyad:** etiketi ve gerçek ad ayrı satırlardadır. Avatar/hesap/fotoğraf işlemleri kimlik kartında, kişisel bilgiler ayrı karttadır; uzun başlık ve ad dar alanda satıra sarılır, Düzenle butonunun üzerine binmez. Bilgi yoksa değer satırı boştur; kullanıcı adından isim tahmin edilmez. Her iki alanı girip bu bölümün **Kaydet** düğmesine basın; **Vazgeç** yalnız ad taslağını bırakır. Ad kaydı fotoğraf/gizlilik kaydından ayrıdır. JPEG/GIF bilgilendirme metni kaldırıldı; gerçek dosya/GIF doğrulama sınırları korunur. Fotoğraf **Kaydet** yalnız gönderilmemiş fotoğraf işlemi varsa görünür. Kullanıcı adı, e-posta, site parolası ve roller değişmez; ad-soyad chat profil metaverisi olarak kalıcı saklanır, mesaj E2EE içeriği değildir. Arka plan profil yenilemesi açık isim taslağını ezmez.

Ayarlar içinden açılan **Profilim/Gizlilik → geri** düğmesi önce **Ayarlar** sayfasına döner. Gizlilik yüklemesi başarısızsa aynı bölümde **Yeniden dene** kullanılır; kayıt devam ederken yanlışlıkla sayfa değiştirilmez.

**Yeni grup** ekranı avatar, rol ve DevExpress SVG seçicilerinden oluşur; tüm satıra tıklama veya Space ile seçim yapılır, arama seçili kişileri kaybetmez. Grup kartına sağ tık veya başlıktaki **… → Grup seçenekleri → Grup adını değiştir**; aynı işlem grup yönetiminde **Adı değiştir** düğmesinden de açılır. Bu işlem yalnız grup yöneticisi/site adminine açıktır; mod yetkisi yeterli değildir. Ad değişikliği taslağı, üyeleri ve mesajları değiştirmez. Ad sunucuda kalıcı saklanır; site admini gruba üye değilse yine mesajlarını okuyamaz.

**Tümü / Okunmamış / Favoriler / Kişiler / Gruplar** filtreleri arama ile birlikte çalışır. Gerçekten okunmamış sohbet yoksa **Okunmamış sohbet yok — Hepsini gördünüz.** gösterilir; dönüş düğmesi bütün sohbetleri tekrar açar. Filtre seçmek mesajları kendiliğinden okundu saymaz.

Sohbeti sağ tıklayıp **Favorilere ekle / Favorilerden çıkar** seçin. Mesajı sağ tıklayıp **Yıldızla / Yıldızı kaldır** seçin; sohbet menüsündeki **Yıldızlı mesajlar**, o sohbetin erişilebilir geçmişindeki yıldızlı iletileri gösterir. **Sohbette göster** asıl mesaja kaydırır. Yıldızlar mesajı kopyalamaz ve silinen/süresi dolan iletileri geri getirmez.

Favoriler/yıldızlar/arşiv/sessiz tercihler bu bilgisayarda sohbet hesabına özel saklanır; başka cihazlara eşitlenmez. `%LOCALAPPDATA%/MTKChat/Preferences` içindeki DPAPI-korumalı dosya yalnız kimlikleri ve bildirim bitiş zamanlarını tutar, mesaj metni/fotoğraf/ses içermez. Bozuk/yazılamayan tercih dosyası ezilmez; kayıt hatası bildirilir. Mesaj şifrelemesi ve teslim/okunma API'si değiştirilmedi; durumlar ayrı kriptografik kapsamla eklendi, site dosyaları/ayarları korunmuştur.

Eski sürüm çıktıları, `.publish`, `bin/obj` ve işlevsiz eski `tools/MTKChat.Smoke` projeden kaldırıldı; geri alınabilmeleri için proje dışındaki `C:\Users\Y\AppData\Local\Temp\MTKChat-cleanup-20261002` ve `MTKChat-cleanup-20261002-ui-5d3c7567f94c4b61b1577e564199d727` geçici arşivlerinde tutulur. Önceki sürümlere ait aşağıdaki çıktı yolları tarihsel kayıttır, güncel çalıştırma hedefi değildir. Aktif kaynaklar, testler, dağıtım betikleri, görseller/ikonlar ve cihaz anahtarları korundu. Yeni çıktı temiz restore/Release derlemesiyle hazırlandı; kullanılan bağımlılıklar tahminen silinmedi.

Yeni yerel regresyon girişleri: `--verify-profile-names`, `--verify-invites`, `--verify-member-removal`, `--verify-previews`, `--verify-presence`, `--verify-groups`, `--verify-sidebar`, `--verify-send`, `--verify-filters`, `--verify-bookmarks`; tamamı `--snapshot` kapsamına da dahildir. `node tools/VerifyInviteWeb.mjs` web betiğini sahte DOM/fetch ile sınar; gerçek tarayıcı testi değildir. Bu makinedeki sentetik testler başka fiziksel PC'de DPI veya uzun süreli canlı kullanım testi yerine geçmez. Son doğrulama kayıtları ve sınırlar [değişiklik günlüğünde](docs/DEGISIKLIK_GUNLUGU.md).

Önceki sabitleme/arama paketinde birim/yerel HTTP testleri **339/339** idi; bu tarihsel sonuçtur. Güncel paketin sonucu yukarıda ve `artifacts/current/validation` altındadır; TRX `artifacts/current/tests`, ekran görüntüleri `artifacts/current/tests/snapshots` içindedir. Taşıma ZIP'i bunları içermez; yalnız gerekli runtime dosyaları, görseller/ikon ve SoundTouch lisansı paketlenir. Debug sembolleri taşıma paketine eklenmez; gerçek tam UI sonucu, dosya listesi/boyut/SHA-256 denetimi ve kalan sınırlar `validation/PACKAGE_VERIFICATION.md` içinde tutulur.

Önceki temizlik turlarında `bin/obj` önbellekleri dış kurtarma arşivine alındı. Bu sosyal özellik turunun kaynak/test derlemeleri normal önbellekleri yeniden oluşturdu; güncel runtime/ZIP bunlara ihtiyaç duymaz ve bunları içermez. Kaynaktan derlemek için `dotnet build MTKChat.slnx -c Release` veya `dotnet test MTKChat.slnx` kullanın; ilk koşuda `--no-restore` vermeyin.

Yeni profil/sınırsız davet paketi hazırlanırken önceki `artifacts/current` ve 10 bilinen derleme önbelleği kalıcı silinmeden `C:/Users/Y/AppData/Local/Temp/MTKChat-profile-unlimited-20261006-bb211462f94e44eeabda885510c862a7` altındaki `previous-current` / `final-build-caches` klasörlerine taşındı. Önceki buton/ad-soyad turunun kurtarma kökü `MTKChat-profile-actions-20261006-af9f2f81302c466a8bf48070d8ca0a49` olarak kalır. O tarihsel runtime/ZIP 75 dosya içeriyordu; test görselleri ve günlükler paket dışında tutulur.

Önceki arşiv/durum/engel listesi turunda eski paket `C:/Users/Y/AppData/Local/Temp/MTKChat-social-20261006-702ef8cf05fc4f0ab17b6e88e13a1383/previous-current` konumuna taşındı; o turdaki 6.2 sürümü 2602 UI çıktı satırı ve 278 PNG ile doğrulandı. Bu sayılar tarihsel kanıttır, güncel test adedi değildir. Güncel runtime/ZIP ve test kayıtları `artifacts/current` altındadır; bu turdaki gerçek koşu/hash/kurtarma sonuçları `validation/PACKAGE_VERIFICATION.md` içinde tutulur.

Filtre/kişisel bilgiler turunda güncel 6.3 EXE ile 1046 filtre, 114 profil denetimi ve tam UI regresyonu exit 0/boş stderr geçti; tam koşu 3114 çıktı satırı / 279 PNG üretti, bu sayılar test adedi değildir. 270 birim/yerel HTTP testi geçti. Yeni temiz win-x64 paketi 74 runtime dosyası içerir; ZIP ile tüm ad/boyut/SHA-256 değerleri eşleşti. Önceki current kalıcı silinmeden `C:/Users/Y/AppData/Local/Temp/MTKChat-filter-profile-20261006-acc5acb3908a4f61975c8aaccf48ce55/previous-current` konumuna taşındı. Kaynak/anahtar/veri ve sunucu değişmedi.

### Kararlılık ve başka bilgisayardaki ikon düzeltmesi (2 Ekim 2026)

Tarihsel sürüm (temizlik arşivine taşındı): `artifacts/desktop-stability-20261002/MTKChat.Desktop.exe`. Eski taşıma paketi `artifacts/MTKChat-stability-20261002.zip`, dosya sürümü `2026.10.2.0`, ürün sürümü `2026.10.02-stability` idi. Güncel çalıştırma/ZIP hedefi yukarıdaki `artifacts/current` bölümüdür. Paket framework-dependent'tır: diğer makinede .NET 9 Windows Desktop Runtime gerekir; DevExpress runtime dosyaları paketle gelir.

Emoji, ataş, arama, telefon, katılımcılar, seçenekler, kapatma ve gönder simgeleri fonttan bağımsız vektör oldu. Ses/süre etiketleri gerçek metin ölçümüne göre alan ayırır. Menü kapanışında uygulamayı düşüren doğrulanmış `ObjectDisposedException` giderildi. Yeni mesaj yalnız değişen satırı oluşturur; değişmeyen geçmiş/listeler yeniden ölçülmez. Uzun ilk yükleme arayüz kuyruğuna aralık verir. GIF callback'leri birleşir ve gizlenen pencerenin animasyonu durur. Gelen fotoğraflar tam boyutlu kopya yerine en uzun kenarı 1024 px yerel önizleme tutar; kaynak boyut sınırı uygulanır. Şifreli orijinal/veritabanı/protokol değişmedi.

Release ve tam `--snapshot` kümesi başarılıdır; birim testleri 86/86 geçti. Menü kapanışı, bekleyen callback ile GIF temizliği, 200 geçmiş yenilemesi, payload/imza değişimi, ağ toparlanması ve mevcut mesaj-bilgisi/duvar-kâğıdı testleri bu pakette çalıştı. Kayıtlar `artifacts/stability-full-final-output.log`, `artifacts/stability-full-final-error.log` (boş) ve `artifacts/stability-test-results/stability-tests.trx`. Diğer fiziksel PC'de DPI/uzun süreli canlı kullanım yapılmış sayılmaz.

Ek doğrulama girişleri: `--verify-icons`, `--verify-popup`, `--verify-performance`, `--verify-avatar`, `--verify-runtime`, `--verify-images`. Uygulama yine kapanırsa `%LOCALAPPDATA%/MTKChat/Diagnostics` içindeki `runtime-*.jsonl` dosyalarını paylaşabilirsiniz; istisna mesajı, sohbet metni veya API anahtarı içermez, yalnız sınırlı hata tipi/metot izi ve sürüm bilgisi tutar. Bütün değişiklik nedenleri ve test sınırları [değişiklik günlüğünde](docs/DEGISIKLIK_GUNLUGU.md).

### Mesaj bilgisi ve çizim düzeltmesi (1 Ekim 2026)

Tarihsel Windows çıktısı (temizlik arşivine taşındı): `artifacts/desktop-receipt-polish-20261001/MTKChat.Desktop.exe`. Bu işlev güncel pakette korunur: kendi gönderdiğin mesajı sağ tıklayıp **Mesaj bilgisi** seç; sağ panelde alıcı durumları görünür. X veya Escape kapatır; açık panel mevcut geçmiş yenilemesiyle güncellenir. Dar pencerede editörü sıkıştırmadan sağdan üstüne açılır; altında kalan mesajlar için okundu ACK'i panel kapanana kadar durur. Okundu bilgisini paylaşmayan kişi için okunmadı sonucu uydurulmaz. Sunucu/mesaj şifreleme protokolü değiştirilmedi.

Kaydırmada manzaranın şeritlenmesi native taşınmış piksellerin yeniden boyanmasıyla düzeltildi. Gönder ikonu font yerine vektör olarak çizilir; biriken dış kenar vurgusu kaldırıldı, klavye odak göstergesi korunur. Opak yuvarlak kart/editör kenarları da parent yüzeyine doğru boyanır. Resize sonrası eski layout genişliğinin açtığı beyaz yatay scrollbar, sohbet listesi ve bilgi panelinde doğru reflow ile giderilir. Yerel sentetik doğrulama girişleri: `--verify-message-info`, `--verify-wallpaper`, `--verify-buttons`, `--verify-panels`, `--verify-conversation-native`, `--snapshot`. Bunlar gerçek site hesabında test yapılmış olduğu anlamına gelmez.

### Referans görünümü

Sohbet ve giriş ekranları kullanıcının paylaştığı MTK Chat görselinin geniş sol liste, lacivert cam yüzey, mavi–mor balon ve dağ manzarası düzenine göre yeniden kurulmuştur. Arayüz gerçek C#/DevExpress kontrolleridir; bir ekran görüntüsü üstüne düğmeler yerleştirilmez. Üretilen arka plan `Assets/chat-mountains.png` olarak paketlenir; çalışma sırasında görsel üretim servisi veya internet gerekmez. Üretim açıklaması [görsel kaydında](docs/REFERENCE_DESIGN_ASSETS.md) bulunur. Başlık/mesaj/saat ayrı ölçülür; normal kullanıcı adları beyaz, bot/mod adları mor, yönetici adları sarı kalır. Programın MTK ikon dosyası korunur.

Sol listedeki **Tümü / Okunmamış / Favoriler / Kişiler / Gruplar** filtreleri aramayla birlikte çalışır; **Ctrl+K** arama alanına odaklanır. **Yeni Sohbet** düğmesi gerçek birebir sohbet/grup oluşturma ekranını açar. Okunmamış sayı ayrı yuvarlak rozettir; son mesaj önizlemesi cihazda doğrulanan/çözülen içerikten gelir, sunucunun şifreli saklama sınırı korunur. Başlıktaki katılımcı düğmesi geniş pencerede sağ paneli açıp kapatır; dar pencerede mevcut üye görüntüleme ekranını açar. Panel başlangıçta kapalıdır, mesaj alanı geniş kalır. Menü, üyelik ve yetki kuralları değiştirilmez.

Tek satırlı mesaj araç çubuğunda ataş **Görsel / Dosya** menüsünü, yüz simgesi emoji seçeneklerini açar. **Ses**, süre seçimi ve gönderme işlevleri korunur; görsel/dosya/ses taslağı varsa çubuk yukarı doğru genişleyerek önizleme gösterir. **Enter** gönderir, **Shift+Enter** yeni satır ekler, **Ctrl+V** panodaki görseli taslak olarak yapıştırır. Arşiv filtresi gerçek kişisel arşivi açar; işlevsiz video düğmesi eklenmemiştir. Sesli arama ve mevcut sohbet seçenekleri gerçek işlevlerine bağlıdır.

### Profil ve roller

Mesajların üzerinde ve katılımcı listesinde `kullanıcı adı · Admin`, `User` veya `Bot` görünür. İnsan rollerinin kaynağı site veritabanıdır; istemci rol atayamaz. Site rolü değişiklikleri periyodik eşitlemeyle görünür.

Sol alttaki hesap kartı veya profil şeridi **Profilim** panelini açar. JPEG/PNG seçimi önce yerel kare önizlemeye dönüşür; ancak **Kaydet** ile yüklenir. Fotoğrafı kaldırmak da kaydetme gerektirir. İstemci 8 MB/24 MP giriş dosyasını 512×512 JPEG'e küçültür; sunucu en fazla 2 MB/4 MP kabul eder ve yeniden kodlanmış, metadata içermeyen 256×256 JPEG saklar. Profil fotoğrafları şifreli özel mesaj değildir: giriş yapan kullanıcılara görünür ve aynı MySQL veritabanındaki ayrı `chat_profile_photos` tablosunda tutulur. Site kullanıcı tablosu değiştirilmez.

Profil API'si: `GET /api/profile`, `PUT/DELETE /api/profile/photo`, `GET /api/users/{id}/photo`. Tümü oturum gerektirir; değiştirme işlemi yalnızca oturum sahibine uygulanır. Yükleme/silme kişi başına dakikada 10 istek, yükleme aynı anda 2 işlemle sınırlandırılır. Fotoğraf küçük resimleri istemci belleğinde sürüm bazında tutulur; diske indirilmez.

Giriş ekranındaki **Şifremi unuttum** bağlantısı varsayılan tarayıcıda `https://mtkaya.me/loginregister.html` sayfasını açar. Kullanıcı bu sayfadaki mevcut sıfırlama akışını kullanır; masaüstü uygulaması parola sıfırlama e-postasını kendisi göndermez.

### Grup fotoğrafı ve sesli mesaj

Üstteki sohbet avatarına tıklamak **Grup fotoğrafı** ekranını açar. Seçilen fotoğraf kaydetmeden yüklenmez; kaldırma da kaydetme gerektirir. Sohbetin insan üyeleri fotoğrafı değiştirebilir; üye olmayanlar fotoğrafı okuyamaz veya değiştiremez. Grup fotoğrafı özel mesaj E2EE katmanında değildir: sunucuda profil fotoğrafıyla aynı boyut/normalizasyon sınırlarıyla, aynı site veritabanındaki yeni `chat_group_photos` tablosunda saklanır. Fotoğraf değiştirme kişi başına dakikada 10 istekle sınırlandırılır. Başlık ve sol sohbet kartındaki avatar sürüm değiştiğinde yenilenir; diğer açık istemciler yaklaşık 12 saniyede fark eder.

Mesaj araç satırındaki **Ses** düğmesi varsayılan Windows mikrofonuyla kaydı başlatır; tekrar basmak durdurur. 45 saniyede kayıt otomatik durur. Hazır ses taslağı **Dinle** ile kontrol edilebilir, **×** ile kaldırılabilir ve **Gönder** ile paylaşılır. Mikrofon erişiminin Windows gizlilik ayarlarında açık olması gerekir. Ses 16 kHz/16 bit/mono WAV olarak yalnızca bellekte hazırlanır; uygulama açık ses dosyasını diske yazmaz. Ses baytları metin/görselle aynı alıcıya özel şifreleme ve imza yolundan geçer, sunucu şifreli zarfları saklar. Gelen ses balonundaki düğme oynatma/durdurma içindir. Sohbet değişince veya pencere kapanınca kayıt/taslak temizlenir. Ses+metin iki ayrı mesajdır; botlar bu sürümde sesleri yazıya dökmez veya sese cevap üretmez.

Görsel regresyon için `MTKChat.Desktop.exe --snapshot` giriş, profil, grup fotoğrafı, sohbet (1120/1380/1920 genişlik), görsel/ses taslağı, ses mesajı balonu ve yönetim ekranlarının sentetik PNG'lerini exe yanındaki `snapshots` klasörüne üretir. Bu ekranların örnek hesapları canlı sunucuda oluşturulmaz; sentetik ses mikrofon kullanmadan hazırlanır.

`--snapshot` ayrıca emoji menüsünün gerçek editör değeri/yer tutucu davranışını, Ctrl+K odağını, katılımcı menüsü açıkken yenilemenin ertelenmesini ve kaydırma konumunun korunmasını denetler. `--verify-ui` daha kısa düğme/pencere çizim kontrolüdür: önceki kareyi silmeden aynı bitmap'i tekrar boyar, metin/hover/odak/durum geçişlerini temiz kareyle karşılaştırır. Bu uygulamaya ait test kontrolleri gerçek hesaba giriş yapmaz, mesaj göndermez; fiziksel fare/klavye veya farklı monitör DPI testi yerine geçmez.

Bağlantı kesilirse arka plan yenilemesi uyarı penceresi açmaz: başlıkta **Bağlantı yeniden deneniyor** görünür; mevcut mesajlar ve taslak korunur. Yenileme tek bir cycle olarak çalışır, hatada 3/6/12/24/30 saniyelik beklemeyle tekrar dener. Şifreleme etiketi bağlantı durumundan bağımsızdır. Geçici cihaz anahtarı sorgu hatası mesajı kalıcı “Mesaj açılamadı” durumuna kilitlemez; bağlantı dönünce aynı şifreli mesaj tekrar çözülür. 401 yanıtında otomatik yoklama durur ve **Yeniden giriş gerekli** görünür; uygulamayı yeniden açarak oturum açılabilir.

HTTP istekleri sınırsız beklemez: metadata 30 sn, normal komut 45 sn, geçmiş 90 sn, medya/mesaj yükleme 120 sn, arama 15 sn toplam bütçe kullanır; çağıranın daha kısa iptali korunur. Yalnız güvenli GET okumaları geçici bağlantı veya 502/503/504 hatasında en fazla bir ek deneme yapar; aynı toplam süre içinde kalır. Kayıt yapan POST/PUT/DELETE otomatik tekrarlanmaz. Gönderim sırasında bağlantı kesilmişse sunucu kabul etmiş olabilir: tekrar Gönder'e basmadan sohbeti kontrol edin. Kalıcı, mesaj kimliğini koruyan outbox henüz yoktur.

`--verify-network` sahte HTTP handler'ı ve gerçek şifreli mesaj/yerel editör yolu ile sessiz timer seçimi, backoff, tek polling cycle, anahtar sorgusu sonrası toparlanma ve 401 davranışını kontrol eder; canlı sunucuya bağlanmaz. Tam `--snapshot` bu kontrolleri de içerir.

Seçili sohbetin mesajları teslim-inbox kontrolünden önce yüklenir; ayrı bir inbox/ACK hatası sağlıklı sohbeti yükleniyor durumunda bırakmaz. İlk geçmiş yüklemesi başarısızsa **Mesajlar yüklenemedi → Yeniden dene** görünür. Daha önce yüklenmiş mesajlar sonraki GET hatasında korunur. Geçmiş GET'i ve gönderen anahtarları birlikte **60 saniyelik toplam yükleme sınırına** tabidir; bu, tek HTTP isteğinin daha uzun bütçesini de kısaltır. Başka sohbete geçiş eski yüklemeyi iptal eder; aynı kartı tekrar tıklamak sürmekte olan yüklemeyi bozmaz.

Bir gönderenin anahtarı geçici olarak alınamazsa yalnız ilgili mesajlar yeniden-denenecek satırı olur; diğer gönderenlerin mesajları açılır. Bu satırlar okundu sayılmaz ve başarılı render olarak önbelleğe alınmaz. Aynı gönderenin başarısız/yenilenen anahtarı tek render turunda tekrar tekrar sorgulanmaz. **401** oturum hatası bu yolla gizlenmez. Yükleniyor/boş/hata alanı da pencerenin ortak duvar kâğıdını kullanır; büyük opak bir panel oluşturmaz.

`--verify-history` sahte HTTP ve gerçek şifreli mesajlarla inbox hatası, ilk yükleme/retry düğmesi, gönderen bazında anahtar toparlanması, hızlı sohbet değişimi ve toplam yükleme süresini sınar. Tam `--snapshot` hem ağ hem geçmiş kontrollerini içerir; gerçek hesap veya uzaktan mesaj göndermez.

Masaüstü projesi `DevExpress.Win` 26.1.5 paketini kullanır. Makinedeki lisanslı DevExpress yerel NuGet kaynağı etkin olmalıdır; lisans anahtarı projeye eklenmez.

```powershell
dotnet run --project src/MTKChat.Server -- --environment Development
$env:MTK_CHAT_API_URL = "http://127.0.0.1:5088/"
dotnet run --project src/MTKChat.Desktop
```

Masaüstü istemcisi, adres değiştirilmezse doğrudan `https://mtkaya.me/chat/` kullanır. Yerel geliştirme sunucusu site hesabıyla giriş doğrular; çevrimdışı demo girişi kaldırılmıştır.

Testler:

```powershell
dotnet test MTKChat.slnx
```

### Yeni özel sohbet ve kapalı grup

Sağdaki kullanıcı kartına **sol veya sağ tıklayıp → Mesaj gönder** ile birebir sohbet açın. Aynı kişiyle tekrar açınca mevcut sohbet kullanılır; iki kişi aynı anda açarsa da tek sohbet oluşur. **Engelle / Engeli kaldır** menüsü kişisel engeli değiştirir; iki taraftan biri engellediyse birebir yeni mesaj gönderilemez. Ortak gruplarda engelleme üyeyi gruptan çıkarmaz; engellenen kişinin mesajları engelleyen kullanıcının görünümünde gizlenir.

Sol **Yeni Sohbet → Yeni sohbet** tüm insan site hesaplarında arama/seçim sunar. **Yeni Sohbet → Yeni grup** ekranında 1–80 karakterlik ad girip 1–49 kişi seçin: oluşturan kişi de dahil toplam 2–50 üye olur. Seçimler kullanıcı araması değişirken korunur. Bu arayüz botları gruba kendiliğinden eklemez. Yalnızca grup üyeleri sohbeti listeler, üyeleri/presence bilgisini ve mesajları okur; site admini olmak tek başına üyelik vermez. Sitede yeni hesap oluşturulması onu özel gruplara eklemez. Yeni sohbet/grup diğer üyelerin açık istemcilerinde yaklaşık 3 saniyelik liste yenilemesiyle belirir; bu yenileme seçili sohbeti ve taslağı değiştirmez.

Mesajlar mevcut imzalı, alıcıya özel E2EE zarfıyla gönderilir. Özel sohbet veya grupta bir üyenin cihaz anahtarı yoksa mesaj gönderilmez ve taslak korunur; o kişi uygulamaya bir kez giriş yaptıktan sonra gönderim tekrar denenebilir. Genel MTK Lounge'ın eski eksik-anahtar davranışı korunur. Profil/grup fotoğrafı ve sohbet adı/üyelik metadatası mesaj E2EE katmanında değildir. Oluşturma sonrasında üyeler davetle katılabilir; yetkili yönetici üye çıkarabilir ve üye kendisi gruptan ayrılabilir.

### Mesaj teslimi ve okunma

Gönderdiğiniz mesajlarda **tek gri tik** sunucu kabulünü, **çift gri tik** alıcının uygulamasına teslimi, **mavi çift tik** okunmayı gösterir. İnternetin açık olması tek başına teslim demek değildir: alıcı MTK Chat'e giriş yapmış ve uygulama çalışıyor olmalıdır. İnternet kesilirse bildirim yeniden denenir; durum yaklaşık 3 saniyede güncellenir.

Seçili olmayan sohbetler de şifreli zarfları indirip teslim bildirimi verir. Okundu bildirimi yalnızca ön plandaki, küçültülmemiş sohbet penceresinde içeriği görünür olan ve başarıyla çözülen mesajlar için gönderilir. “Mesaj açılamadı”, arka plan penceresi ve ekran dışındaki balonlar okundu sayılmaz. Sesli mesajın okunması, sesin dinlendiği anlamına gelmez; ayrı dinlendi bildirimi yoktur. Sohbet listesinde okunmamış mesaj sayısı gösterilir.

Gruplarda tüm **orijinal şifreli zarf alıcıları** teslim alınca çift tik, tamamı okuyunca mavi çift tik görünür; sonradan üye olanlar eski mesajın durumunu değiştirmez. Botun metni okuması, kendi zarfının agent işleyicisinde çözülmesidir; model sağlayıcısının yanıt üretmesi anlamına gelmez. Görsel/ses için bunları tüketmeyen botlar teslim/okunma toplamına katılmaz. Mesaja sağ tıklayıp **Mesaj bilgisi** ile alıcıların ilk teslim ve okunma zamanlarını görebilirsiniz.

Bildirimler mevcut `chat_state_snapshot` içinde kalıcıdır; eski mesajlarda bilinmeyen geçmiş teslim/okunma zamanları uydurulmaz. Yalnızca gönderen, diğer alıcıların bildirim listesini alır. Mesaj içeriğinin şifrelenmesi değişmez; teslim/okunma zamanları sunucunun gördüğü metadatadır. Bunlar istemci bildirimleridir, kişinin gerçekten okuduğunun kriptografik kanıtı değildir. Okundu paylaşımı profil ayarından kapatılabilir; çoklu cihaz ve telefon push bildirimi bu sürümde yoktur.

### Kişisel silme ve sohbet işlemleri

Başlıktaki Geçmiş menüsü kaldırıldı. Sol listedeki bir sohbet kartına sağ tıklayın:

- **Bu grup/sohbet mesajlarını sil** mevcut mesajları yalnızca sizden gizler. Üyelik ve diğer katılımcıların mesajları korunur; yeni mesajları almaya devam edersiniz. Site admini için de herkesten toplu temizleme yoktur.
- **Bu sohbeti sil** mevcut mesajları ve sohbeti kişisel listenizden gizler; gruptan çıkarmaz. Size adreslenmiş yeni bir mesaj gelirse sohbet tekrar görünür, eski mesajlar gizli kalır. Birebir sohbet Yeni sohbet üzerinden de tekrar açılabilir.
- **Gruptan çık** üyeliği, grup rolünü ve yeni mesaj erişimini sonlandırır. Lounge hesabı eşitlemesi sizi otomatik geri eklemez. Grupta başka insanlar kalacaksa son yönetici önce başka bir yönetici atamalıdır.
- **Grup üyelerini görüntüle / Sohbet bilgileri** salt okunur üye, rol ve durum listesini açar. Grup fotoğrafı ve yetkili kullanıcılara grup yönetimi aynı menüden açılır.

Mesaja sağ tıklayıp **Benden sil** her zaman kullanılabilir. **Herkesten sil** yalnızca kendi mesajınızda, sunucunun mesajı kabulünden sonraki ilk 15 dakika boyunca kullanılabilir; site admini başkasının mesajını bu yolla silemez. Süre sunucuda doğrulanır, istemcinin gönderim tarihi süreyi uzatmaz. Önceki sürümden kalan ve güvenilir kabul zamanı bulunmayan mesajlara güncelleme sırasında yeni 15 dakika verilmez. Kişisel gizleme kalıcıdır fakat başka cihazlara kaydedilmiş kopyaları geri alamaz. Silme eylemleri mesajın E2EE şifreleme biçimini değiştirmez; görünürlük/üyelik metadata'sını sunucuda yönetir.

### Sesli mesaj hızı ve pencere

Ses balonundaki **1×** düğmesi **1.5× → 2× → 1×** arasında geçer; hız değişirken ses tonu korunur. Oynatma düğmesi duraklatır ve kaldığı yerden devam eder; başka bir sesi başlatmak önceki oynatmayı kapatır. İşlem cihazda yapılır. SoundTouch.Net lisans bilgisi `docs/THIRD_PARTY_NOTICES.md` ve derleme yanındaki `Licenses/SoundTouch-LGPL.txt` dosyasındadır.

Sohbet, giriş, profil, grup fotoğrafı, yönetim ve arama pencereleri ortak koyu başlık kullanır. Pencereyi başlıktan sürükleyebilir; büyütülebilen pencerelerde çift tıklayarak büyütüp geri alabilirsiniz. Mesaja sağ tıklayıp metni kopyalayabilirsiniz.

Katılımcı/grup rolü, mesaj, sohbet işlemleri, süre, yeni sohbet/grup ve ekleme menüleri aynı koyu popup temasını kullanır. Aktif süre ve grup rolü tikle belirtilir; silme/engelleme gibi eylemler kırmızıdır. DevExpress yazı alanlarının düzenleme menüleri ve grup seçicinin açılır listesi de koyu temaya bağlıdır. Windows'un dosya seçme ve sistem onay pencereleri işletim sisteminin kendi görünümünü kullanır.

### Yönetim erişimi

Yönetim paneli yalnızca sitenin admin rolüne sahip insan hesaplarında görünür. Sunucu **her `/api/admin/*` isteğinde** site hesaplarını veritabanından tekrar okuyup oturum ve rol denetler; düğmeyi gizlemek tek güvenlik kontrolü değildir. Açık panel de yaklaşık 3 saniyede yetkisini kontrol eder ve yetki alınırsa kapanır. Veritabanı okunamazsa admin işlemi onaylanmaz. Normal kullanıcılar katılımcıya sağ tıklayıp kişisel engelleme/engeli kaldırma işlemini yapabilir; yönetim paneline ihtiyaçları yoktur.

### Birebir ve grup sesli arama

Sohbet başlığındaki telefon düğmesiyle **1–5 insan katılımcı** seçin: siz dahil en çok **6 kişi** konuşabilir. Katılımcıya sağ tıklayıp **Sesli ara** ile birebir arama da açılabilir. Alıcı uygulamada oturum açmış olmalıdır; davet yaklaşık 3 saniyelik kontrolle görünür. **Kabul et** mikrofonu açar; **Reddet/Kapat** aramadan çıkarır. Mikrofon düğmesi kendi sesinizi kapatır/açar. Cihaz kaydı olmayan hesaplar, botlar, engellenmiş/yasaklı/susturulmuş kullanıcılar aramaya alınmaz. Çevrimdışı masaüstüne push bildirim yoktur.

Arama kabulünde cihaz yeni geçici P-256 ECDH anahtarı üretir; cihazın kayıtlı ECDSA anahtarıyla imzalar. İki uç HKDF-SHA256 ile yön ve katılımcı oturumuna özel AES-256-GCM anahtarları türetir. Paket kimlikleri/sıra numarası doğrulamaya bağlıdır; değiştirilmiş veya tekrarlanan paketler oynatılmaz. Ses, alıcı başına cihazda şifrelenir; sunucu yalnızca davet/kimlik/zaman metadata'sını ve sınırlı geçici şifreli paket kuyruğunu görür. Arama sesi veritabanına veya agentlara gönderilmez. Katılımcı kartından açılan **güvenlik kodunu karşı tarafla ayrı, güvenilir bir kanalda karşılaştırın**: ilk kayıtlı kimlik anahtarlarının dağıtımı sunucuya dayanır. Bu özel protokol bağımsız denetimden geçmemiştir; WhatsApp/Signal güvenliğine eşdeğer olduğu iddia edilmez. Bir katılımcının kendi cihazında sesi kaydetmesini E2EE önlemez.

Bu ilk sürüm mevcut HTTPS API üzerinden kısa isteklerle şifreli **16 kHz mono PCM** taşır; ilave UDP portu veya nginx değişikliği gerektirmez. Sıkıştırma, yankı giderme, video, ekran paylaşımı ve otomatik yeniden bağlanma yoktur. Kulaklık önerilir; grup büyüdükçe alıcı başına veri miktarı artar. Bağlantısı 20 saniye etkinlik göstermeyen katılımcı çıkarılır; yanıtlanmayan arama 60 saniyede sona erer. Aramalar bellektedir ve servis yeniden başlayınca biter. Gerçek iki bilgisayar/mikrofon ve yavaş bağlantı kalitesi ayrıca denenmelidir; otomatik testler sentetik PCM ve izole HTTP üzerinden çalışır.

## Gizlilik, dosyalar ve grup rolleri

Sol alttaki kendi profil kartınıza tıklayın. **Son görülmemi göster** ve **Okundu bilgisi gönder** seçeneklerini ayrı ayrı kapatabilirsiniz; tercihler sunucuda hesabınıza kaydedilir. Son görülmesini gizleyen kişinin çevrimiçi durumu gizlenmez. Hiç chat'e giriş yapmamış kişiye tarih uydurulmaz. Okundu paylaşımı kapalıyken kendi okunmamış sayınız temizlenir, karşı tarafa yalnızca teslim bilgisi gider. Daha sonra paylaşımı açmak o sırada özel okunan mesajlara geriye dönük mavi tik göndermez. Bu ayarlar hem birebir hem grup sohbetlerinde geçerlidir. Bir grup üyesi okundu bilgisini paylaşmıyorsa bütün alıcıların okuduğunu gösteren mavi çift tik oluşmaz.

**＋ Ekle → Dosya** ile 1 bayt–5 MB dosya seçin; taslakta adını görüp kaldırabilir veya gönderebilirsiniz. Alıcı dosya balonuna tıklayıp kaydetme konumunu seçer; dosyalar otomatik açılmaz veya çalıştırılmaz. Dosya AES-256-GCM ile cihazda şifrelenir. Sunucu `chat_encrypted_files` tablosunda yalnızca şifreli baytları tutar. Dosya adı, anahtar, nonce ve tag alıcıya özel imzalı/şifreli mesaj zarfında taşınır; dış metadata yalnızca token, boyut ve sohbet/gönderen kimliklerini içerir. Dosya mesajları botlara iletilmez. Birebir veya özel gruptaki tüm insan alıcıların önceden cihaz anahtarı kaydetmesi gerekir. Herkesten silme/süre dolumu erişimi kaldırır ve ilgili şifreli baytları siler. Kişisel temizleme dosyayı yalnızca sizden gizler, diğer alıcıların şifreli kopyası korunur. Alıcının daha önce cihazına kaydettiği kopya geri alınamaz. Teslim/okundu tiki dosya mesajının teslimini/okunmasını belirtir, dosyanın indirilip kaydedildiğini değil.

Profil fotoğrafında JPEG/PNG yanında **GIF** seçilebilir: en fazla **2 MB, 256×256 piksel ve 120 kare**. Daha büyük GIF'i önce küçültün. Animasyon korunur; gereksiz GIF metadata'sı ayıklanır. Profil ve grup fotoğrafları E2EE mesaj eki değildir; kimliği doğrulanmış kullanıcıların görebildiği sunucu profil verisidir. Son görülme ve teslim/okundu bildirimleri de mesaj içeriğinden ayrı, sunucunun bildiği metadata'dır.

Sol sohbet listesindeki grup kartına sağ tıklayıp **Grup yönetimi** seçerek grup rolleri ve gruba özel susturma/yasak işlemleri yapılır. Yetkili kullanıcı katılımcıya sağ tıklayıp rolünü de değiştirebilir:

- **Grup yöneticisi**: sarı; grupta yönetici/mod/üye atayabilir, mod ve normal üyeleri susturabilir/yasaklayabilir. Kendini ve site adminini değiştiremez.
- **Mod**: mor; sadece normal üyeleri susturabilir/yasaklayabilir, rol atayamaz.
- **User**: beyaz; yönetim işlemi yapamaz. Çevrimiçi durum noktası yeşil kalır.

Grubu oluşturan kişi o grubun yöneticisidir. Grup yetkileri `users.role` alanını değiştirmez ve **genel Yönetim panelini açmaz**. Sitede admin olan hesap tüm grupları yönetebilir; diğer grup yöneticilerini de susturabilir. Grubun üyesi değilse bu yetki onu gruba eklemez, özel mesajları veya şifre çözme anahtarlarını vermez. Site admininin rolü grup menüsünden alınamaz; genel site rolü ayrı yönetilir. Botlar insan grup rolleri almaz.

## Üretim yapılandırması

Hiçbir parola kaynak koduna veya `appsettings.json` içine yazılmamalıdır. VDS'de ayrı `mtk-chat.service`, mevcut sitenin DB ortam değişkenlerini yalnızca okur; agent API anahtarları `/etc/mtk-chat/agent-secrets.json` içinde, bot özel anahtarları `/etc/mtk-chat/agent-keys.json` içinde tutulur. Bu dosyalar kaynak depoya eklenmez. Servis ve HTTPS yönlendirme örnekleri `deploy/` altındadır.

```text
Database__Provider=MySQL
DB_HOST/DB_PORT/DB_USER/DB_PASS/DB_NAME=<sitenin mevcut ortam değişkenleri>
Agents__KeyStorePath=/etc/mtk-chat/agent-keys.json
```

Yerel geliştirmede anahtarlar `.NET User Secrets` içinde tutulur. Kaynak kodunda veya appsettings dosyalarında anahtar bulunmaz. Normal bir mesaj Gemini ve Groq'tan birer bağımsız yanıt alır; agentlar kendiliğinden konuşmayı sürdürmez. Agentlar arası kontrollü konuşmayı başlatmak, durdurmak veya tek bir agentı hedeflemek için şu komutlar kullanılabilir:

```text
@gemini uzun bir teknik açıklama hazırla
@groq bu tasarımı eleştir
@agents iki ayrı öneri üretin
/roundtable bu mimarinin artılarını ve eksilerini tartışın
birbirinizle sohbet edin ve güvenli mesajlaşmayı tartışın
groqla sohbet etsenize
geminiyle sohbet edin
Gemini dur
Groq dur
ikiniz de durun
Gemini sadece sen konuş
Gemini sus
Groq devam et
ikiniz de konuşun
```

“Gemini/Groq sadece sen konuş” seçilen agentı etkin bırakır. “Gemini/Groq sus” yalnızca adı geçen agentı kapatır; “ikiniz de durun” ikisini de kapatır. “İkiniz de konuşun” normal çift-agent modunu yeniden açar. Bu tercihler sohbet bazında tutulur.

“Groq'la sohbet et” Gemini'yi, “Gemini'yle sohbet et” Groq'u ilk konuşmacı yapar. Uygulama yanıtı diğer agente sonraki turda iletir; bu, model ağırlıklarının eğitilmesi değil sohbet orkestrasyonudur. Mesaj yazma alanında Ctrl+V ile panodaki görsel veya kopyalanmış görsel dosyası taslağa eklenir. Görsel, “Gönder” tıklanana veya Enter'a basılana kadar karşı tarafa gitmez; × ile kaldırılabilir. Metinle beraber gönderilirse görsel ve metin ayrı şifreli mesajlar olarak iletilir.

Admin rolü doğrudan sitenin `users.role` alanından eşitlenir. Yönetim panelinden rol verme/alma da aynı site kaydına yazılır; chat kendi `chat_state_snapshot`, `chat_profile_photos`, `chat_group_photos` ve `chat_encrypted_files` tablolarını oluşturur ve mevcut `users` kayıtlarını silmez. Site hesabı pasif hale gelince chat listesinden ve üyeliklerden çıkarılır. Sohbetler, şifreli mesajlar, kayıtlı açık cihaz anahtarları ve moderasyon durumu aynı MySQL veritabanındaki chat tablolarında kalıcıdır. Kullanıcı parolaları ve parola hash'leri chat uygulamasına aktarılmaz; giriş sitenin HTTPS API'si üzerinden yapılır.

Sağdaki “Kişiler” alanı 1360 piksel ve daha geniş pencerelerde görünür. Son 45 saniyede API etkinliği olan kullanıcı “çevrimiçi”, o sohbet ekranından son 45 saniyede etkinlik bildiren kullanıcı “şu an bu sohbette” olarak gösterilir. Liste site veritabanındaki bütün etkin hesapları, henüz chat'e hiç giriş yapmamış olsalar bile içerir. Sohbete özel yasak kullanıcıyı yalnızca o sohbette mesaj yazmaktan alıkoyar; tüm uygulamadan banlama ayrı işlemdir.

Masaüstü uygulamasının üretim API adresi `https://mtkaya.me/chat/` adresidir. Giriş parolası mesaj E2EE katmanından önce taşındığı için çıplak IP/HTTP kullanılmamalıdır.

## Önemli güvenlik sınırı

Bu prototip gerçek şifreleme uygular ancak henüz Signal Double Ratchet kullanmaz. Dolayısıyla mevcut `v1` zarfı ileri gizlilik ve oturum-sonrası güvenlik bakımından WhatsApp/Signal ile eşdeğer değildir. Üretime çıkmadan önce denetlenmiş bir Signal protokolü uygulaması, anahtar değişimi doğrulaması, çoklu cihaz pre-key akışı ve bağımsız güvenlik denetimi gerekir.

Agentlar yalnızca katılımcı oldukları sohbetin kendilerine ayrılmış zarfını çözebilir. Agent bulunan sohbete gönderilen plaintext ilgili model sağlayıcısına gönderilir. Agentlar ekli değilken sunucu veya model sağlayıcısı mesaj metnini göremez.

## Mevcut sınırlar

Sohbet verisi şu an tek `chat_state_snapshot` satırında tutulur. Küçük kullanım için yeniden başlatma sonrası kalıcılık sağlar; büyük mesaj/görsel geçmişinde tam tablo normalizasyonu, yedekleme ve sayfalama gerekir. Her değişiklikte anlık görüntü tekrar yazılır. Giriş oturumları ve çevrimiçi bilgisi bellektedir; servis yeniden başlatıldığında yeniden giriş gerekir. Tek cihaz modeli devam eder. Bu dağıtımın gerçek site hesabıyla giriş ve iki ayrı cihaz arasında uçtan uca mesajlaşma akışı ayrıca test edilmelidir.
