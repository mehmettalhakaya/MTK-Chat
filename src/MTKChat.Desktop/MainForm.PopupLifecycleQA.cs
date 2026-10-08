using System.Runtime.InteropServices;

namespace MTKChat.Desktop;

internal sealed partial class MainForm
{
    internal static IReadOnlyList<string> VerifyPopupLifecycle()
    {
        var checks = new List<string>();
        void Require(bool condition, string check)
        {
            if (!condition) throw new InvalidOperationException("Popup lifetime regression: " + check);
            checks.Add(check);
        }

        // This is an isolated app-owned form, not the user's running chat window.
        // Feed the framework's real menu message filter a mouse message addressed
        // only to this helper HWND; no desktop input or global hook is used.
        using var form = new MainForm(snapshotMode: true);
        form.Show(); form.PerformLayout(); Application.DoEvents();
        ModernContextMenu? previous = null;
        for (var cycle = 0; cycle < 24; cycle++)
        {
            form.ShowExpiryMenu(); Application.DoEvents();
            var menu = FindPopup(form._premiumExpiryButton);
            Require(menu is not null && menu.Visible, $"Cycle {cycle + 1}: expiry popup is visible");
            if (previous is not null)
                Require(ReferenceEquals(previous, menu), $"Cycle {cycle + 1}: one owned popup is reused");
            var message = Message.Create(form.Handle, 0x0201 /* WM_LBUTTONDOWN */,
                new IntPtr(1), new IntPtr((50 << 16) | 20));
            // Reentrant Dispose in Closed used to throw ObjectDisposedException
            // from ToolStripDropDown.SetVisibleCore after this filter returned
            // from the Closed callback; the real application's main loop crashed.
            Application.FilterMessage(ref message);
            Require(!menu!.Visible && !menu.IsDisposed,
                $"Cycle {cycle + 1}: outside click closes without destroying the active dropdown");
            Application.DoEvents();
            previous = menu;
        }
        form.ShowExpiryMenu(); Application.DoEvents();
        var owned = FindPopup(form._premiumExpiryButton);
        Require(owned is not null && owned.Visible, "Owned menu opens before form teardown");
        form.Close(); Application.DoEvents();
        Require(owned!.IsDisposed, "Form teardown releases the owned popup");
        return checks;
    }

    private static ModernContextMenu? FindPopup(Control source)
    {
        ModernContextMenu? found = null;
        EnumThreadWindows(GetCurrentThreadId(), (handle, _) =>
        {
            if (Control.FromHandle(handle) is ModernContextMenu { Visible: true } menu &&
                ReferenceEquals(menu.SourceControl, source)) found = menu;
            return true;
        }, IntPtr.Zero);
        return found;
    }

    private delegate bool EnumWindowCallback(IntPtr handle, IntPtr parameter);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumThreadWindows(uint threadId, EnumWindowCallback callback, IntPtr parameter);
    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
