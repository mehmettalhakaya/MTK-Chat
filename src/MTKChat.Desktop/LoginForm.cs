using System.Drawing.Drawing2D;
using DevExpress.XtraEditors;
using DevExpress.XtraEditors.Controls;

namespace MTKChat.Desktop;

internal sealed class LoginForm : ModernForm
{
    // The website has one verified sign-in/registration page. Keep the destination
    // fixed instead of accepting an untrusted URL from account or server metadata.
    internal static readonly Uri RegistrationUri = new("https://mtkaya.me/loginregister.html");
    private readonly TextEdit _email = new();
    private readonly TextEdit _password = new();
    private readonly ModernButton _login = Theme.Button("Giriş yap  ↗", ButtonKind.Primary);
    private readonly Label _error = new();
    private readonly Action<Uri> _openWebsite;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal LinkLabel RegistrationLinkForQa { get; private set; } = null!;
    internal Label ErrorForQa => _error;
    internal ModernButton LoginButtonForQa => _login;

    public string Email => _email.Text.Trim();
    public string Password => _password.Text;

    public LoginForm() : this(uri => System.Diagnostics.Process.Start(
        new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true })) { }

    internal LoginForm(Action<Uri> openWebsite)
    {
        _openWebsite = openWebsite ?? throw new ArgumentNullException(nameof(openWebsite));
        Text = "MTK Chat · Giriş";
        Icon = Theme.AppIcon();
        // This form owns its pixel geometry; scaling the font and a second auto-layout pass
        // made the old headline/input spacing diverge on Windows 125% and 150% displays.
        AutoScaleMode = AutoScaleMode.None;
        ClientSize = new Size(Scale(980), Scale(624));
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Theme.Canvas;
        ForeColor = Theme.Text;
        Font = Theme.Font(10f);
        AcceptButton = _login;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = Theme.Canvas
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 44));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 56));
        root.Controls.Add(new LoginArtwork(), 0, 0);
        root.Controls.Add(BuildForm(), 1, 0);
        Controls.Add(root);
    }

    private Control BuildForm()
    {
        var host = new LoginSurface { Dock = DockStyle.Fill };
        var content = new RoundedPanel
        {
            FillColor = Color.FromArgb(230, 25, 31, 59), GradientEndColor = Color.FromArgb(230, 16, 22, 46),
            BorderColor = Color.FromArgb(72, 79, 123), CornerRadius = 18
        };
        host.Controls.Add(content);
        var title = new Label
        {
            Text = "Giriş yap", ForeColor = Theme.Text,
            Font = Theme.Font(22f, FontStyle.Bold), BackColor = Color.Transparent
        };
        var subtitle = new Label
        {
            Text = "mtkaya.me hesabınla", ForeColor = Theme.Muted,
            Font = Theme.Font(9.5f), BackColor = Color.Transparent
        };

        var emailLabel = new Label
        {
            Text = "Kullanıcı adı veya e-posta", ForeColor = Theme.Muted,
            Font = Theme.Font(8.5f), BackColor = Color.Transparent
        };
        StyleInput(_email, "mtkaya.me hesabın");
        var emailFrame = InputFrame(_email);

        var passwordLabel = new Label
        {
            Text = "Parola", ForeColor = Theme.Muted,
            Font = Theme.Font(8.5f), BackColor = Color.Transparent
        };
        StyleInput(_password, "Parolan");
        _password.Properties.UseSystemPasswordChar = true;
        var passwordFrame = InputFrame(_password);

        _error.ForeColor = Theme.Danger;
        _error.Font = Theme.Font(8.5f);
        _error.BackColor = Color.Transparent;

        _login.Text = "Giriş yap  ↗";
        _login.Font = Theme.Font(10f, FontStyle.Bold);
        _login.CornerRadius = 12;
        _login.Cursor = Cursors.Hand;
        _login.Click += (_, _) =>
        {
            if (Email.Length == 0 || Password.Length == 0) { ShowError("Kullanıcı adını ve parolanı gir."); return; }
            DialogResult = DialogResult.OK;
        };
        var forgot = new LinkLabel
        {
            Text = "Şifremi unuttum ↗", BackColor = Color.Transparent,
            TextAlign = ContentAlignment.MiddleRight, LinkColor = Theme.Violet,
            ActiveLinkColor = Theme.AccentHover, VisitedLinkColor = Theme.Violet,
            LinkBehavior = LinkBehavior.HoverUnderline, Font = Theme.Font(8.5f)
        };
        forgot.LinkClicked += (_, _) =>
        {
            try { _openWebsite(RegistrationUri); }
            catch { ShowError("Tarayıcı açılamadı. mtkaya.me giriş sayfasını açabilirsin."); }
        };

        const string signupPrompt = "Hesabınız yok mu? ";
        const string signupAction = "Kayıt olun ↗";
        var register = RegistrationLinkForQa = new LinkLabel
        {
            Name = "RegistrationLink", Text = signupPrompt + signupAction,
            AccessibleName = "Hesabınız yok mu? Kayıt olun",
            AccessibleDescription = "mtkaya.me kayıt sayfasını varsayılan tarayıcıda açar.",
            BackColor = Color.Transparent, ForeColor = Theme.Muted,
            TextAlign = ContentAlignment.MiddleCenter, LinkColor = Theme.Violet,
            ActiveLinkColor = Theme.AccentHover, VisitedLinkColor = Theme.Violet,
            LinkBehavior = LinkBehavior.HoverUnderline, Font = Theme.Font(9f),
            TabStop = true
        };
        register.Links.Clear();
        register.Links.Add(signupPrompt.Length, signupAction.Length, RegistrationUri);
        register.LinkClicked += (_, _) => OpenRegistration();
        content.Controls.AddRange([title, subtitle, emailLabel, emailFrame,
            passwordLabel, passwordFrame, forgot, _error, _login, register]);
        void LayoutContent()
        {
            var width = Math.Max(Scale(300), Math.Min(Scale(400), host.ClientSize.Width - Scale(48)));
            // Reserve a separate footer row; the new signup link must never sit
            // on top of the error message, submit button, or rounded card edge.
            var height = Scale(522);
            content.SetBounds(Math.Max(0, (host.ClientSize.Width - width) / 2),
                Math.Max(0, (host.ClientSize.Height - height) / 2), width, height);
            var inset = Scale(32);
            var innerWidth = width - inset * 2;
            void Place(Control control, int top, int controlHeight) =>
                control.SetBounds(inset, Scale(top), innerWidth, Scale(controlHeight));
            Place(title, 32, 48);
            Place(subtitle, 85, 28);
            Place(emailLabel, 132, 20);
            Place(emailFrame, 159, 48);
            Place(passwordLabel, 225, 20);
            Place(passwordFrame, 251, 48);
            Place(forgot, 310, 24);
            Place(_error, 347, 38);
            Place(_login, 398, 48);
            Place(register, 462, 28);
        }
        host.Resize += (_, _) => LayoutContent();
        host.DpiChangedAfterParent += (_, _) => LayoutContent();
        LayoutContent();
        return host;
    }

    internal void OpenRegistration()
    {
        try { _openWebsite(RegistrationUri); }
        catch { ShowError("Tarayıcı açılamadı. Kayıt olmak için mtkaya.me sayfasını açabilirsin."); }
    }

    private int Scale(int value) => Math.Max(1, (int)Math.Round(value * DeviceDpi / 96f));

    private static RoundedPanel InputFrame(TextEdit edit)
    {
        var frame = new RoundedPanel { FillColor = Theme.Canvas, BorderColor = Theme.Divider, CornerRadius = 12 };
        frame.Controls.Add(edit);
        void LayoutEditor()
        {
            var inset = Math.Max(1, (int)Math.Round(14 * frame.DeviceDpi / 96f));
            var textHeight = edit.Properties.Appearance.Font.Height;
            edit.SetBounds(inset, Math.Max(0, (frame.Height - textHeight - inset) / 2),
                Math.Max(1, frame.Width - inset * 2), textHeight + inset);
        }
        frame.Resize += (_, _) => LayoutEditor();
        edit.Enter += (_, _) => frame.BorderColor = Theme.Accent;
        edit.Leave += (_, _) => frame.BorderColor = Theme.Divider;
        return frame;
    }

    private static void StyleInput(TextEdit edit, string placeholder)
    {
        edit.Properties.AutoHeight = false;
        edit.Properties.NullValuePrompt = placeholder;
        edit.Properties.Appearance.BackColor = Theme.Canvas;
        edit.Properties.Appearance.ForeColor = Theme.Text;
        edit.Properties.Appearance.Font = Theme.Font(10f);
        edit.Properties.Appearance.Options.UseBackColor = true;
        edit.Properties.Appearance.Options.UseForeColor = true;
        edit.Properties.Appearance.Options.UseFont = true;
        edit.Properties.BorderStyle = BorderStyles.NoBorder;
    }

    public void ShowError(string message)
    {
        _error.Text = message;
        SetBusy(false);
    }

    public void SetBusy(bool busy)
    {
        _login.Enabled = !busy;
        _login.Text = busy ? "Bağlanıyor..." : "Giriş yap  ↗";
    }

    private sealed class LoginSurface : Panel
    {
        internal LoginSurface()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            // The auth card samples the same window-anchored mountain scene as chat, not a
            // differently stretched panel image. A veil keeps credential labels high-contrast.
            ChatWallpaper.Draw(e.Graphics, this);
            using var veil = new SolidBrush(Color.FromArgb(105, 7, 11, 31));
            e.Graphics.FillRectangle(veil, ClientRectangle);
        }
    }

    private sealed class LoginArtwork : Control
    {
        public LoginArtwork()
        {
            Dock = DockStyle.Fill;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            AccessibleRole = AccessibleRole.StaticText;
            AccessibleName = "MTK Chat";
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            ChatWallpaper.Draw(e.Graphics, this);
            using var veil = new SolidBrush(Color.FromArgb(55, 7, 11, 31));
            e.Graphics.FillRectangle(veil, ClientRectangle);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (Width < 2 || Height < 2) return;
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var scale = DeviceDpi / 96f;
            int S(float value) => (int)Math.Round(value * scale);
            using var font = Theme.Font(25, FontStyle.Bold);
            const TextFormatFlags flags = TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix |
                TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter;
            var mtkWidth = TextRenderer.MeasureText(g, "MTK", font, new Size(int.MaxValue, Height), flags).Width;
            var chatWidth = TextRenderer.MeasureText(g, "Chat", font, new Size(int.MaxValue, Height), flags).Width;
            var brandWidth = S(64 + 18 + 9) + mtkWidth + chatWidth;
            var left = Math.Max(S(24), (Width - brandWidth) / 2);
            var top = (int)(Height * .29f);
            var bubble = new Rectangle(left, top, S(64), S(44));
            using var shape = new GraphicsPath();
            var diameter = S(23);
            shape.AddArc(bubble.Left, bubble.Top, diameter, diameter, 180, 90);
            shape.AddArc(bubble.Right - diameter, bubble.Top, diameter, diameter, 270, 90);
            shape.AddArc(bubble.Right - diameter, bubble.Bottom - diameter, diameter, diameter, 0, 90);
            shape.AddLine(bubble.Right - S(11), bubble.Bottom, bubble.Left + S(30), bubble.Bottom);
            shape.AddLine(bubble.Left + S(30), bubble.Bottom, bubble.Left + S(12), bubble.Bottom + S(10));
            shape.AddLine(bubble.Left + S(12), bubble.Bottom + S(10), bubble.Left + S(16), bubble.Bottom);
            shape.AddArc(bubble.Left, bubble.Bottom - diameter, diameter, diameter, 90, 90);
            shape.CloseFigure();
            using var fill = new LinearGradientBrush(new Rectangle(bubble.Left, bubble.Top, bubble.Width, bubble.Height + S(10)),
                Color.FromArgb(75, 145, 255), Color.FromArgb(120, 50, 255), LinearGradientMode.Vertical);
            g.FillPath(fill, shape);
            using var edge = new Pen(Color.FromArgb(125, 145, 199, 255));
            g.DrawPath(edge, shape);
            using var dot = new SolidBrush(Color.FromArgb(248, 248, 255));
            for (var index = 0; index < 3; index++)
            {
                var x = bubble.Left + S(17 + index * 14);
                var y = bubble.Top + S(22);
                var radius = S(index == 1 ? 4 : 3.3f);
                g.FillPolygon(dot, [new Point(x, y - radius), new Point(x + radius, y), new Point(x, y + radius), new Point(x - radius, y)]);
            }
            var textLeft = bubble.Right + S(18);
            var textBounds = new Rectangle(textLeft, top - S(3), mtkWidth + S(2), S(53));
            TextRenderer.DrawText(g, "MTK", font, textBounds, Theme.Text, flags);
            TextRenderer.DrawText(g, "Chat", font,
                new Rectangle(textLeft + mtkWidth + S(9), textBounds.Top, chatWidth + S(2), textBounds.Height),
                Color.FromArgb(144, 118, 255), flags);
            using var divider = new Pen(Color.FromArgb(45, 110, 120, 179));
            g.DrawLine(divider, Width - 1, 0, Width - 1, Height);
        }
    }
}
