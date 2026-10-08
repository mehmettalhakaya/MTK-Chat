using SkiaSharp;

namespace MTKChat.Server.Services;

internal static class GifProfileCodec
{
    internal static byte[] Normalize(byte[] bytes, SKCodec codec)
    {
        if (codec.Info.Width > 256 || codec.Info.Height > 256 || codec.FrameCount is < 1 or > 120 ||
            (long)codec.Info.Width * codec.Info.Height * codec.FrameCount > 8_388_608)
            throw new InvalidDataException("Hareketli GIF en fazla 256×256 piksel ve 120 kare olabilir.");
        using var decoded = new SKBitmap(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        for (var i = 0; i < codec.FrameCount; i++)
            if (codec.GetPixels(decoded.Info, decoded.GetPixels(), new SKCodecOptions(i, i - 1)) != SKCodecResult.Success)
                throw new InvalidDataException("GIF'in bir karesi bozuk veya eksik.");

        // Preserve animation/LZW data, but discard comments, unknown applications and bytes
        // appended after the GIF trailer. Only graphic controls and the standard loop survive.
        var offset = 13;
        if (bytes.Length < offset) throw new InvalidDataException("GIF başlığı eksik.");
        if ((bytes[10] & 0x80) != 0) offset += 3 * (1 << ((bytes[10] & 7) + 1));
        if (offset > bytes.Length) throw new InvalidDataException("GIF renk tablosu eksik.");
        using var clean = new MemoryStream();
        clean.Write(bytes, 0, offset);
        while (offset < bytes.Length)
        {
            var start = offset;
            var block = bytes[offset++];
            if (block == 0x3b) { clean.WriteByte(block); return clean.ToArray(); }
            if (block == 0x2c)
            {
                if (offset + 9 >= bytes.Length) throw new InvalidDataException("GIF karesi eksik.");
                var packed = bytes[offset + 8];
                offset += 9;
                if ((packed & 0x80) != 0) offset += 3 * (1 << ((packed & 7) + 1));
                if (offset >= bytes.Length) throw new InvalidDataException("GIF karesi eksik.");
                offset++; // LZW minimum code size; SKCodec already validated the compressed pixels.
                SkipSubBlocks(bytes, ref offset);
                clean.Write(bytes, start, offset - start);
            }
            else if (block == 0x21)
            {
                if (offset >= bytes.Length) throw new InvalidDataException("GIF uzantısı eksik.");
                var tag = bytes[offset++];
                SkipSubBlocks(bytes, ref offset);
                var graphicControl = tag == 0xf9 && offset - start == 8 && bytes[start + 2] == 4;
                var standardLoop = tag == 0xff && offset - start == 19 && bytes[start + 2] == 11 &&
                    System.Text.Encoding.ASCII.GetString(bytes, start + 3, 11) == "NETSCAPE2.0" &&
                    bytes[start + 14] == 3 && bytes[start + 15] == 1;
                if (graphicControl || standardLoop) clean.Write(bytes, start, offset - start);
            }
            else throw new InvalidDataException("GIF blok yapısı geçersiz.");
        }
        throw new InvalidDataException("GIF bitişi eksik.");
    }

    private static void SkipSubBlocks(byte[] bytes, ref int offset)
    {
        while (offset < bytes.Length)
        {
            var length = bytes[offset++];
            if (length == 0) return;
            if (offset + length > bytes.Length) break;
            offset += length;
        }
        throw new InvalidDataException("GIF verisi eksik.");
    }
}
