using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;

namespace MTKChat.Desktop;

// One window-anchored painting source. Children sample the same coordinates rather
// than stretching a fresh wallpaper into each row or moving it with message scroll.
internal static class ChatWallpaper
{
    private static Bitmap? _wallpaper;
    // A modal and its owner can repaint alternately (especially with GIF avatars).
    // One shared last-size bitmap made both rebuild and dispose each other's canvas.
    private static readonly ConditionalWeakTable<Control, WindowCanvas> WindowCanvases = new();
    private static bool _loadAttempted;

    private sealed class WindowCanvas : IDisposable
    {
        internal Bitmap? Image;
        public void Dispose() { Image?.Dispose(); Image = null; }
    }

    static ChatWallpaper()
    {
        Application.ApplicationExit += (_, _) =>
        {
            _wallpaper?.Dispose(); _wallpaper = null;
            foreach (var entry in WindowCanvases)
            {
                entry.Key.Disposed -= RootDisposed;
                entry.Key.ClientSizeChanged -= RootCanvasChanged;
                entry.Value.Dispose();
            }
            WindowCanvases.Clear();
        };
    }

    internal static void LoadIfExists()
    {
        if (_loadAttempted) return;
        _loadAttempted = true;
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "chat-mountains.png");
        if (!File.Exists(path)) return;
        try
        {
            // Clone once and close the source stream immediately; previews/builds can
            // replace the packaged asset and no per-message image decoding is needed.
            using var source = Image.FromFile(path);
            _wallpaper = new Bitmap(source);
        }
        catch (Exception exception) when (exception is IOException or ArgumentException or ExternalException or UnauthorizedAccessException)
        {
            // Missing/unreadable artwork never prevents login or message rendering.
            _wallpaper = null;
        }
    }

    internal static void Draw(Graphics graphics, Control surface)
    {
        if (surface.IsDisposed || surface.Width <= 0 || surface.Height <= 0) return;
        LoadIfExists();
        Control root = surface;
        var origin = Point.Empty;
        while (root.Parent is { } parent)
        {
            // AutoScroll adjusts child Location too. Including that offset pins the
            // image to the window while a row's own local coordinates travel past it.
            origin.Offset(root.Left, root.Top);
            root = parent;
        }
        var width = Math.Max(1, root.ClientSize.Width);
        var height = Math.Max(1, root.ClientSize.Height);
        var state = graphics.Save();
        graphics.TranslateTransform(-origin.X, -origin.Y);
        // Many transparent controls sample this same scene. Resample only when the
        // window dimensions change, instead of bicubic-scaling a large bitmap per row.
        graphics.DrawImageUnscaled(GetWindowCanvas(root, width, height), 0, 0);
        graphics.Restore(state);
    }

    private static void RootDisposed(object? sender, EventArgs args)
    {
        if (sender is not Control root) return;
        root.Disposed -= RootDisposed;
        root.ClientSizeChanged -= RootCanvasChanged;
        if (WindowCanvases.TryGetValue(root, out var canvas)) canvas.Dispose();
        WindowCanvases.Remove(root);
    }

    private static void RootCanvasChanged(object? sender, EventArgs args)
    {
        if (sender is Control root && !root.IsDisposed)
        {
            // The shared landscape is cover-scaled to the whole window. A resize
            // changes every sample, including rows whose own size/position stayed
            // unchanged; those child HWNDs would otherwise retain the old canvas.
            root.Invalidate(invalidateChildren: true);
        }
    }

    // Read-only observation for the app-owned repaint QA; callers never own this image.
    internal static Bitmap? CachedCanvasForQa(Control root) =>
        WindowCanvases.TryGetValue(root, out var canvas) ? canvas.Image : null;

    private static Bitmap GetWindowCanvas(Control root, int width, int height)
    {
        var canvas = WindowCanvases.GetValue(root, owner =>
        {
            owner.Disposed += RootDisposed;
            owner.ClientSizeChanged += RootCanvasChanged;
            return new WindowCanvas();
        });
        if (canvas.Image is { } cached && cached.Width == width && cached.Height == height)
            return cached;
        var next = new Bitmap(width, height);
        using var graphics = Graphics.FromImage(next);
        var bounds = new Rectangle(0, 0, width, height);
        using (var fallback = new LinearGradientBrush(bounds, Color.FromArgb(14, 20, 47), Color.FromArgb(7, 10, 28), LinearGradientMode.Vertical))
            graphics.FillRectangle(fallback, bounds);
        if (_wallpaper is { } image)
        {
            var ratio = Math.Max(width / (float)image.Width, height / (float)image.Height);
            var imageWidth = image.Width * ratio;
            var imageHeight = image.Height * ratio;
            var destination = new RectangleF((width - imageWidth) / 2, (height - imageHeight) / 2, imageWidth, imageHeight);
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.DrawImage(image, destination);
        }
        // The landscape stays decorative: a shared navy veil keeps contrast stable
        // behind every message, avatar and glass surface at any window size.
        using (var veil = new SolidBrush(Color.FromArgb(110, 4, 6, 22)))
            graphics.FillRectangle(veil, bounds);
        canvas.Image?.Dispose();
        canvas.Image = next;
        return next;
    }
}
