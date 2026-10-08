using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MTKChat.Desktop;

// Synthetic, app-owned HWND capture only. GetDC/BitBlt reads a GDI backing
// surface, which can omit the native DirectX MemoEdit even after Paint has run.
// This helper neither reads desktop pixels nor replaces a capture with artwork.
internal static class NativeQaCapture
{
    internal static Bitmap Capture(Control control)
    {
        if (control.IsDisposed || !control.IsHandleCreated || !control.Visible ||
            control.ClientSize.Width <= 0 || control.ClientSize.Height <= 0)
            throw new InvalidOperationException("Native QA capture requires a shown app-owned window.");

        var elapsed = Stopwatch.StartNew();
        do
        {
            control.Invalidate(true); control.Update(); Application.DoEvents();
            Thread.Sleep(10);
        } while (elapsed.Elapsed < TimeSpan.FromMilliseconds(180));
        DwmFlush();

        var bitmap = new Bitmap(control.ClientSize.Width, control.ClientSize.Height);
        try
        {
            using var graphics = Graphics.FromImage(bitmap);
            var destination = graphics.GetHdc();
            try
            {
                // PW_CLIENTONLY | PW_RENDERFULLCONTENT includes the DirectX
                // child surface without depending on a stale GDI backing DC.
                if (!PrintWindow(control.Handle, destination, 0x00000001 | 0x00000002))
                    throw new InvalidOperationException("Cannot capture the complete QA-owned client surface.");
            }
            finally { graphics.ReleaseHdc(destination); }
            return bitmap;
        }
        catch { bitmap.Dispose(); throw; }
    }

    [DllImport("dwmapi.dll")] private static extern int DwmFlush();
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PrintWindow(IntPtr window, IntPtr destination, uint flags);
}
