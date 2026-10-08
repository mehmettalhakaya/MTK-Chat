# Üçüncü Taraf Bildirimleri

## SoundTouch.Net 2.3.2

Olaf Woudenberg (2011–2019), LGPL-2.1-or-later. Kütüphane ayrı ve değiştirilmemiş DLL olarak kullanılır. Lisans metni Windows dağıtımında `Licenses/SoundTouch-LGPL.txt` içindedir.

- İlgili kaynak: https://github.com/owoudenberg/soundtouch.net/tree/2.3.2
- Paket kaynak commit'i: `98e5b8fd2f8efed0ddf7c8f66b435bfb231659dc`

Kütüphanenin değiştirilmiş uyumlu sürümünü bağlama ve bu değişiklikleri hata ayıklamak amacıyla tersine mühendislik yapma hakları saklıdır. Uygulama kaynak projesi ve NuGet sürüm referansı yeniden bağlamaya olanak verir; bağımlılık yerine geçecek uyumlu DLL kullanılabilir.

## NAudio 2.4.0

Mark Heath, MIT. Mikrofon erişimi ve PCM kayıt/oynatma için kullanılır. Kaynak: https://github.com/naudio/NAudio/tree/7c855e6737435f781dcbac782930a0de7a13cb2b . Tam lisans metni `Licenses/NAudio-MIT.txt` içindedir ve Windows Release ZIP'ine eklenir.

## Microsoft açık kaynak çalışma zamanı bağımlılıkları

`Microsoft.Extensions.ObjectPool` 8.0.10, `System.Data.OleDb` 8.0.1 ve `System.ServiceModel.Http/Primitives` 8.1.2 MIT lisanslıdır. Dağıtılan WCF uyumluluk DLL'leri de bu istemci paketlerinden gelir. Ortak .NET lisans bildirimi `Licenses/Microsoft-MIT.txt` içindedir. Kaynaklar: https://github.com/dotnet/runtime , https://github.com/dotnet/aspnetcore , https://github.com/dotnet/wcf .

## Microsoft Windows SDK / WinRT

`Microsoft.Windows.SDK.NET.dll` ve `WinRT.Runtime.dll`, `Microsoft.Windows.SDK.NET.Ref` 10.0.17763.57 paketinden gelir. Paket Windows SDK lisansına başvurur: https://aka.ms/WinSDKLicenseURL . Bu bileşenler yukarıdaki ortak MIT bildiriminin kapsamına topluca dahil edilmez. .NET Windows Desktop Runtime framework-dependent ZIP'e dahil değildir; hedef bilgisayarda ayrıca bulunmalıdır.

## DevExpress WinForms 26.1.5

DevExpress bileşenleri ticari lisansa tabidir. Windows paketi uygulamanın çalışması için gereken runtime DLL'lerini içerir; tasarım araçları, NuGet feed erişimi veya geliştirme lisansı sağlamaz. Kaynak koddan DevExpress ile geliştirme için kendi geçerli lisansınız gerekir. Lisans koşulları: https://www.devexpress.com/support/eulas/winforms-controls.xml .
