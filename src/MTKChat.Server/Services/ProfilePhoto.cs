using System.Security.Cryptography;
using SkiaSharp;

namespace MTKChat.Server.Services;

public sealed record ProfilePhoto(string Version, byte[] Jpeg)
{
    // Existing MySQL column names remain compatible; the encoded bytes can now be GIF.
    public string ContentType => Jpeg.AsSpan().StartsWith("GIF8"u8) ? "image/gif" : "image/jpeg";
}

public static class ProfilePhotoCodec
{
    public const int MaxUploadBytes = 2 * 1024 * 1024;

    public static ProfilePhoto Normalize(byte[] bytes)
    {
        if (bytes.Length is 0 or > MaxUploadBytes) throw new InvalidDataException("Fotoğraf en fazla 2 MB olabilir.");
        using var data = SKData.CreateCopy(bytes);
        using var codec = SKCodec.Create(data);
        if (codec is null || codec.EncodedFormat is not (SKEncodedImageFormat.Jpeg or SKEncodedImageFormat.Png or SKEncodedImageFormat.Gif))
            throw new InvalidDataException("Geçerli bir JPEG, PNG veya GIF fotoğraf seçin.");
        if (codec.Info.Width <= 0 || codec.Info.Height <= 0 ||
            (long)codec.Info.Width * codec.Info.Height > 4_000_000)
            throw new InvalidDataException("Fotoğraf çözünürlüğü en fazla 4 milyon piksel olabilir.");
        if (codec.EncodedFormat == SKEncodedImageFormat.Gif)
        {
            var gif = GifProfileCodec.Normalize(bytes, codec);
            return new(Convert.ToHexString(SHA256.HashData(gif)).ToLowerInvariant(), gif);
        }
        using var source = new SKBitmap(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        if (codec.GetPixels(source.Info, source.GetPixels()) != SKCodecResult.Success)
            throw new InvalidDataException("Fotoğraf dosyası bozuk veya eksik.");
        using var target = new SKBitmap(256, 256, SKColorType.Rgba8888, SKAlphaType.Opaque);
        using var canvas = new SKCanvas(target);
        canvas.Clear(SKColors.White);
        var edge = Math.Min(source.Width, source.Height);
        var crop = SKRect.Create((source.Width - edge) / 2f, (source.Height - edge) / 2f, edge, edge);
        canvas.DrawBitmap(source, crop, new SKRect(0, 0, 256, 256));
        using var image = SKImage.FromBitmap(target);
        using var encoded = image.Encode(SKEncodedImageFormat.Jpeg, 88);
        // Re-encoding strips original metadata and any appended payload, not just the file extension.
        var jpeg = encoded.ToArray();
        return new(Convert.ToHexString(SHA256.HashData(jpeg)).ToLowerInvariant(), jpeg);
    }
}
