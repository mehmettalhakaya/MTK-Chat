using System.Drawing.Drawing2D;
using System.Reflection;
using System.Runtime.InteropServices;

namespace MTKChat.Desktop;

// Parent hover invalidation must repaint the avatar's HWND too. A fresh
// DrawToBitmap/Refresh(avatar) would conceal the stale square seen by users.
internal static class AvatarHoverPaintQA
{
    internal static IReadOnlyList<string> Verify(string directory)
    {
        Directory.CreateDirectory(directory);
        var checks = new List<string>();
        using var host = new Form { ClientSize = new Size(460, 180), ShowInTaskbar = false };
        var surface = new GradientSurface { Dock = DockStyle.Fill };
        host.Controls.Add(surface);
        using var card = new RoundedPanel
        {
            Location = new Point(24, 34), Size = new Size(340, 88),
            FillColor = Theme.Sidebar, BorderColor = Color.Transparent, CornerRadius = 13
        };
        surface.Controls.Add(card);
        using var avatar = new AvatarView
        {
            Location = new Point(10, 20), Size = new Size(48, 48),
            Initials = "BK", AvatarColor = Theme.SurfaceHover
        };
        card.Controls.Add(avatar);
        var childInvalidations = 0;
        avatar.Invalidated += (_, _) => childInvalidations++;
        host.Show(); Application.DoEvents();

        void Check(string label, Action transition)
        {
            childInvalidations = 0;
            transition();
            // Update only the parent. No explicit child invalidation or clean
            // child repaint is permitted before capturing the native surface.
            card.Update(); Application.DoEvents();
            using var native = Capture(avatar);
            native.Save(Path.Combine(directory, $"avatar-hover-{checks.Count:00}.png"));
            var (checkedPixels, mismatched) = CompareCorners(card, avatar, native);
            Console.WriteLine($"Avatar hover geometry: {label}; corner pixels={checkedPixels}; " +
                $"mismatched={mismatched}; child invalidations={childInvalidations}.");
            if (checkedPixels < 100 || mismatched != 0)
                throw new InvalidOperationException($"Avatar native hover regression: {label}; stale square corner pixels={mismatched}.");
            checks.Add(label + ": native avatar corners match the current parent surface without forcing child paint");
        }

        Check("Initial conversation avatar", () => { });
        Check("Conversation card hover", () => card.FillColor = Color.FromArgb(21, 27, 44));
        Check("Hover to idle", () => card.FillColor = Theme.Sidebar);
        Check("Selected gradient card", () =>
        {
            card.FillColor = Color.FromArgb(31, 38, 66);
            card.GradientEndColor = Color.FromArgb(37, 34, 75);
        });
        Check("Gradient endpoint changes beneath avatar", () => card.GradientEndColor = Theme.SurfaceHover);
        using var photo = new Bitmap(12, 12);
        using (var graphics = Graphics.FromImage(photo)) graphics.Clear(Color.CornflowerBlue);
        avatar.SetPhoto(photo); Application.DoEvents();
        Check("Profile photo on selected gradient", () => { });
        Check("Photo card deselected", () => { card.FillColor = Theme.Sidebar; card.GradientEndColor = Color.Empty; });
        Check("Photo card hover", () => card.FillColor = Color.FromArgb(21, 27, 44));
        foreach (var edge in new[] { 48, 60, 72 })
        {
            avatar.Size = new Size(edge, edge); card.Height = edge + 40;
            Application.DoEvents();
            Check($"{edge}px avatar hover to idle", () => card.FillColor = Theme.Sidebar);
            Check($"{edge}px avatar idle to hover", () => card.FillColor = Color.FromArgb(21, 27, 44));
        }
        host.Hide(); Application.DoEvents();
        VerifyActualConversationCard(directory, checks);
        return checks;
    }

    private static void VerifyActualConversationCard(string directory, List<string> checks)
    {
        using var form = new MainForm(snapshotMode: true);
        form.Show(); form.PopulateSnapshot(); Application.DoEvents();
        var list = (FlowLayoutPanel)typeof(MainForm).GetField("_conversationList",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
        // The first card is selected; use a real unselected card and its real
        // recursive hover callbacks, as in the reported user screenshot.
        var card = list.Controls.OfType<RoundedPanel>().Skip(1).First();
        var avatar = card.Controls.OfType<AvatarView>().Single();
        void Check(string label, Control eventSource)
        {
            typeof(Control).GetMethod("OnMouseEnter", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(eventSource, [EventArgs.Empty]);
            card.Update(); Application.DoEvents();
            using var native = Capture(avatar);
            var (checkedPixels, mismatched) = CompareCorners(card, avatar, native);
            if (checkedPixels < 100 || mismatched != 0 || card.FillColor != Color.FromArgb(21, 27, 44))
                throw new InvalidOperationException($"Actual conversation-card avatar hover regression: {label}; " +
                    $"corner samples={checkedPixels}; stale pixels={mismatched}; surface={card.FillColor}.");
            using var cardFrame = Capture(card);
            cardFrame.Save(Path.Combine(directory, label + ".png"));
            checks.Add(label + ": actual MainForm hover handler updates native avatar corners and preserves the card surface");
        }
        Check("conversation-card-hover-initials", avatar);
        using var photo = new Bitmap(12, 12);
        using (var graphics = Graphics.FromImage(photo)) graphics.Clear(Color.CornflowerBlue);
        avatar.SetPhoto(photo); card.FillColor = Theme.Sidebar; card.Update(); Application.DoEvents();
        Check("conversation-card-hover-photo", card);
        form.Hide(); Application.DoEvents();
    }

    private static (int CheckedPixels, int Mismatched) CompareCorners(RoundedPanel card, AvatarView avatar, Bitmap native)
    {
        using var expected = new Bitmap(card.Width, card.Height);
        using (var graphics = Graphics.FromImage(expected))
        {
            graphics.Clear(Color.Magenta);
            using var args = new PaintEventArgs(graphics, card.ClientRectangle);
            typeof(RoundedPanel).GetMethod("OnPaintBackground", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(card, [args]);
        }
        var mismatched = 0;
        var checkedPixels = 0;
        for (var y = 0; y < avatar.Height; y++)
        for (var x = 0; x < avatar.Width; x++)
        {
            // Stay outside the antialiased circle: every inspected corner
            // pixel belongs solely to the background, not photo/glyph ink.
            var nx = (x + .5f - (avatar.Width - 1) / 2f) / ((avatar.Width - 1) / 2f);
            var ny = (y + .5f - (avatar.Height - 1) / 2f) / ((avatar.Height - 1) / 2f);
            if (nx * nx + ny * ny < 1.14f) continue;
            checkedPixels++;
            var actual = native.GetPixel(x, y);
            var reference = expected.GetPixel(avatar.Left + x, avatar.Top + y);
            if (Math.Abs(actual.R - reference.R) > 1 || Math.Abs(actual.G - reference.G) > 1 ||
                Math.Abs(actual.B - reference.B) > 1) mismatched++;
        }
        return (checkedPixels, mismatched);
    }

    private static Bitmap Capture(Control control)
    {
        var bitmap = new Bitmap(control.ClientSize.Width, control.ClientSize.Height);
        using var graphics = Graphics.FromImage(bitmap);
        var target = graphics.GetHdc();
        var source = GetDC(control.Handle);
        try
        {
            if (source == IntPtr.Zero || !BitBlt(target, 0, 0, bitmap.Width, bitmap.Height, source, 0, 0, 0x00CC0020))
                throw new InvalidOperationException("Cannot capture the app-owned native avatar client.");
        }
        catch { bitmap.Dispose(); throw; }
        finally
        {
            if (source != IntPtr.Zero) ReleaseDC(control.Handle, source);
            graphics.ReleaseHdc(target);
        }
        return bitmap;
    }

    private sealed class GradientSurface : Panel
    {
        protected override void OnPaintBackground(PaintEventArgs args)
        {
            using var brush = new LinearGradientBrush(ClientRectangle, Theme.Sidebar, Theme.SurfaceHover, LinearGradientMode.Horizontal);
            args.Graphics.FillRectangle(brush, ClientRectangle);
        }
    }

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr context);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BitBlt(IntPtr target, int x, int y, int width, int height,
        IntPtr source, int sx, int sy, uint operation);
}
