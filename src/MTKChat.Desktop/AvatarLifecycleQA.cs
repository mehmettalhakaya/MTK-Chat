using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace MTKChat.Desktop;

internal static class AvatarLifecycleQA
{
    internal static IReadOnlyList<string> Verify()
    {
        var checks = new List<string>();
        void Require(bool condition, string check)
        {
            if (!condition) throw new InvalidOperationException("Avatar lifecycle regression: " + check);
            checks.Add(check);
        }
        using var form = new Form { Size = new Size(180, 180), BackColor = Theme.Canvas };
        using var avatar = new AvatarView { Location = new Point(16, 16), Size = new Size(64, 64) };
        form.Controls.Add(avatar); form.Show(); Application.DoEvents();
        var notification = typeof(AvatarView).GetMethod("OnAnimationFrame", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var animating = typeof(AvatarView).GetField("_photoAnimating", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var photo = typeof(AvatarView).GetField("_photo", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var stream = typeof(AvatarView).GetField("_photoStream", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var invalidates = 0;
        avatar.Invalidated += (_, _) => invalidates++;

        // An occupied UI pump can receive hundreds of notifications before it
        // handles even the first one. Test the real BeginInvoke callback queue,
        // not only an implementation flag or a forced clean bitmap repaint.
        for (var i = 0; i < 500; i++) notification.Invoke(avatar, [null, EventArgs.Empty]);
        Require(invalidates == 0, "Animation notifications stay asynchronous while the UI pump is busy");
        Application.DoEvents();
        Require(invalidates == 1, "Five hundred pending frame notifications collapse into one UI invalidation");
        avatar.Hide(); Application.DoEvents(); invalidates = 0;
        for (var i = 0; i < 500; i++) notification.Invoke(avatar, [null, EventArgs.Empty]);
        Application.DoEvents();
        Require(invalidates == 0, "Hidden avatars enqueue no frame invalidations");
        avatar.Show(); Application.DoEvents();

        var gif = CreateGif(); avatar.SetEncodedPhoto(gif);
        Require((bool)animating.GetValue(avatar)!, "A visible animated avatar registers the GIF frame source");
        avatar.Hide();
        Require(!(bool)animating.GetValue(avatar)!, "Hiding the avatar pauses its GIF frame source");
        avatar.Show();
        Require((bool)animating.GetValue(avatar)!, "Showing the avatar resumes GIF animation");
        form.Hide();
        Require(!(bool)animating.GetValue(avatar)!, "Hiding the parent window also pauses child animation");
        form.Show(); Application.DoEvents();
        Require((bool)animating.GetValue(avatar)!, "Showing the parent window resumes child animation");
        notification.Invoke(avatar, [null, EventArgs.Empty]);
        typeof(Control).GetMethod("RecreateHandle", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(avatar, null);
        Require(avatar.IsHandleCreated && (bool)animating.GetValue(avatar)!,
            "Native handle recreation preserves a live, animating avatar");
        avatar.SetPhoto(null); Application.DoEvents();
        Require(!(bool)animating.GetValue(avatar)! && photo.GetValue(avatar) is null && stream.GetValue(avatar) is null,
            "Replacing the GIF with initials releases the decoder and its backing stream");

        // Dispose while a native UI callback is still queued, repeatedly. This
        // exercises the exact late-callback/owner-close race without audio
        // devices, network APIs, desktop input or the user's running window.
        var baselineGdi = GetGuiResources(Process.GetCurrentProcess().Handle, 0);
        for (var i = 0; i < 40; i++)
        {
            var pending = new AvatarView { Size = new Size(48, 48), Location = new Point(92, 16) };
            form.Controls.Add(pending); pending.SetEncodedPhoto(gif);
            var backingStream = (MemoryStream)stream.GetValue(pending)!;
            for (var frame = 0; frame < 100; frame++) notification.Invoke(pending, [null, EventArgs.Empty]);
            pending.Dispose();
            Require(!(bool)animating.GetValue(pending)! && photo.GetValue(pending) is null &&
                stream.GetValue(pending) is null && !backingStream.CanRead,
                $"Close cycle {i + 1}: GIF registration, image and stream are released");
            notification.Invoke(pending, [null, EventArgs.Empty]);
            Application.DoEvents();
        }
        var finalGdi = GetGuiResources(Process.GetCurrentProcess().Handle, 0);
        Require(finalGdi <= baselineGdi + 8, "Repeated pending-callback disposal leaves no growing GDI resource count");
        form.Close(); Application.DoEvents();
        return checks;
    }

    private static byte[] CreateGif()
    {
        var bytes = new List<byte>(Encoding.ASCII.GetBytes("GIF89a"));
        bytes.AddRange([1, 0, 1, 0, 0x80, 0, 0, 255, 0, 0, 0, 0, 255]);
        bytes.AddRange([0x21, 0xff, 11]); bytes.AddRange(Encoding.ASCII.GetBytes("NETSCAPE2.0"));
        bytes.AddRange([3, 1, 0, 0, 0]);
        foreach (var color in new byte[] { 0x44, 0x4c })
        {
            bytes.AddRange([0x21, 0xf9, 4, 0, 10, 0, 0, 0]);
            bytes.AddRange([0x2c, 0, 0, 0, 0, 1, 0, 1, 0, 0, 2, 2, color, 1, 0]);
        }
        bytes.Add(0x3b);
        return bytes.ToArray();
    }

    [DllImport("user32.dll")]
    private static extern uint GetGuiResources(IntPtr processHandle, uint flags);
}
