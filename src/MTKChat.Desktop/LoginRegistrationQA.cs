using System.Reflection;

namespace MTKChat.Desktop;

// Local-only login probes: an injected launcher records the real LinkClicked
// destination without opening a browser, registering an account, or sending HTTP.
internal static class LoginRegistrationQA
{
    internal static IReadOnlyList<string> Verify(string directory)
    {
        Directory.CreateDirectory(directory);
        var checks = new List<string>();
        void Require(bool valid, string text)
        {
            if (!valid) throw new InvalidOperationException("Login registration QA: " + text);
            checks.Add(text);
        }

        Require(LoginForm.RegistrationUri is
            { Scheme: "https", Host: "mtkaya.me", AbsolutePath: "/loginregister.html", UserInfo: "", Query: "", Fragment: "" },
            "Signup uses the verified first-party HTTPS registration page, without credentials or an invented tab query/fragment");

        foreach (var width in new[] { 980, 840, 720 })
        {
            var opened = new List<Uri>();
            using var form = new LoginForm(opened.Add);
            form.Show();
            form.ClientSize = new Size((int)Math.Round(width * form.DeviceDpi / 96f),
                (int)Math.Round(624 * form.DeviceDpi / 96f));
            form.PerformLayout(); Application.DoEvents();
            var register = form.RegistrationLinkForQa;
            Require(register.Visible && register.Text == "Hesabınız yok mu? Kayıt olun ↗" &&
                register.ForeColor == Theme.Muted && register.LinkColor == Theme.Violet && register.TabStop,
                $"A keyboard-focusable themed signup footer is visible at {width}px / {form.DeviceDpi} DPI");
            Require(register.Links.Count == 1 && register.Links[0].Start == "Hesabınız yok mu? ".Length &&
                register.Links[0].Length == "Kayıt olun ↗".Length &&
                Equals(register.Links[0].LinkData, LoginForm.RegistrationUri),
                "Only Kayıt olun is actionable; the preceding question remains a quiet label");
            var measured = TextRenderer.MeasureText(register.Text, register.Font,
                new Size(int.MaxValue, int.MaxValue), TextFormatFlags.SingleLine);
            Require(register.Parent!.ClientRectangle.Contains(register.Bounds) &&
                register.Parent.Parent!.ClientRectangle.Contains(register.Parent.Bounds) &&
                register.Width >= measured.Width && register.Height >= measured.Height &&
                !register.Bounds.IntersectsWith(form.LoginButtonForQa.Bounds) &&
                !register.Bounds.IntersectsWith(form.ErrorForQa.Bounds),
                $"The signup sentence fits fully below the login/error rows inside its rounded card at {width}px");

            var click = typeof(LinkLabel).GetMethod("OnLinkClicked", BindingFlags.Instance | BindingFlags.NonPublic)!;
            click.Invoke(register, [new LinkLabelLinkClickedEventArgs(register.Links[0])]);
            Require(opened.Count == 1 && opened[0] == LoginForm.RegistrationUri &&
                form.DialogResult == DialogResult.None && form.Email.Length == 0 && form.Password.Length == 0,
                "The actual signup link opens exactly one fixed website URL without submitting or closing the login form");
            form.ShowError("Bağlantı kurulamadı. Tekrar deneyebilirsin."); Application.DoEvents();
            Require(register.Visible && !register.Bounds.IntersectsWith(form.ErrorForQa.Bounds),
                "A visible login failure cannot hide or overlap the registration link");
            using var image = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(image, new Rectangle(Point.Empty, form.Size));
            image.Save(Path.Combine(directory, $"login-registration-{width}.png"));
            form.Hide();
        }

        using (var unavailableBrowser = new LoginForm(_ => throw new InvalidOperationException("Synthetic launch failure")))
        {
            unavailableBrowser.OpenRegistration();
            Require(unavailableBrowser.ErrorForQa.Text.Contains("Tarayıcı açılamadı", StringComparison.Ordinal) &&
                unavailableBrowser.ErrorForQa.Text.Contains("mtkaya.me", StringComparison.Ordinal) &&
                unavailableBrowser.LoginButtonForQa.Enabled && unavailableBrowser.DialogResult == DialogResult.None,
                "A browser-launch failure stays inline, retains the login window, and leaves retry available");
        }
        return checks;
    }
}
