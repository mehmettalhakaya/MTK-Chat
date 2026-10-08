using System.Drawing.Drawing2D;
using System.Reflection;
using System.Runtime.InteropServices;

namespace MTKChat.Desktop;

// Exercise actual owner-draw entry points on a reused dirty frame. Calling only
// DrawToBitmap would hide Opaque-button repaint faults and glyph remnants.
internal static class ModernButtonVisualQA
{
    internal static void Verify(string directory)
    {
        Directory.CreateDirectory(directory);
        using var host = new Form { ClientSize = new Size(540, 230), ShowInTaskbar = false };
        var surface = new GradientSurface { Dock = DockStyle.Fill };
        host.Controls.Add(surface);
        using var button = Theme.GlyphButton("\uE724", "Mesajı gönder", ButtonKind.Primary);
        button.VectorIcon = ModernButtonIcon.Send;
        button.CornerRadius = 90;
        button.SetBounds(36, 42, 60, 60);
        surface.Controls.Add(button);
        using var focusTarget = new TextBox { Location = new Point(160, 42), Width = 160 };
        surface.Controls.Add(focusTarget);
        host.Show(); Application.DoEvents();
        focusTarget.Focus(); Application.DoEvents();
        var opaque = (bool)typeof(Control).GetMethod("GetStyle", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(button, [ControlStyles.Opaque])!;
        using var dirty = new Bitmap(100, 100);
        Render(button, dirty, forceBackground: true);
        var step = 0;
        void Check(string name, Action transition, Rectangle? clip = null)
        {
            transition();
            Render(button, dirty, forceBackground: !opaque, clip);
            using var clean = new Bitmap(100, 100);
            Render(button, clean, forceBackground: true);
            var area = clip ?? button.ClientRectangle;
            AssertEqual(dirty, clean, area, name);
            // Pixels outside a resized control's current client are not part of
            // its WM_PAINT surface; do not present that inactive canvas as a UI.
            using var currentClient = dirty.Clone(button.ClientRectangle, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            currentClient.Save(Path.Combine(directory, $"send-repaint-{++step:00}.png"));
            Console.WriteLine($"Button visual QA: {name} has no stale pixels.");
        }
        Check("Vector send hover", () => Invoke(button, "OnMouseEnter", EventArgs.Empty));
        Check("Vector send hover→idle", () => Invoke(button, "OnMouseLeave", EventArgs.Empty));
        Check("Vector send keyboard focus", () =>
        {
            if (!button.Focus()) throw new InvalidOperationException("Send button did not acquire focus.");
            // Show the keyboard cue explicitly even if prior system input was a mouse.
            SendMessage(host.Handle, 0x0127, new IntPtr(2 | (1 << 16)), IntPtr.Zero);
            SendMessage(button.Handle, 0x0127, new IntPtr(2 | (1 << 16)), IntPtr.Zero);
        });
        using (var focused = new Bitmap(100, 100))
        {
            Render(button, focused, forceBackground: true);
            focusTarget.Focus();
            using var unfocused = new Bitmap(100, 100);
            Render(button, unfocused, forceBackground: true);
            if (CountDifferences(focused, unfocused, button.ClientRectangle) == 0)
                throw new InvalidOperationException("Keyboard focus cue disappeared from the vector send button.");
            Console.WriteLine("Button visual QA: keyboard focus cue is visible and distinct from the outer edge.");
        }
        Check("Vector send focus lost", () => focusTarget.Focus());
        Check("Vector send mouse pressed", () => Invoke(button, "OnMouseDown", new MouseEventArgs(MouseButtons.Left, 1, 20, 20, 0)));
        Check("Vector send mouse released", () => Invoke(button, "OnMouseUp", new MouseEventArgs(MouseButtons.Left, 1, 20, 20, 0)));
        Check("Vector send keyboard pressed", () => Invoke(button, "OnKeyDown", new KeyEventArgs(Keys.Space)));
        Check("Vector send keyboard released", () => Invoke(button, "OnKeyUp", new KeyEventArgs(Keys.Space)));
        Check("Vector send disabled", () => button.Enabled = false);
        Check("Vector send re-enabled", () => button.Enabled = true);
        Check("Vector send→empty ghost", () => { button.Kind = ButtonKind.Ghost; button.VectorIcon = ModernButtonIcon.None; button.Text = ""; });
        Check("Ghost text→vector send", () => { button.Text = "Gönder"; button.Kind = ButtonKind.Primary; button.VectorIcon = ModernButtonIcon.Send; });
        Check("Vector send partial invalidation", () => Invoke(button, "OnMouseEnter", EventArgs.Empty), new Rectangle(12, 12, 36, 36));
        Invoke(button, "OnMouseLeave", EventArgs.Empty);
        foreach (var edge in new[] { 48, 60, 75, 90 })
        {
            Check($"Vector send {edge}px geometry", () => button.Size = new Size(edge, edge));
            using var originalFont = new Bitmap(100, 100);
            Render(button, originalFont, forceBackground: true);
            using var alternate = new Font("Arial", 7, FontStyle.Bold);
            var previous = button.Font;
            button.Font = alternate;
            using var alternateFont = new Bitmap(100, 100);
            Render(button, alternateFont, forceBackground: true);
            AssertEqual(originalFont, alternateFont, button.ClientRectangle, "Vector send must not depend on glyph font fallback");
            button.Font = previous;
            Console.WriteLine($"Button visual QA: vector send at {edge}px is font-independent.");
        }
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

    private static int CountDifferences(Bitmap first, Bitmap second, Rectangle area)
    {
        var count = 0;
        for (var y = area.Top; y < area.Bottom; y++)
        for (var x = area.Left; x < area.Right; x++)
            if (first.GetPixel(x, y) != second.GetPixel(x, y)) count++;
        return count;
    }

    private static void AssertEqual(Bitmap dirty, Bitmap clean, Rectangle area, string name)
    {
        var differences = CountDifferences(dirty, clean, area);
        if (differences != 0)
            throw new InvalidOperationException($"Button visual repaint failed: {name}, stale pixels={differences}.");
    }

    private static void Invoke(ModernButton button, string method, object args) =>
        typeof(ModernButton).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(button, [args]);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

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
