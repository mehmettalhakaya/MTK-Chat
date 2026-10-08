using System.Drawing.Drawing2D;
using System.Reflection;

namespace MTKChat.Desktop;

// A fresh DrawToBitmap image hid the reported bugs. Reuse the same dirty canvas,
// obey the actual control's WM_PAINT/Opaque policy and compare it to a clean frame.
internal static class UiRepaintRegression
{
    internal static void Verify(string directory)
    {
        Directory.CreateDirectory(directory);
        using var host = new Form { Size = new Size(540, 220), ShowInTaskbar = false };
        var surface = new GradientSurface { Dock = DockStyle.Fill };
        host.Controls.Add(surface);
        using var button = Theme.Button("Önceki uzun yazı", ButtonKind.Ghost);
        button.SetBounds(36, 42, 220, 48);
        surface.Controls.Add(button);
        using var focusTarget = new TextBox { Location = new Point(300, 42), Width = 160 };
        surface.Controls.Add(focusTarget);
        host.Show(); Application.DoEvents();
        var opaque = (bool)typeof(Control).GetMethod("GetStyle", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(button, [ControlStyles.Opaque])!;
        Console.WriteLine($"Repaint QA: ModernButton Opaque={opaque}.");
        using var dirty = new Bitmap(button.Width, button.Height);
        Render(button, dirty, forceBackground: true);
        var step = 0;
        void Check(string name, Action transition, Rectangle? clip = null)
        {
            transition();
            Render(button, dirty, forceBackground: !opaque, clip);
            using var clean = new Bitmap(button.Width, button.Height);
            Render(button, clean, forceBackground: true);
            var area = clip ?? button.ClientRectangle;
            var differences = 0;
            for (var y = area.Top; y < area.Bottom; y++)
            for (var x = area.Left; x < area.Right; x++)
                if (dirty.GetPixel(x, y) != clean.GetPixel(x, y)) differences++;
            var file = $"repaint-{++step:00}";
            dirty.Save(Path.Combine(directory, file + ".png"));
            if (differences != 0)
            {
                clean.Save(Path.Combine(directory, file + "-expected.png"));
                throw new InvalidOperationException($"Dynamic repaint failed: {name}, stale pixels={differences}.");
            }
            Console.WriteLine("Repaint QA: " + name + " has no stale pixels.");
        }
        Check("Ghost long→short text", () => button.Text = "Tümü");
        Check("Ghost→secondary", () => button.Kind = ButtonKind.Secondary);
        Check("Secondary→ghost and text change", () => { button.Kind = ButtonKind.Ghost; button.Text = "Kişiler"; });
        Check("Mouse hover", () => Invoke(button, "OnMouseEnter", EventArgs.Empty));
        Check("Hover→idle", () => Invoke(button, "OnMouseLeave", EventArgs.Empty));
        Check("Keyboard focus", () =>
        {
            if (!button.Focus() || !button.Focused) throw new InvalidOperationException("Button did not acquire test focus.");
        });
        Check("Keyboard pressed", () => Invoke(button, "OnKeyDown", new KeyEventArgs(Keys.Space)));
        Check("Keyboard released", () => Invoke(button, "OnKeyUp", new KeyEventArgs(Keys.Space)));
        Check("Focus lost", () =>
        {
            if (!focusTarget.Focus() || button.Focused) throw new InvalidOperationException("Button did not release test focus.");
        });
        Check("Enabled→disabled", () => button.Enabled = false);
        Check("Disabled→enabled", () => button.Enabled = true);
        Check("Caption close glyph→minimize glyph", () => { button.Font = Theme.Glyph(12); button.Text = "\uE921"; });
        Check("Primary rounded corners", () => { button.Kind = ButtonKind.Primary; button.Text = "Gönder"; });
        Check("Primary→ghost", () => { button.Kind = ButtonKind.Ghost; button.Text = ""; });
        Check("Partial invalidation", () => button.Text = "K", new Rectangle(20, 8, 180, 32));
        host.Hide();
    }

    private static void Render(ModernButton button, Bitmap bitmap, bool forceBackground, Rectangle? clip = null)
    {
        using var graphics = Graphics.FromImage(bitmap);
        var area = clip ?? button.ClientRectangle;
        graphics.SetClip(area);
        using var args = new PaintEventArgs(graphics, area);
        if (forceBackground) Invoke(button, "OnPaintBackground", args);
        Invoke(button, "OnPaint", args);
    }

    private static void Invoke(ModernButton button, string method, object args) =>
        typeof(ModernButton).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(button, [args]);

    private sealed class GradientSurface : Panel
    {
        protected override void OnPaintBackground(PaintEventArgs args)
        {
            if (Width < 2 || Height < 2) return;
            using var gradient = new LinearGradientBrush(ClientRectangle, Theme.Sidebar, Theme.SurfaceHover, LinearGradientMode.Horizontal);
            args.Graphics.FillRectangle(gradient, ClientRectangle);
        }
    }
}
