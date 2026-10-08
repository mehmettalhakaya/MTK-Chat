using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace MTKChat.Desktop;

internal static class PhotoPreparation
{
    internal static (byte[] Jpeg, Bitmap Preview) ReadSquare(string path)
    {
        if (new FileInfo(path).Length > 8 * 1024 * 1024) throw new InvalidDataException("En fazla 8 MB bir fotoğraf seç.");
        using var stream = File.OpenRead(path);
        using var image = Image.FromStream(stream, false, true);
        if ((long)image.Width * image.Height > 24_000_000) throw new InvalidDataException("En fazla 24 megapiksel bir fotoğraf seç.");
        if (image.PropertyIdList.Contains(0x112))
        {
            var orientation = image.GetPropertyItem(0x112)?.Value?.FirstOrDefault() ?? 1;
            image.RotateFlip(orientation switch {
                2 => RotateFlipType.RotateNoneFlipX, 3 => RotateFlipType.Rotate180FlipNone,
                4 => RotateFlipType.Rotate180FlipX, 5 => RotateFlipType.Rotate90FlipX,
                6 => RotateFlipType.Rotate90FlipNone, 7 => RotateFlipType.Rotate270FlipX,
                8 => RotateFlipType.Rotate270FlipNone, _ => RotateFlipType.RotateNoneFlipNone });
        }
        var square = new Bitmap(512, 512);
        try
        {
            using (var graphics = Graphics.FromImage(square))
            {
                graphics.Clear(Color.White);
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                var edge = Math.Min(image.Width, image.Height);
                graphics.DrawImage(image, new Rectangle(0, 0, 512, 512), (image.Width - edge) / 2,
                    (image.Height - edge) / 2, edge, edge, GraphicsUnit.Pixel);
            }
            using var encoded = new MemoryStream();
            square.Save(encoded, ImageFormat.Jpeg);
            return (encoded.ToArray(), square);
        }
        catch { square.Dispose(); throw; }
    }
}
