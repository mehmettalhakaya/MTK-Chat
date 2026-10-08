using System.Drawing.Drawing2D;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace MTKChat.Desktop;

internal static class RoundedPanelVisualQA
{
    internal static void Verify(string directory)
    {
        Directory.CreateDirectory(directory);
        using var form = new Form { ClientSize = new Size(400, 180), ShowInTaskbar = false };
        var surface = new GradientSurface { Dock = DockStyle.Fill };
        form.Controls.Add(surface);
        using var panel = new RoundedPanel { FillColor = Theme.SurfaceRaised, BorderColor = Theme.Divider, CornerRadius = 14 };
        panel.SetBounds(30, 38, 220, 78);
        surface.Controls.Add(panel);
        form.Show(); Application.DoEvents(); surface.Refresh(); panel.Refresh(); Application.DoEvents();
        using (var native = Capture(surface))
        {
            native.Save(Path.Combine(directory, "rounded-panel-native-opaque.png"));
            var black = 0;
            for (var y = 0; y < panel.Height; y++)
            for (var x = 0; x < panel.Width; x++)
            {
                // Native region clips the square corners out. Only pixels that
                // belong to this panel's actual visible client count as defects.
                if (panel.Region?.IsVisible(x + .5f, y + .5f) == false) continue;
                var pixel = native.GetPixel(panel.Left + x, panel.Top + y);
                if (pixel.R <= 2 && pixel.G <= 2 && pixel.B <= 2) black++;
            }
            Console.WriteLine($"Rounded panel QA: native opaque client black pixels={black}.");
            if (black != 0)
                throw new InvalidOperationException($"Opaque rounded panel retained {black} black/unpainted native edge pixels.");
        }
        form.Hide();
        VerifyReferenceNativeSurface(directory);

        using var dirty = new Bitmap(panel.Width, panel.Height);
        using (var initial = Graphics.FromImage(dirty)) initial.Clear(Color.Black);
        Paint(panel, dirty);
        var step = 0;
        void Check(string name, Action transition, Rectangle? clip = null)
        {
            transition();
            Paint(panel, dirty, clip);
            using var fresh = new Bitmap(panel.Width, panel.Height);
            using (var initial = Graphics.FromImage(fresh)) initial.Clear(Color.Magenta);
            Paint(panel, fresh);
            using var paintedMask = new Bitmap(panel.Width, panel.Height);
            using (var maskGraphics = Graphics.FromImage(paintedMask))
            {
                if (panel.Region is { } region) maskGraphics.SetClip(region, CombineMode.Intersect);
                maskGraphics.FillRectangle(Brushes.White, panel.ClientRectangle);
            }
            var area = clip ?? panel.ClientRectangle;
            var different = 0;
            for (var y = area.Top; y < area.Bottom; y++)
            for (var x = area.Left; x < area.Right; x++)
            {
                // Graphics' raster region clip and the analytic IsVisible(.5f)
                // boundary disagree on a few arc-edge pixels. Compare only the
                // pixels that actually belong to this WM_PAINT clip.
                if (paintedMask.GetPixel(x, y).A == 0) continue;
                if (dirty.GetPixel(x, y) != fresh.GetPixel(x, y)) different++;
            }
            dirty.Save(Path.Combine(directory, $"rounded-panel-repaint-{++step:00}.png"));
            if (different != 0)
                throw new InvalidOperationException($"Rounded panel dirty repaint failed: {name}, stale/seed-dependent pixels={different}.");
            Console.WriteLine("Rounded panel QA: " + name + " has no stale or unpainted client pixels.");
        }
        Check("Initial opaque surface on differently seeded buffers", () => { });
        Check("Opaque fill color change", () => panel.FillColor = Theme.Outgoing);
        Check("Opaque→glass", () => panel.FillColor = Color.FromArgb(180, Theme.Surface));
        Check("Glass repeated repaint", () => { });
        Check("Glass→opaque", () => panel.FillColor = Theme.SurfaceRaised);
        Check("Opaque gradient", () => panel.GradientEndColor = Theme.SurfaceHover);
        Check("Gradient→glass gradient", () => panel.GradientEndColor = Color.FromArgb(170, Theme.SurfaceHover));
        Check("Glass accent glow", () => panel.AccentGlow = true);
        Check("Corner radius change", () => panel.CornerRadius = 24);
        Check("Border disabled", () => panel.BorderWidth = 0);
        Check("Border and opaque gradient restored", () => { panel.BorderWidth = 1; panel.FillColor = Theme.SurfaceRaised; panel.GradientEndColor = Theme.Surface; });
        Check("Partial edge invalidation", () => panel.FillColor = Theme.Outgoing, new Rectangle(panel.Width - 26, 0, 26, panel.Height));
    }

    private static void VerifyReferenceNativeSurface(string directory)
    {
        using var form = new MainForm(snapshotMode: true) { Size = new Size(1536, 940) };
        form.Show(); form.PopulateMessageInfoSnapshot(direct: true); Application.DoEvents(); form.Refresh(); Application.DoEvents();
        using var native = Capture(form);
        native.Save(Path.Combine(directory, "rounded-panel-native-reference.png"));
        var origin = form.PointToScreen(Point.Empty);
        var black = 0;
        var panels = 0;
        foreach (var rounded in Descendants(form).OfType<RoundedPanel>())
        {
            if (!rounded.Visible || rounded.FillColor.A != 255 || rounded.Width < 20 || rounded.Height < 20) continue;
            panels++;
            var panelBlack = 0;
            var location = rounded.PointToScreen(Point.Empty);
            location.Offset(-origin.X, -origin.Y);
            for (var y = 0; y < rounded.Height; y++)
            for (var x = 0; x < rounded.Width; x++)
            {
                if (x > 2 && x < rounded.Width - 3 && y > 2 && y < rounded.Height - 3) continue;
                if (rounded.Region?.IsVisible(x + .5f, y + .5f) == false) continue;
                var targetX = location.X + x;
                var targetY = location.Y + y;
                if (targetX < 0 || targetX >= native.Width || targetY < 0 || targetY >= native.Height) continue;
                var pixel = native.GetPixel(targetX, targetY);
                if (pixel.R <= 2 && pixel.G <= 2 && pixel.B <= 2) { black++; panelBlack++; }
            }
            if (panelBlack != 0)
            {
                Console.WriteLine($"Rounded panel QA: unpainted edge in {rounded.Name}/{rounded.GetType().Name}, client origin={location}, size={rounded.Size}, black={panelBlack}.");
                DiagnosePanelCapture(rounded, directory, panels);
            }
        }
        Console.WriteLine($"Rounded panel QA: actual reference native opaque panels={panels}, black edge pixels={black}.");
        form.Hide();
        if (black != 0)
            throw new InvalidOperationException($"Reference rounded panels retained {black} black/unpainted native edge pixels.");
    }

    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }

    private static void Paint(RoundedPanel panel, Bitmap frame, Rectangle? clip = null)
    {
        using var graphics = Graphics.FromImage(frame);
        if (panel.Region is { } region) graphics.SetClip(region, CombineMode.Intersect);
        var area = clip ?? panel.ClientRectangle;
        graphics.SetClip(area, CombineMode.Intersect);
        using var args = new PaintEventArgs(graphics, area);
        typeof(RoundedPanel).GetMethod("OnPaintBackground", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(panel, [args]);
    }

    private static Bitmap Capture(Control control) => NativeQaCapture.Capture(control);

    private static void DiagnosePanelCapture(RoundedPanel panel, string directory, int index)
    {
        // Diagnostics only: never replace the form capture or exclude this panel
        // from the unchanged strict assertion. A per-HWND comparison identifies
        // sibling/composition damage instead of guessing at rounded-edge colours.
        using var own = Capture(panel);
        own.Save(Path.Combine(directory, $"rounded-panel-native-failed-{index:00}.png"));
        for (var y = 0; y < own.Height; y++)
        {
            var count = 0; var first = -1; var last = -1;
            for (var x = 0; x < own.Width; x++)
            {
                if (panel.Region?.IsVisible(x + .5f, y + .5f) == false) continue;
                var pixel = own.GetPixel(x, y);
                if (pixel.R > 2 || pixel.G > 2 || pixel.B > 2) continue;
                count++; if (first < 0) first = x; last = x;
            }
            if (count != 0)
                Console.WriteLine($"Rounded panel QA: own HWND black row={y}, count={count}, x={first}..{last}.");
        }
        foreach (Control child in panel.Controls)
            Console.WriteLine($"Rounded panel QA: managed child {child.GetType().Name}, bounds={child.Bounds}, screen origin={child.PointToScreen(Point.Empty)}, padding={child.Padding}, dpi={child.DeviceDpi}.");
        EnumChildWindows(panel.Handle, (window, _) =>
        {
            var className = new StringBuilder(256);
            GetClassName(window, className, className.Capacity);
            if (GetWindowRect(window, out var rect))
                Console.WriteLine($"Rounded panel QA: native child {className}, screen bounds={rect.Left},{rect.Top},{rect.Right - rect.Left},{rect.Bottom - rect.Top}.");
            return true;
        }, IntPtr.Zero);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { internal int Left, Top, Right, Bottom; }
    private delegate bool EnumChildProc(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumChildWindows(IntPtr parent, EnumChildProc callback, IntPtr parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, StringBuilder text, int maximum);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect rectangle);

    private sealed class GradientSurface : Panel
    {
        protected override void OnPaintBackground(PaintEventArgs args)
        {
            using var gradient = new LinearGradientBrush(ClientRectangle, Theme.Sidebar, Theme.SurfaceHover, LinearGradientMode.Horizontal);
            args.Graphics.FillRectangle(gradient, ClientRectangle);
        }
    }
}
