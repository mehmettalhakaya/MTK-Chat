# Referans tasarımının görsel kaydı — 2026-10-01

## Kullanım ve kaydedilen dosya

- Araç: Yerleşik `image_gen.imagegen`; API/CLI alternatifi kullanılmadı.
- İşlem: Yeni bitmap üretimi; mevcut görsel düzenleme işlemi değil. Şeffaf arka plan: false. Referans dosya / son konuşma görseli gönderilmedi.
- Kaydedilen uygulama varlığı (depo köküne göre): `src/MTKChat.Desktop/Assets/chat-mountains.png`.
- Referans: Kullanıcının paylaştığı MTK Chat ekranından dağ manzaralı, lacivert–mor atmosfer yönü alındı. Gerçek düğmeler, yazılar, mesajlar, avatarlar ve pencereler C# kodudur; üretilen resimde kullanıcı arayüzü yoktur.
- Sonradan uygulanan kod-native stil: `ChatWallpaper.cs` pencereye sabit, cover ölçekli manzaraya yarı saydam lacivert perde uygular; pencere boyutuna göre boyanan tuvali tekrar kullanır. Resim dosyası bu işlemle değiştirilmez. Dosya eksikse koyu geçiş arka planına dönülür.
- Sınır: Bu görsel mesaj veya profil fotoğrafı değildir; herkese aynı yerel uygulama varlığı gösterilir. Canlı kullanıcı bilgisi ve API anahtarı görsel üretim isteğine gönderilmedi.

## Üretime gönderilen tam istem

```text
Use case: stylized-concept. Asset type: actual background texture for a C# desktop chat application, not an interface mockup. Primary request: cinematic dark indigo alpine valley at twilight, layered mountain silhouettes with soft forested foothills, faint violet glow deep in the central valley, large quiet almost-black navy sky across the upper half. Wide landscape 16:9 composition, subdued low contrast so white text and blue-violet chat bubbles can sit clearly on top. The valley slopes descend from both sides toward the center; nearest silhouettes almost black. Restrained navy, deep cobalt and muted violet color palette, elegant realistic matte digital landscape. It should look like a subtle mountain wallpaper behind a premium blue/purple desktop messenger. Constraints: only the landscape, no user interface, no window frame, no buttons, no chat messages, no writing, no letters, no logo, no watermark, no people, no bright moon, no distracting highlights. Generate a brand new usable wallpaper image.
```

## Önizleme verisi

`--snapshot` içindeki Zeynep, Ahmet ve diğer örnek adlar/okunmamış sayılar yalnızca arayüz doğrulama verisidir. Gerçek site hesabı veya sohbet oluşturmaz. Canlı uygulamada mevcut site kullanıcıları, gerçek profil/grup fotoğrafları, mesajlar ve teslim durumları kullanılır.
