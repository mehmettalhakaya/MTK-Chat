using System.Buffers.Binary;
using System.Drawing.Imaging;
using MTKChat.Contracts;

namespace MTKChat.Desktop;

internal static class MessageImagePreviewQA
{
    internal static IReadOnlyList<string> Verify()
    {
        var checks = new List<string>();
        void Require(bool value, string name)
        {
            if (!value) throw new InvalidOperationException("Message image preview QA: " + name);
            checks.Add(name);
        }
        foreach (var format in new[] { ImageFormat.Png, ImageFormat.Jpeg })
        {
            using var original = new Bitmap(3200, 1800);
            using (var graphics = Graphics.FromImage(original)) graphics.Clear(Color.FromArgb(24, 80, 180));
            using var stream = new MemoryStream(); original.Save(stream, format);
            var encoded = stream.ToArray();
            using var preview = MessageImagePreview.Decode(encoded);
            Require(preview.Size == new Size(1024, 576), $"{format}: a full-resolution source retains only a proportional 1024px preview");
            var pixel = preview.GetPixel(512, 288);
            Require(Math.Abs(pixel.B - 180) <= 3 && Math.Abs(pixel.G - 80) <= 3,
                $"{format}: decoded preview preserves real source pixels");
        }
        using (var source = new Bitmap(24, 18))
        using (var stream = new MemoryStream())
        {
            source.Save(stream, ImageFormat.Png);
            using var preview = MessageImagePreview.Decode(stream.ToArray());
            Require(preview.Size == source.Size, "Small images are not unnecessarily enlarged");
        }
        var invalid = new byte[33];
        new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }.CopyTo(invalid, 0);
        "IHDR"u8.CopyTo(invalid.AsSpan(12));
        BinaryPrimitives.WriteUInt32BigEndian(invalid.AsSpan(16), 10000);
        BinaryPrimitives.WriteUInt32BigEndian(invalid.AsSpan(20), 10000);
        var sender = new ChatUser(Guid.NewGuid(), "Sentetik", "", false, null);
        var message = new StoredMessage(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), sender.Id,
            "image", DateTimeOffset.UtcNow, null, false, [], null);
        using (var row = new MessageRow(sender, false, message, "", invalid))
        {
            Require(!row.CanMarkRead && !Descendants(row).OfType<PictureBox>().Any(),
                "An oversized PNG header is rejected before native decode and is not marked read");
            Require(invalid.All(value => value == 0), "Rejected plaintext image bytes are cleared");
        }
        var corrupt = new byte[] { 1, 2, 3, 4, 5 };
        using (var row = new MessageRow(sender, false, message, "", corrupt))
            Require(!row.CanMarkRead && corrupt.All(value => value == 0), "Corrupt image is unreadable and plaintext bytes are cleared");
        foreach (var dimensions in new[] { (0L, 200L), (10000L, 5000L), (long.MaxValue, long.MaxValue) })
        {
            var rejected = false;
            try { MessageImagePreview.ValidateDimensions(dimensions.Item1, dimensions.Item2); }
            catch (InvalidDataException) { rejected = true; }
            Require(rejected, $"Invalid or excessive dimensions {dimensions.Item1}x{dimensions.Item2} are rejected without overflow");
        }
        return checks;
    }

    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
}
