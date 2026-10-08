using System.Drawing.Drawing2D;
using System.Reflection;
using System.Runtime.InteropServices;

namespace MTKChat.Desktop;

// Geometry must cover the actual composer's non-square slot, not only an
// isolated ideal 60×60 sample. These checks never use live accounts or APIs.
internal static class SendButtonGeometryQA
{
    internal static IReadOnlyList<string> Verify(string directory)
    {
        Directory.CreateDirectory(directory);
        var results = new List<string>();
        using var host = new Form { ClientSize = new Size(360, 220), ShowInTaskbar = false };
        var surface = new GradientSurface { Dock = DockStyle.Fill };
        host.Controls.Add(surface);
        using var button = Theme.GlyphButton("\uE724", "Mesajı gönder", ButtonKind.Primary);
        button.CircularSurface = true;
        button.Location = new Point(40, 40);
        button.CornerRadius = 30;
        surface.Controls.Add(button);
        using var focusTarget = new TextBox { Location = new Point(200, 40), Width = 100 };
        surface.Controls.Add(focusTarget);
        host.Show(); Application.DoEvents();
        focusTarget.Focus(); Application.DoEvents();
        foreach (var size in new[] { new Size(56, 60), new Size(58, 60), new Size(60, 56),
                     new Size(57, 61), new Size(61, 57), new Size(70, 75),
                     new Size(75, 70), new Size(84, 90), new Size(112, 120) })
        {
            button.Size = size;
            var bounds = ModernButton.CenteredCircleBounds(size);
            Require(bounds.Width == bounds.Height && Math.Abs(bounds.Left + bounds.Width / 2 - size.Width / 2f) < .01f &&
                Math.Abs(bounds.Top + bounds.Height / 2 - size.Height / 2f) < .01f,
                "Send path is not a centered circle: " + size);
            using var bitmap = new Bitmap(size.Width, size.Height);
            Render(button, bitmap);
            var silhouette = AccentBounds(bitmap);
            Require(Math.Abs(silhouette.Width - silhouette.Height) <= 1,
                "Send painted silhouette is stretched: " + size + " → " + silhouette);
            Require(Math.Abs(silhouette.Left + silhouette.Width / 2f - size.Width / 2f) <= 1 &&
                Math.Abs(silhouette.Top + silhouette.Height / 2f - size.Height / 2f) <= 1,
                "Send painted silhouette is off-center: " + size + " → " + silhouette);
            var ink = PixelBounds(bitmap, pixel => pixel.R > 220 && pixel.G > 220 && pixel.B > 220);
            Require(Math.Abs(ink.Left + ink.Width / 2f - size.Width / 2f) <= 1 &&
                Math.Abs(ink.Top + ink.Height / 2f - size.Height / 2f) <= 1 &&
                ink.Width < silhouette.Width * .65 && ink.Height < silhouette.Height * .65,
                "Send vector is not centered with balanced breathing space: " + size + " → " + ink);
            // A previous integer round-rectangle path could leave a flat cap.
            // The top/bottom circle silhouette must be much narrower than its equator.
            var cap = AccentSpan(bitmap, silhouette.Top + 2);
            var middle = AccentSpan(bitmap, (silhouette.Top + silhouette.Bottom) / 2);
            Require(cap < middle * .65, "Send circle has a flat or clipped cap: " + size);
            bitmap.Save(Path.Combine(directory, $"send-circle-{size.Width}x{size.Height}.png"));
            results.Add($"Painted {size.Width}×{size.Height} slot has a circular, centered, unclipped send surface.");
            // Reflection-only owner-draw checks skip the real ButtonBase paint
            // pipeline and double buffer. Inspect this fixture's own client DC
            // after WM_PAINT too: an inherited native cap/rim must not survive.
            button.Refresh(); Application.DoEvents();
            using var native = Capture(button);
            RequireEqualRgb(native, bitmap, button.ClientRectangle, "native send surface " + size);
            native.Save(Path.Combine(directory, $"send-native-{size.Width}x{size.Height}.png"));
            results.Add($"Native WM_PAINT {size.Width}×{size.Height} surface matches the clean vector/circle paint.");
        }

        button.Size = new Size(58, 60);
        using var dirty = new Bitmap(button.Width, button.Height);
        Render(button, dirty);
        void CheckTransition(string label, Action transition, Rectangle? area = null)
        {
            transition(); Render(button, dirty, area);
            using var clean = new Bitmap(button.Width, button.Height);
            Render(button, clean);
            var rectangle = area ?? button.ClientRectangle;
            for (var y = rectangle.Top; y < rectangle.Bottom; y++)
            for (var x = rectangle.Left; x < rectangle.Right; x++)
                Require(dirty.GetPixel(x, y) == clean.GetPixel(x, y), "Send circle retained dirty pixels: " + label);
            results.Add("Non-square send dirty repaint: " + label + ".");
        }
        CheckTransition("hover", () => Invoke(button, "OnMouseEnter", EventArgs.Empty));
        CheckTransition("hover → idle", () => Invoke(button, "OnMouseLeave", EventArgs.Empty));
        CheckTransition("press", () => Invoke(button, "OnMouseDown", new MouseEventArgs(MouseButtons.Left, 1, 20, 20, 0)));
        CheckTransition("release", () => Invoke(button, "OnMouseUp", new MouseEventArgs(MouseButtons.Left, 1, 20, 20, 0)));
        CheckTransition("disabled", () => button.Enabled = false);
        CheckTransition("enabled", () => button.Enabled = true);
        CheckTransition("partial invalidation", () => Invoke(button, "OnMouseEnter", EventArgs.Empty), new Rectangle(8, 8, 34, 34));
        Invoke(button, "OnMouseLeave", EventArgs.Empty);
        VerifyNativeTransitions(button, focusTarget, host, results, directory);
        VerifyRailVectors(button, results, directory);
        host.Hide();

        using var form = new MainForm(snapshotMode: true);
        form.Show(); form.PopulateSnapshot(); Application.DoEvents();
        foreach (var size in new[] { new Size(1120, 720), new Size(1536, 940), new Size(1920, 1040) })
        {
            form.Size = size; form.PerformLayout(); Application.DoEvents();
            VerifyProduction(form, results, size + " normal composer");
        }
        form.Size = new Size(1120, 720);
        form.PopulateImageDraftSnapshot(); form.PerformLayout(); Application.DoEvents();
        VerifyProduction(form, results, "image draft");
        form.PopulateFileDraftSnapshot(); form.PerformLayout(); Application.DoEvents();
        VerifyProduction(form, results, "file draft");
        form.PopulateVoiceDraftSnapshot(); form.PerformLayout(); Application.DoEvents();
        VerifyProduction(form, results, "voice draft");
        form.Hide();
        return results;
    }

    private static void VerifyProduction(MainForm form, List<string> results, string label)
    {
        var controls = Descendants(form).ToArray();
        var send = controls.OfType<ModernButton>().Single(button => button.AccessibleName == "Mesajı gönder");
        var attach = controls.OfType<ModernButton>().Single(button => button.AccessibleName == "Görsel veya dosya ekle");
        var emoji = controls.OfType<ModernButton>().Single(button => button.AccessibleName == "Emoji ekle");
        var slot = send.Parent!;
        Require(send.CircularSurface && send.Width == send.Height && send.Dock == DockStyle.None && slot.DisplayRectangle.Contains(send.Bounds),
            "Production send is stretched or clipped: " + label + " " + send.Bounds);
        Require(Math.Abs(send.Left + send.Width / 2f - (slot.DisplayRectangle.Left + slot.DisplayRectangle.Width / 2f)) <= .5f &&
            Math.Abs(send.Top + send.Height / 2f - (slot.DisplayRectangle.Top + slot.DisplayRectangle.Height / 2f)) <= .5f,
            "Production send is off-center inside its slot: " + label);
        var sendCenter = send.PointToScreen(new Point(send.Width / 2, send.Height / 2)).Y;
        foreach (var neighbor in new[] { attach, emoji })
        {
            var center = neighbor.PointToScreen(new Point(neighbor.Width / 2, neighbor.Height / 2)).Y;
            Require(Math.Abs(sendCenter - center) <= 1, "Toolbar centers disagree: " + label);
        }
        Require(send.Visible && send.Enabled && send.AccessibleName == "Mesajı gönder" && send.Text.Length == 0,
            "Send behavior/accessibility changed: " + label);
        results.Add("Production centered square send and aligned toolbar: " + label + ".");
    }

    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control control in parent.Controls)
        {
            yield return control;
            foreach (var child in Descendants(control)) yield return child;
        }
    }

    private static Rectangle AccentBounds(Bitmap bitmap) => PixelBounds(bitmap, IsAccent);

    private static Rectangle PixelBounds(Bitmap bitmap, Func<Color, bool> matches)
    {
        var left = bitmap.Width; var top = bitmap.Height; var right = -1; var bottom = -1;
        for (var y = 0; y < bitmap.Height; y++)
        for (var x = 0; x < bitmap.Width; x++)
        {
            if (!matches(bitmap.GetPixel(x, y))) continue;
            left = Math.Min(left, x); top = Math.Min(top, y); right = Math.Max(right, x); bottom = Math.Max(bottom, y);
        }
        Require(right >= left && bottom >= top, "Expected send surface/vector pixels were not painted.");
        return Rectangle.FromLTRB(left, top, right + 1, bottom + 1);
    }

    private static int AccentSpan(Bitmap bitmap, int y)
    {
        var left = bitmap.Width; var right = -1;
        for (var x = 0; x < bitmap.Width; x++)
        {
            if (!IsAccent(bitmap.GetPixel(x, y))) continue;
            left = Math.Min(left, x); right = Math.Max(right, x);
        }
        return right >= left ? right - left + 1 : 0;
    }

    private static bool IsAccent(Color pixel) => pixel.B > 170 && pixel.R < 150 && pixel.G < 140;
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
    private static void Invoke(ModernButton button, string name, EventArgs args) =>
        typeof(ModernButton).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(button, [args]);
    private static void Render(ModernButton button, Bitmap bitmap, Rectangle? clip = null)
    {
        using var graphics = Graphics.FromImage(bitmap);
        var bounds = clip ?? button.ClientRectangle; graphics.SetClip(bounds);
        using var args = new PaintEventArgs(graphics, bounds);
        // Mimic ButtonBase's Opaque native WM_PAINT, without an extra background pass.
        typeof(ModernButton).GetMethod("OnPaint", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(button, [args]);
    }

    private static void VerifyNativeTransitions(ModernButton button, TextBox focusTarget, Form host,
        List<string> results, string directory)
    {
        var step = 0;
        void Check(string label, Action transition)
        {
            transition(); button.Update(); Application.DoEvents();
            using var native = Capture(button);
            using var fresh = new Bitmap(button.Width, button.Height);
            Render(button, fresh);
            RequireEqualRgb(native, fresh, button.ClientRectangle, "native dirty repaint " + label);
            native.Save(Path.Combine(directory, $"send-native-state-{++step:00}.png"));
            results.Add("Native send repaint has no stale edges/ink: " + label + ".");
        }
        Check("hover", () => Invoke(button, "OnMouseEnter", EventArgs.Empty));
        Check("hover → idle", () => Invoke(button, "OnMouseLeave", EventArgs.Empty));
        Check("keyboard focus", () =>
        {
            Require(button.Focus(), "Native send fixture could not acquire keyboard focus.");
            SendMessage(host.Handle, 0x0127, new IntPtr(2 | (1 << 16)), IntPtr.Zero);
            SendMessage(button.Handle, 0x0127, new IntPtr(2 | (1 << 16)), IntPtr.Zero);
            button.Invalidate();
        });
        Check("focus lost", () => { focusTarget.Focus(); button.Invalidate(); });
        Check("pressed", () => Invoke(button, "OnMouseDown", new MouseEventArgs(MouseButtons.Left, 1, 20, 20, 0)));
        Check("released", () => Invoke(button, "OnMouseUp", new MouseEventArgs(MouseButtons.Left, 1, 20, 20, 0)));
        Check("disabled", () => button.Enabled = false);
        Check("enabled", () => button.Enabled = true);
        // Keep state unchanged and repaint only the top edge. This is the area
        // that looked like a flat clipped line in the reported screenshot.
        Check("top-edge-only invalidation", () => button.Invalidate(new Rectangle(0, 0, button.Width, 4)));
    }

    private static void RequireEqualRgb(Bitmap native, Bitmap fresh, Rectangle area, string label)
    {
        // BitBlt copies the screen DC's RGB, not a meaningful ARGB alpha channel.
        // Compare only visible color so an alpha-storage difference cannot hide
        // a genuine native clipped cap or produce a false geometry failure.
        const int rgbMask = 0x00ffffff;
        for (var y = area.Top; y < area.Bottom; y++)
        for (var x = area.Left; x < area.Right; x++)
            Require((native.GetPixel(x, y).ToArgb() & rgbMask) == (fresh.GetPixel(x, y).ToArgb() & rgbMask),
                $"Send circle native pixels differ from clean paint: {label}, pixel={x}/{y}.");
    }

    private static void VerifyRailVectors(ModernButton button, List<string> results, string directory)
    {
        button.Kind = ButtonKind.Ghost;
        button.CircularSurface = false;
        button.Text = "";
        foreach (var icon in new[] { ModernButtonIcon.Chat, ModernButtonIcon.Settings,
                     ModernButtonIcon.Profile, ModernButtonIcon.Back, ModernButtonIcon.Shield })
        foreach (var edge in new[] { 26, 42, 63 })
        {
            button.Size = new Size(edge, edge);
            // At 26px a 1.2px anti-aliased outline can have no nearly-white
            // pixels. Detect ink against the same unpainted parent instead of
            // assuming that every healthy small vector has a solid white core.
            button.VectorIcon = ModernButtonIcon.None;
            using var background = new Bitmap(edge, edge);
            Render(button, background);
            button.VectorIcon = icon;
            using var original = new Bitmap(edge, edge);
            Render(button, original);
            using var inkMask = new Bitmap(edge, edge);
            var visibleInkPixels = 0;
            for (var y = 0; y < edge; y++)
            for (var x = 0; x < edge; x++)
            {
                var painted = original.GetPixel(x, y); var parent = background.GetPixel(x, y);
                if (Math.Max(Math.Max(Math.Abs(painted.R - parent.R), Math.Abs(painted.G - parent.G)),
                        Math.Abs(painted.B - parent.B)) < 48) continue;
                inkMask.SetPixel(x, y, Color.White); visibleInkPixels++;
            }
            Require(visibleInkPixels >= Math.Max(6, edge / 2),
                $"Rail vector is missing or has insufficient contrast: {icon}/{edge}, ink pixels={visibleInkPixels}.");
            var ink = PixelBounds(inkMask, pixel => pixel.A == 255);
            Require(ink.Left > 0 && ink.Top > 0 && ink.Right < edge && ink.Bottom < edge,
                $"Rail icon touches its slot boundary: {icon}/{edge}.");
            var previousFont = button.Font;
            using var unrelatedFont = new Font("Arial", 24, FontStyle.Bold);
            try
            {
                button.Font = unrelatedFont;
                using var alternate = new Bitmap(edge, edge);
                Render(button, alternate);
                RequireEqualRgb(original, alternate, button.ClientRectangle, "font-independent rail " + icon + "/" + edge);
            }
            finally { button.Font = previousFont; }
            original.Save(Path.Combine(directory, $"rail-icon-{icon}-{edge}.png"));
            results.Add($"Rail {icon} {edge}px has unclipped, font-independent vector ink.");
        }
    }

    private static Bitmap Capture(Control control)
    {
        var bitmap = new Bitmap(control.ClientSize.Width, control.ClientSize.Height);
        using var graphics = Graphics.FromImage(bitmap);
        var destination = graphics.GetHdc();
        var source = GetDC(control.Handle);
        try
        {
            if (source == IntPtr.Zero || !BitBlt(destination, 0, 0, bitmap.Width, bitmap.Height,
                    source, 0, 0, 0x00cc0020))
                throw new InvalidOperationException("Own send-button client DC capture failed.");
        }
        finally
        {
            if (source != IntPtr.Zero) ReleaseDC(control.Handle, source);
            graphics.ReleaseHdc(destination);
        }
        return bitmap;
    }

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BitBlt(IntPtr destination, int x, int y, int width, int height,
        IntPtr source, int sourceX, int sourceY, int operation);

    private sealed class GradientSurface : Panel
    {
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            if (Width < 2 || Height < 2) return;
            using var gradient = new LinearGradientBrush(ClientRectangle, Theme.Sidebar, Theme.SurfaceRaised, LinearGradientMode.Horizontal);
            e.Graphics.FillRectangle(gradient, ClientRectangle);
        }
    }
}
