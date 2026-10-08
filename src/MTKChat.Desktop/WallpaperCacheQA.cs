namespace MTKChat.Desktop;

// Snapshot-only repaint probes use the real wallpaper draw path without accounts,
// native input injection or a desktop screenshot. No image is owned by this observer.
internal static class WallpaperCacheQA
{
    internal static IReadOnlyList<string> Verify()
    {
        var checks = new List<string>();
        void Require(bool valid, string name)
        {
            if (!valid) throw new InvalidOperationException("Wallpaper cache regression: " + name);
            checks.Add(name);
        }

        using var owner = new Form { ClientSize = new Size(640, 420) };
        using var modal = new Form { ClientSize = new Size(360, 260) };
        var ownerSurface = new Panel { Bounds = new Rectangle(17, 23, 80, 64) };
        var modalSurface = new Panel { Bounds = new Rectangle(11, 19, 80, 64) };
        owner.Controls.Add(ownerSurface);
        modal.Controls.Add(modalSurface);

        static Bitmap Repaint(Control surface)
        {
            var bitmap = new Bitmap(surface.Width, surface.Height);
            using var graphics = Graphics.FromImage(bitmap);
            ChatWallpaper.Draw(graphics, surface);
            return bitmap;
        }

        using (var first = Repaint(ownerSurface))
        using (var second = Repaint(modalSurface))
        {
            var ownerCanvas = ChatWallpaper.CachedCanvasForQa(owner);
            var modalCanvas = ChatWallpaper.CachedCanvasForQa(modal);
            Require(ownerCanvas is not null && modalCanvas is not null && !ReferenceEquals(ownerCanvas, modalCanvas),
                "Different roots own independent canvases");
            Require(first.GetPixel(0, 0) == ownerCanvas!.GetPixel(ownerSurface.Left, ownerSurface.Top) &&
                second.GetPixel(0, 0) == modalCanvas!.GetPixel(modalSurface.Left, modalSurface.Top),
                "Child repaint samples the window-anchored coordinates");

            for (var repaint = 0; repaint < 8; repaint++)
            {
                using var ownerFrame = Repaint(ownerSurface);
                using var modalFrame = Repaint(modalSurface);
                Require(ReferenceEquals(ownerCanvas, ChatWallpaper.CachedCanvasForQa(owner)) &&
                    ReferenceEquals(modalCanvas, ChatWallpaper.CachedCanvasForQa(modal)),
                    $"Alternating repaint {repaint + 1} reuses both canvases");
            }

            owner.ClientSize = new Size(700, 440);
            using var resizedFrame = Repaint(ownerSurface);
            var resizedCanvas = ChatWallpaper.CachedCanvasForQa(owner);
            Require(resizedCanvas is not null && resizedCanvas.Size == owner.ClientSize &&
                !ReferenceEquals(ownerCanvas, resizedCanvas) &&
                ReferenceEquals(modalCanvas, ChatWallpaper.CachedCanvasForQa(modal)),
                "Resizing one root preserves the other root's canvas");

            modal.Dispose();
            Require(ChatWallpaper.CachedCanvasForQa(modal) is null &&
                ReferenceEquals(resizedCanvas, ChatWallpaper.CachedCanvasForQa(owner)),
                "Closing the modal releases only its cache");
        }

        owner.Dispose();
        Require(ChatWallpaper.CachedCanvasForQa(owner) is null,
            "Closing the owner releases its cache");
        return checks;
    }
}
