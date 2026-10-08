using System.Reflection;
using System.Runtime.InteropServices;

namespace MTKChat.Desktop;

// Capture only the application's own synthetic HWND. Dirty-frame comparisons
// deliberately omit OnPaintBackground when ButtonBase's Opaque WM_PAINT does.
internal static class RailProfilePaintQA
{
    internal static IReadOnlyList<string> Verify(string directory)
    {
        Directory.CreateDirectory(directory);
        var checks = new List<string>();
        void Require(bool valid, string check)
        {
            if (!valid) throw new InvalidOperationException("Rail profile repaint QA: " + check);
            checks.Add(check);
        }
        using var host = new Form { ClientSize = new Size(280, 180), ShowInTaskbar = false };
        var rail = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Rail };
        host.Controls.Add(rail);
        var avatar = new AvatarView();
        using var photo = new Bitmap(12, 12);
        using (var graphics = Graphics.FromImage(photo)) graphics.Clear(Color.Lime);
        avatar.SetPhoto(photo);
        using var button = new RailProfileButton(avatar) { Bounds = new Rectangle(24, 24, 44, 44) };
        rail.Controls.Add(button);
        using var focus = new TextBox { Bounds = new Rectangle(96, 24, 130, 28) };
        rail.Controls.Add(focus);
        host.Show(); focus.Focus(); Application.DoEvents();

        using var idleNative = Capture(button);
        idleNative.Save(Path.Combine(directory, "rail-profile-native-first-idle.png"));
        AssertNative(button, idleNative, "first idle photo before any click", directory);
        Require(true, "First real HWND idle photo and its square corners match the enclosing rail before a click");

        using var dirty = new Bitmap(120, 120);
        using (var graphics = Graphics.FromImage(dirty)) graphics.Clear(Color.Magenta);
        var opaque = (bool)typeof(Control).GetMethod("GetStyle", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(button, [ControlStyles.Opaque])!;
        var step = 0;
        void Check(string title, Action transition, Rectangle? clip = null)
        {
            transition();
            Render(button, dirty, !opaque, clip);
            using var expected = new Bitmap(120, 120);
            using (var graphics = Graphics.FromImage(expected)) graphics.Clear(Color.Black);
            Render(button, expected, true);
            AssertEqual(dirty, expected, clip ?? button.ClientRectangle, title);
            button.Update(); avatar.Update(); Application.DoEvents();
            using var native = Capture(button);
            AssertNative(button, native, title, directory);
            native.Save(Path.Combine(directory, $"rail-profile-native-{++step:00}.png"));
            Require(true, title + ": dirty WM_PAINT and real native avatar/tile pixels match a clean frame");
        }
        Check("Initial idle surface on different dirty buffers", () => { });
        Check("Photo hover", () => Invoke(button, "SetHovered", true));
        Check("Photo hover to idle", () => Invoke(button, "SetHovered", false));
        Check("Profile drawer selected", () => button.Active = true);
        Check("Profile drawer closed", () => button.Active = false);
        Check("Mouse press", () => Invoke(button, "OnMouseDown", new MouseEventArgs(MouseButtons.Left, 1, 2, 2, 0)));
        Check("Mouse release", () => Invoke(button, "OnMouseUp", new MouseEventArgs(MouseButtons.Left, 1, 2, 2, 0)));
        Check("Lost keyboard focus", () => focus.Focus());
        Check("Disabled photo target", () => button.Enabled = false);
        Check("Re-enabled photo target", () => button.Enabled = true);
        Check("Avatar replacement while idle", () =>
        {
            using var replacement = new Bitmap(12, 12);
            using (var graphics = Graphics.FromImage(replacement)) graphics.Clear(Color.Blue);
            avatar.SetPhoto(replacement);
        });
        for (var frame = 0; frame < 8; frame++)
            Check($"Child-only idle repaint {frame + 1}", () => avatar.Invalidate());
        Check("Changed enclosing rail color", () => { rail.BackColor = Theme.Canvas; rail.Invalidate(true); });
        Check("Restored enclosing rail color", () => { rail.BackColor = Theme.Rail; rail.Invalidate(true); });
        Check("Partial corner invalidation", () =>
        {
            button.Invalidate(new Rectangle(0, 0, 14, 14));
            avatar.Invalidate();
        }, new Rectangle(0, 0, 14, 14));
        foreach (var edge in new[] { 44, 55, 66 })
        {
            Check($"Rail hit target {edge}px", () => button.Size = new Size(edge, edge));
            Require(button.ClientRectangle.Contains(avatar.Bounds) && avatar.Width == avatar.Height,
                $"Rail hit target {edge}px keeps a square bounded photo");
        }
        VerifyNavigationButtons(rail, focus, checks, directory);
        host.Hide(); Application.DoEvents();
        return checks;
    }

    private static void VerifyNavigationButtons(Control rail, Control focus, List<string> checks, string directory)
    {
        // The wider button suites use gradient parents. These controls live on
        // the rail's ordinary opaque native parent, like the real navigation.
        using var button = new ModernButton { Bounds = new Rectangle(112, 78, 44, 44), Kind = ButtonKind.Ghost };
        rail.Controls.Add(button);
        focus.Focus(); Application.DoEvents();
        var paint = typeof(ModernButton).GetMethod("OnPaint", BindingFlags.NonPublic | BindingFlags.Instance)!;
        foreach (var icon in new[] { ModernButtonIcon.Smile, ModernButtonIcon.Attachment, ModernButtonIcon.Chat,
            ModernButtonIcon.Settings, ModernButtonIcon.Shield })
        {
            button.VectorIcon = icon;
            foreach (var phase in new[] { "idle", "disabled", "re-enabled" })
            {
                button.Enabled = phase != "disabled";
                button.Invalidate(); button.Update(); Application.DoEvents();
                using var actual = Capture(button);
                using var expected = new Bitmap(button.Width, button.Height);
                using (var graphics = Graphics.FromImage(expected))
                using (var args = new PaintEventArgs(graphics, button.ClientRectangle)) paint.Invoke(button, [args]);
                AssertEqual(actual, expected, button.ClientRectangle, $"Rail {icon} {phase}");
                actual.Save(Path.Combine(directory, $"rail-navigation-{icon}-{phase}.png"));
                checks.Add($"Rail {icon} {phase}: native icon/background pixels match the clean frame on the opaque rail parent");
            }
        }
    }

    private static void Render(RailProfileButton button, Bitmap target, bool background, Rectangle? clip = null)
    {
        using var graphics = Graphics.FromImage(target);
        var area = clip ?? button.ClientRectangle;
        graphics.SetClip(area);
        using (var args = new PaintEventArgs(graphics, area))
        {
            if (background) Invoke(button, "OnPaintBackground", args);
            Invoke(button, "OnPaint", args);
        }
        var state = graphics.Save();
        graphics.TranslateTransform(button.Avatar.Left, button.Avatar.Top);
        // Each real child HWND is clipped to its own client. Without this clip,
        // the reflected avatar background would erase the parent's focus ring
        // outside the avatar, producing a false native-repaint discrepancy.
        graphics.SetClip(button.Avatar.ClientRectangle, System.Drawing.Drawing2D.CombineMode.Intersect);
        using var childArgs = new PaintEventArgs(graphics, button.Avatar.ClientRectangle);
        typeof(AvatarView).GetMethod("OnPaintBackground", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(button.Avatar, [childArgs]);
        typeof(AvatarView).GetMethod("OnPaint", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(button.Avatar, [childArgs]);
        graphics.Restore(state);
    }

    private static void AssertNative(RailProfileButton button, Bitmap native, string title, string directory)
    {
        using var expected = new Bitmap(button.Width, button.Height);
        Render(button, expected, true);
        try { AssertEqual(native, expected, button.ClientRectangle, title); }
        catch
        {
            native.Save(Path.Combine(directory, "rail-profile-failed-native.png"));
            expected.Save(Path.Combine(directory, "rail-profile-failed-expected.png"));
            throw;
        }
    }

    private static void AssertEqual(Bitmap first, Bitmap second, Rectangle area, string title)
    {
        var stale = 0;
        for (var y = area.Top; y < area.Bottom; y++)
        for (var x = area.Left; x < area.Right; x++)
        {
            var a = first.GetPixel(x, y); var b = second.GetPixel(x, y);
            if (a.R != b.R || a.G != b.G || a.B != b.B) stale++;
        }
        if (stale != 0) throw new InvalidOperationException($"Rail profile repaint QA: {title}; stale RGB pixels={stale}.");
    }

    private static void Invoke(RailProfileButton button, string method, object value) =>
        typeof(RailProfileButton).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(button, [value]);

    private static Bitmap Capture(Control control)
    {
        var image = new Bitmap(control.Width, control.Height);
        var source = GetDC(control.Handle);
        try
        {
            using var graphics = Graphics.FromImage(image);
            var target = graphics.GetHdc();
            try
            {
                if (source == IntPtr.Zero || !BitBlt(target, 0, 0, image.Width, image.Height, source, 0, 0, 0x00CC0020))
                    throw new InvalidOperationException("Cannot capture the app-owned profile button HWND.");
            }
            finally { graphics.ReleaseHdc(target); }
        }
        catch { image.Dispose(); throw; }
        finally { if (source != IntPtr.Zero) ReleaseDC(control.Handle, source); }
        return image;
    }

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr context);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BitBlt(IntPtr target, int x, int y, int width, int height, IntPtr source, int sx, int sy, uint operation);
}
