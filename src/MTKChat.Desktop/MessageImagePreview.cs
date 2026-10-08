using System.Buffers.Binary;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace MTKChat.Desktop;

internal static class MessageImagePreview
{
    internal const int MaximumEncodedBytes = 5 * 1024 * 1024;
    internal const long MaximumSourcePixels = 40_000_000;
    internal const int MaximumSourceEdge = 16_384;
    internal const int MaximumPreviewEdge = 1024;

    internal static Bitmap Decode(byte[] encoded)
    {
        if (encoded.Length is 0 or > MaximumEncodedBytes)
            throw new InvalidDataException("Görsel boyutu desteklenmiyor.");
        // PNG/GIF dimensions are available without asking a native decoder to
        // allocate pixels. Reject obviously oversized headers before GDI+.
        var bytes = encoded.AsSpan();
        if (bytes.Length >= 24 && bytes[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) &&
            bytes.Slice(12, 4).SequenceEqual("IHDR"u8))
            ValidateDimensions(BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(16, 4)),
                BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(20, 4)));
        else if (bytes.Length >= 10 && (bytes[..6].SequenceEqual("GIF87a"u8) || bytes[..6].SequenceEqual("GIF89a"u8)))
            ValidateDimensions(BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(6, 2)),
                BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(8, 2)));

        using var stream = new MemoryStream(encoded, writable: false);
        using var source = Image.FromStream(stream, useEmbeddedColorManagement: false, validateImageData: false);
        ValidateDimensions(source.Width, source.Height);
        var scale = Math.Min(1d, MaximumPreviewEdge / (double)Math.Max(source.Width, source.Height));
        var width = Math.Max(1, (int)Math.Round(source.Width * scale));
        var height = Math.Max(1, (int)Math.Round(source.Height * scale));
        var preview = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
        try
        {
            using var graphics = Graphics.FromImage(preview);
            graphics.CompositingMode = CompositingMode.SourceCopy;
            graphics.InterpolationMode = InterpolationMode.HighQualityBilinear;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.DrawImage(source, new Rectangle(0, 0, width, height),
                new Rectangle(0, 0, source.Width, source.Height), GraphicsUnit.Pixel);
            return preview;
        }
        catch { preview.Dispose(); throw; }
    }

    internal static void ValidateDimensions(long width, long height)
    {
        if (width < 1 || height < 1 || width > MaximumSourceEdge || height > MaximumSourceEdge ||
            width * height > MaximumSourcePixels)
            throw new InvalidDataException("Görsel çözünürlüğü desteklenen sınırın üzerinde.");
    }
}
