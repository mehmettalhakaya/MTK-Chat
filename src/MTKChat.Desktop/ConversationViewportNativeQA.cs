using System.Reflection;
using System.Runtime.InteropServices;

namespace MTKChat.Desktop;

// DrawToBitmap skips non-client scrollbars. Inspect and capture only this fake
// window's own client surface so native resize regressions are not concealed.
internal static class ConversationViewportNativeQA
{
    internal static IReadOnlyList<string> Verify(string directory)
    {
        Directory.CreateDirectory(directory);
        var checks = new List<string>();
        using var form = new MainForm(snapshotMode: true);
        form.Show(); form.PopulateSnapshot(); Application.DoEvents();
        var list = (FlowLayoutPanel)typeof(MainForm).GetField("_conversationList",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
        var viewport = (ModernConversationViewport)list.Parent!.Parent!;
        void Check(string name, FlowLayoutPanel targetList, ModernConversationViewport targetViewport)
        {
            Application.DoEvents();
            using var frame = Capture(targetViewport);
            frame.Save(Path.Combine(directory, "conversation-native-" + checks.Count.ToString("00") + ".png"));
            var diagnostics = $"bounds={targetList.Bounds}, client={targetList.ClientSize}, display={targetList.DisplayRectangle}, " +
                $"min={targetList.AutoScrollMinSize}, horizontal={targetList.HorizontalScroll.Visible}/" +
                $"{targetList.HorizontalScroll.Maximum}/{targetList.HorizontalScroll.LargeChange}, " +
                $"cards={string.Join(';', targetList.Controls.Cast<Control>().Where(card => card.Visible).Select(card => card.Bounds))}";
            Console.WriteLine("Conversation native geometry: " + name + ": " + diagnostics);
            if (targetList.HorizontalScroll.Visible || (GetWindowLong(targetList.Handle, -16) & 0x00100000) != 0)
                throw new InvalidOperationException("Conversation native QA: horizontal scrollbar is visible after " + name + ". " + diagnostics);
            if (targetList.ClientSize.Width != targetViewport.ViewportWidth || targetList.ClientSize.Height != targetViewport.ClientSize.Height)
                throw new InvalidOperationException("Conversation native QA: native client and clipped viewport disagree after " + name + ". " + diagnostics);
            checks.Add(name + ": no native horizontal rail; client matches the clipped viewport");
        }
        Check("Initial wide list", list, viewport);
        form.Size = new Size(1120, 720); Check("Wide-to-compact resize", list, viewport);
        list.AutoScrollPosition = new Point(0, viewport.MaximumOffset); Check("Compact native bottom scroll", list, viewport);
        var visible = list.Controls.Cast<Control>().ToArray();
        foreach (var card in visible.Skip(1)) card.Visible = false;
        Check("Filtering to one conversation removes vertical overflow", list, viewport);
        foreach (var card in visible) card.Visible = true;
        Check("Restoring conversations recreates vertical overflow", list, viewport);
        form.Size = new Size(1920, 1040); Check("Compact-to-wide resize", list, viewport);
        form.Size = new Size(1120, 720); Check("Second compact resize", list, viewport);
        foreach (var check in viewport.VerifyScrolling()) checks.Add(check);
        form.Hide();

        // The same wrapper is also used by the new receipt panel. A short direct
        // receipt list must not retain a stale wider extent during host reparenting.
        using var receiptForm = new MainForm(snapshotMode: true) { Size = new Size(1536, 940) };
        receiptForm.Show(); receiptForm.PopulateMessageInfoSnapshot(direct: true);
        var panel = (MessageInfoPanel)typeof(MainForm).GetField("_messageInfoPanel",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(receiptForm)!;
        var receiptList = (FlowLayoutPanel)typeof(MessageInfoPanel).GetField("_list",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(panel)!;
        var receiptViewport = (ModernConversationViewport)receiptList.Parent!.Parent!;
        Check("340 px direct receipt pane", receiptList, receiptViewport);
        receiptForm.Size = new Size(1120, 720);
        Check("Compact overlay receipt pane", receiptList, receiptViewport);
        receiptForm.Size = new Size(1536, 940);
        Check("Restored wide receipt pane", receiptList, receiptViewport);
        receiptForm.Hide();
        return checks;
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
                throw new InvalidOperationException("Own conversation-viewport client DC capture failed.");
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
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr window, int index);
    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BitBlt(IntPtr destination, int x, int y, int width, int height,
        IntPtr source, int sourceX, int sourceY, int operation);
}
