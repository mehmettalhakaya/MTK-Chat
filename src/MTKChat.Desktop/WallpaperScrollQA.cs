using System.Runtime.InteropServices;
using MTKChat.Contracts;

namespace MTKChat.Desktop;

// This probe reads only the client DC of its own fake chat window after normal WM_PAINT.
// DrawToBitmap/WM_PRINT would freshly render the wallpaper and conceal retained scroll pixels.
internal static class WallpaperScrollQA
{
    internal static IReadOnlyList<string> Verify(string directory)
    {
        Directory.CreateDirectory(directory);
        var checks = new List<string>();
        using var host = new Form
        {
            ClientSize = new Size(820, 600), StartPosition = FormStartPosition.CenterScreen,
            ShowInTaskbar = false, Text = "MTK Chat · Wallpaper repaint QA"
        };
        var list = new ModernMessageList
        {
            FlowDirection = FlowDirection.TopDown, WrapContents = false,
            Padding = new Padding(16, 10, 16, 16), BackColor = Color.Transparent
        };
        var viewport = new ModernMessageViewport(list) { Bounds = new Rectangle(24, 42, 760, 480) };
        host.Controls.Add(viewport);
        var sender = new ChatUser(Guid.NewGuid(), "QA User", "qa@example.invalid", false, null);
        var room = Guid.NewGuid();
        for (var index = 0; index < 24; index++)
        {
            if (index % 5 == 0) list.Controls.Add(new MessageDateDivider(DateTime.Today.AddDays(index / 5 - 4)) { Width = 690 });
            var message = new StoredMessage(Guid.NewGuid(), Guid.NewGuid(), room, sender.Id, "text",
                DateTimeOffset.Now, null, false, [], null);
            list.Controls.Add(new MessageRow(sender, false, message,
                index % 3 == 0 ? "Test message.\nSecond line.\nThird line." : "Test message.", null) { Width = 690 });
        }
        host.Show(); Application.DoEvents(); host.PerformLayout(); list.PerformLayout(); Application.DoEvents();
        void Require(bool valid, string name)
        {
            if (!valid) throw new InvalidOperationException("Wallpaper scroll regression: " + name);
            checks.Add(name);
        }
        void Check(string name)
        {
            // Pump only pending paint work. Do not force Refresh/DrawToBitmap here: a force repaint
            // would fix the stale child/list surfaces that this test is meant to detect.
            Application.DoEvents();
            using var frame = CaptureClient(list, new Size(viewport.Width - 14, list.ClientSize.Height));
            var canvas = ChatWallpaper.CachedCanvasForQa(host) ?? throw new InvalidOperationException("QA wallpaper was not drawn.");
            var origin = Point.Empty;
            for (Control control = list; control.Parent is not null; control = control.Parent)
                origin.Offset(control.Left, control.Top);
            var failures = 0;
            var sampled = 0;
            var columns = BackgroundColumns(list);
            foreach (var column in columns)
            for (var y = 2; y < frame.Height - 2; y++)
            {
                sampled++;
                if (frame.GetPixel(column, y) != canvas.GetPixel(origin.X + column, origin.Y + y)) failures++;
            }
            frame.Save(Path.Combine(directory, "wallpaper-scroll-" + checks.Count.ToString("00") + ".png"));
            Require(failures == 0, $"{name}: {sampled} background pixels remain window-anchored after native repaint " +
                $"(stale={failures}, dpi={list.DeviceDpi}, columns={string.Join("/", columns)})");
        }
        Require(list.MaximumOffset > 800, "Fixture overflows and uses real native scrolling");
        Check("Initial window");
        list.ScrollToOffset(137); Check("Programmatic non-row-aligned scroll");
        list.ScrollToOffset(417); Check("Second programmatic scroll");
        SendMessage(list.Handle, 0x0115, new IntPtr(1), IntPtr.Zero); Check("Native WM_VSCROLL line down");
        SendMessage(list.Handle, 0x020a, new IntPtr(unchecked((int)0xff880000)), new IntPtr(0)); Check("Native WM_MOUSEWHEEL");
        list.ScrollControlIntoView(list.Controls[^1]); Check("ScrollControlIntoView to last row");
        list.ScrollToOffset(0); Check("Return to first message");
        var first = list.Controls.OfType<MessageRow>().First();
        first.Width = 580;
        host.ClientSize = new Size(860, 650);
        viewport.Size = new Size(780, 510); list.PerformLayout(); Check("Window resize and row reflow");
        list.Controls.SetChildIndex(list.Controls[^1], 1); list.PerformLayout(); Check("Reordered child rows");
        first.Width = 145; list.PerformLayout(); Check("Standalone message-height reflow");
        viewport.Location = new Point(40, 74); Check("Viewport relocation without child-local movement");
        list.Invalidate(new Rectangle(0, 130, 110, 45), invalidateChildren: true); Check("Partial background/child invalidation");
        host.Hide();
        return checks;
    }

    private static int[] BackgroundColumns(ModernMessageList list)
    {
        var rows = list.Controls.OfType<MessageRow>().Where(row => row.Visible).ToArray();
        if (rows.Length == 0) throw new InvalidOperationException("Wallpaper QA has no incoming message rows.");
        if (rows.Any(row => !row.Avatar.Visible))
            throw new InvalidOperationException("Wallpaper QA gutter fixture must use incoming rows with visible avatars.");
        // One column exercises the list surface and a second exercises every
        // transparent row's own native surface. A hard-coded +45 landed inside
        // the 50px avatar at 120 DPI (40 logical pixels), falsely treating avatar
        // ink as stale wallpaper. Derive the common true gutter from live bounds
        // after every resize/reflow rather than weakening the pixel assertion.
        var gutterLeft = rows.Max(row => row.Left + row.Avatar.Right + 1);
        var gutterRight = rows.Min(row => row.Left + row.Controls.OfType<RoundedPanel>().Single().Left - 1);
        if (gutterRight < gutterLeft)
            throw new InvalidOperationException($"Wallpaper QA has no common background gap: {gutterLeft}/{gutterRight}, dpi={list.DeviceDpi}.");
        var gutterColumn = gutterLeft + (gutterRight - gutterLeft) / 2;
        var paddingColumn = Math.Max(1, list.Padding.Left / 2);
        if (list.Controls.Cast<Control>().Any(control => control.Visible && control.Left <= paddingColumn) ||
            gutterColumn >= list.ViewportWidth || paddingColumn == gutterColumn)
            throw new InvalidOperationException("Wallpaper QA background columns overlap content or lie outside the clipped viewport.");
        return [paddingColumn, gutterColumn];
    }

    private static Bitmap CaptureClient(Control control, Size size)
    {
        var bitmap = new Bitmap(size.Width, size.Height);
        using var graphics = Graphics.FromImage(bitmap);
        var destination = graphics.GetHdc();
        var source = GetDC(control.Handle);
        try
        {
            if (source == IntPtr.Zero || !BitBlt(destination, 0, 0, size.Width, size.Height, source, 0, 0, 0x00cc0020))
                throw new InvalidOperationException("Own-window client DC capture failed.");
        }
        finally { if (source != IntPtr.Zero) ReleaseDC(control.Handle, source); graphics.ReleaseHdc(destination); }
        return bitmap;
    }

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BitBlt(IntPtr destination, int x, int y, int width, int height, IntPtr source, int sourceX, int sourceY, int operation);
}
