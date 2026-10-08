using System.Runtime.InteropServices;
using System.Drawing.Drawing2D;

namespace MTKChat.Desktop;

// A real caption strip in the app palette, including Windows 10 where caption colors vary by theme.
internal class ModernForm : Form
{
    protected override void OnControlAdded(ControlEventArgs e)
    {
        base.OnControlAdded(e);
        if (e.Control is { } control) EditorMenuTheme.Attach(control);
    }

    private const int WmNcCalcSize = 0x83, WmNcHitTest = 0x84, WmNcPaint = 0x85, WmNcActivate = 0x86;
    private const int WsCaption = 0x00C00000, WsSysMenu = 0x00080000;
    private const int WsMinimizeBox = 0x00020000, WsMaximizeBox = 0x00010000, WsThickFrame = 0x00040000;
    private const int CaptionHeight = 32;
    private readonly Panel _caption = new CaptionPanel { BackColor = Theme.Rail, Height = CaptionHeight };
    private readonly Label _captionText = new() { BackColor = Color.Transparent, ForeColor = Theme.Muted, Font = Theme.Font(9), TextAlign = ContentAlignment.MiddleLeft };
    private readonly CaptionMark _captionMark = new();
    private readonly CaptionButton _close = new(CaptionButtonKind.Close);
    private readonly CaptionButton _maximize = new(CaptionButtonKind.Maximize);
    private readonly CaptionButton _minimize = new(CaptionButtonKind.Minimize);
    private readonly ToolTip _captionTips = new() { InitialDelay = 300, ReshowDelay = 100 };
    private bool _captionReady;
    private bool _layingOutCaption;

    public ModernForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        Padding = new Padding(1, CaptionHeight + 1, 1, 1);
        _caption.Controls.Add(_captionMark);
        _caption.Controls.Add(_captionText);
        _close.AccessibleName = "Kapat";
        _minimize.AccessibleName = "Simge durumuna küçült";
        _maximize.AccessibleName = "Büyüt veya geri yükle";
        _captionTips.SetToolTip(_close, _close.AccessibleName);
        _captionTips.SetToolTip(_minimize, _minimize.AccessibleName);
        _captionTips.SetToolTip(_maximize, _maximize.AccessibleName);
        _close.Click += (_, _) => Close();
        _minimize.Click += (_, _) => { if (MinimizeBox) WindowState = FormWindowState.Minimized; };
        _maximize.Click += (_, _) => ToggleMaximize();
        _caption.Controls.AddRange([_close, _maximize, _minimize]);
        void Drag(object? sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            if (e.Clicks == 2) { ToggleMaximize(); return; }
            ReleaseCapture();
            SendMessage(Handle, 0xA1, 2, 0); // Windows caption drag retains snapping behavior.
        }
        _caption.MouseDown += Drag;
        _captionText.MouseDown += Drag;
        _captionMark.MouseDown += Drag;
        _caption.Paint += (_, e) =>
        {
            using var pen = new Pen(Color.FromArgb(100, Theme.Divider));
            e.Graphics.DrawLine(pen, 0, _caption.Height - 1, _caption.Width, _caption.Height - 1);
        };
        Controls.Add(_caption);
        _captionReady = true;
        Resize += (_, _) => LayoutCaption();
        Load += (_, _) => LayoutCaption();
        Shown += (_, _) => LayoutCaption();
        DpiChanged += (_, _) => LayoutCaption();
        TextChanged += (_, _) => _captionText.Text = Text;
        Activated += (_, _) => { _captionText.ForeColor = Theme.Text; _caption.Invalidate(true); Invalidate(); };
        Deactivate += (_, _) =>
        {
            _captionText.ForeColor = Theme.Muted;
            foreach (var button in new[] { _close, _maximize, _minimize }) button.ClearInteraction();
            _caption.Invalidate(true);
            Invalidate();
        };
        LayoutCaption();
    }

    protected override void OnStyleChanged(EventArgs e)
    {
        base.OnStyleChanged(e);
        // MaximizeBox/MinimizeBox can change after the base constructor, without a size
        // change (Login and modal dialogs do this). Clear old button positions immediately.
        LayoutCaption();
    }

    private void LayoutCaption()
    {
        if (!_captionReady || _layingOutCaption || IsDisposed) return;
        _layingOutCaption = true;
        try
        {
            var geometry = CalculateCaptionGeometry(ClientSize.Width, DeviceDpi, MaximizeBox, MinimizeBox);
            if (Padding != geometry.Padding) Padding = geometry.Padding;
            _caption.Bounds = geometry.Strip;
            _close.Bounds = geometry.Close;
            _maximize.Visible = MaximizeBox;
            _maximize.Bounds = geometry.Maximize;
            _minimize.Visible = MinimizeBox;
            _minimize.Bounds = geometry.Minimize;
            _captionMark.Bounds = geometry.Mark;
            _captionText.Bounds = geometry.Title;
            _caption.BringToFront();
            UpdateWindowShape();
            // Applying a window region can synchronously dispatch native size/state
            // messages. Paint the final state, not the one read before that update.
            _maximize.RestoreGlyph = WindowState == FormWindowState.Maximized;
            // Repaint the whole strip, including space vacated by a hidden/moved control.
            // This prevents a previous close/restore glyph from remaining under a new one.
            _caption.Invalidate(true);
        }
        finally { _layingOutCaption = false; }
    }

    private sealed record CaptionGeometry(Rectangle Strip, Rectangle Close, Rectangle Maximize,
        Rectangle Minimize, Rectangle Mark, Rectangle Title, Padding Padding);

    private static CaptionGeometry CalculateCaptionGeometry(int clientWidth, int dpi, bool canMaximize, bool canMinimize)
    {
        int S(int value) => Math.Max(1, (int)Math.Round(value * Math.Max(1, dpi) / 96f));
        var border = S(1);
        var height = S(CaptionHeight);
        var strip = new Rectangle(border, border, Math.Max(0, clientWidth - border * 2), height);
        var right = strip.Width - S(4);
        Rectangle NextButton()
        {
            var bounds = new Rectangle(Math.Max(0, right - S(42)), S(3), S(42), height - S(6));
            right = bounds.Left - S(2);
            return bounds;
        }
        var close = NextButton();
        var maximize = canMaximize ? NextButton() : Rectangle.Empty;
        var minimize = canMinimize ? NextButton() : Rectangle.Empty;
        return new CaptionGeometry(strip, close, maximize, minimize,
            new Rectangle(S(14), S(9), S(14), S(14)),
            new Rectangle(S(38), 0, Math.Max(0, right - S(50)), height),
            new Padding(border, height + border, border, border));
    }

    // App-owned structural probe: no injected mouse/keyboard input and no live account calls.
    internal IReadOnlyList<string> VerifyCaptionLayout()
    {
        LayoutCaption();
        var checks = new List<string>();
        void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Caption: " + message);
            checks.Add(message);
        }
        foreach (var dpi in new[] { 96, 120, 144, 192 })
        foreach (var canMaximize in new[] { false, true })
        foreach (var canMinimize in new[] { false, true })
        {
            var geometry = CalculateCaptionGeometry(1120 * dpi / 96, dpi, canMaximize, canMinimize);
            var buttons = new[] { geometry.Close, geometry.Maximize, geometry.Minimize }.Where(bounds => !bounds.IsEmpty).ToArray();
            var client = new Rectangle(Point.Empty, geometry.Strip.Size);
            Require(buttons.All(client.Contains), $"{dpi} DPI {canMaximize}/{canMinimize}: buttons stay inside caption");
            Require(buttons.SelectMany((bounds, index) => buttons.Skip(index + 1).Select(other => !bounds.IntersectsWith(other))).All(value => value),
                $"{dpi} DPI {canMaximize}/{canMinimize}: button rectangles do not overlap");
            Require(geometry.Maximize.IsEmpty == !canMaximize && geometry.Minimize.IsEmpty == !canMinimize,
                $"{dpi} DPI {canMaximize}/{canMinimize}: unavailable capabilities have no paint bounds");
            Require(buttons.All(bounds => !bounds.IntersectsWith(geometry.Title) && !bounds.IntersectsWith(geometry.Mark)),
                $"{dpi} DPI {canMaximize}/{canMinimize}: title/brand never enter action bounds");
        }
        var expected = CalculateCaptionGeometry(ClientSize.Width, DeviceDpi, MaximizeBox, MinimizeBox);
        Require(_caption.Bounds == expected.Strip && _close.Bounds == expected.Close && _maximize.Bounds == expected.Maximize && _minimize.Bounds == expected.Minimize,
            "live control bounds match deterministic DPI geometry");
        Require(!Visible || (_close.Visible && _maximize.Visible == MaximizeBox && _minimize.Visible == MinimizeBox),
            "live visible buttons match form capabilities");
        Require(_maximize.RestoreGlyph == (WindowState == FormWindowState.Maximized), "maximize/restore vector follows window state");
        Require(_caption.Controls.OfType<CaptionButton>().Count() == 3 && _caption.Controls.OfType<CaptionButton>().All(button => button.Text.Length == 0),
            "exactly one vector control per action; no native/text glyph layer");
        Require((CreateParams.Style & WsCaption) == 0, "native caption style remains disabled");
        return checks;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _captionTips.Dispose();
        base.Dispose(disposing);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (WindowState == FormWindowState.Maximized || ClientSize.Width < 2 || ClientSize.Height < 2) return;
        // A single restrained frame replaces the native white edge without changing resize hit tests.
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var shape = DrawingExtensions.RoundedRectangle(new Rectangle(0, 0, ClientSize.Width - 1, ClientSize.Height - 1), Math.Max(8, DeviceDpi * 12 / 96));
        using var frame = new Pen(Color.FromArgb(144, 112, 140, 247), Math.Max(1, DeviceDpi / 96f)) { Alignment = PenAlignment.Inset };
        e.Graphics.DrawPath(frame, shape);
    }

    private void UpdateWindowShape()
    {
        if (ClientSize.Width < 2 || ClientSize.Height < 2) return;
        var previous = Region;
        if (WindowState == FormWindowState.Maximized) Region = null;
        else
        {
            using var shape = DrawingExtensions.RoundedRectangle(ClientRectangle, Math.Max(8, DeviceDpi * 12 / 96));
            Region = new Region(shape);
        }
        previous?.Dispose();
    }

    private sealed class CaptionPanel : Panel
    {
        internal CaptionPanel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        }
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            if (Width < 2 || Height < 2) return;
            PaintSurface(e.Graphics, ClientRectangle, ClientRectangle);
        }

        internal static void PaintSurface(Graphics graphics, Rectangle gradientBounds, Rectangle paintBounds)
        {
            if (gradientBounds.Width < 2 || gradientBounds.Height < 2) return;
            using var gradient = new LinearGradientBrush(gradientBounds,
                Color.FromArgb(31, 38, 78), Color.FromArgb(24, 24, 46), LinearGradientMode.Horizontal);
            graphics.FillRectangle(gradient, paintBounds);
        }
    }

    private enum CaptionButtonKind { Close, Maximize, Minimize }

    // Caption glyphs are vectors, not MDL2 text or a native button overlay. Each state
    // paints an opaque copy of the strip first; repeated move/hover/restore paints
    // cannot retain the previous button's glyph or red hover rectangle.
    private sealed class CaptionButton : Button
    {
        private readonly CaptionButtonKind _kind;
        private bool _hovered;
        private bool _pressed;
        private bool _restoreGlyph;

        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        internal bool RestoreGlyph
        {
            get => _restoreGlyph;
            set { if (_restoreGlyph == value) return; _restoreGlyph = value; Invalidate(); }
        }

        internal CaptionButton(CaptionButtonKind kind)
        {
            _kind = kind;
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            UseVisualStyleBackColor = false;
            TabStop = false;
            Text = string.Empty;
            Cursor = Cursors.Default;
            AccessibleRole = AccessibleRole.PushButton;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Opaque, true);
        }

        internal void ClearInteraction()
        {
            _hovered = false;
            _pressed = false;
            Invalidate();
        }

        protected override void OnMouseEnter(EventArgs e) { _hovered = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hovered = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) _pressed = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { _pressed = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnMouseCaptureChanged(EventArgs e) { if (!Capture) _pressed = false; Invalidate(); base.OnMouseCaptureChanged(e); }
        protected override void OnEnabledChanged(EventArgs e) { if (!Enabled) _pressed = false; Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnPaintBackground(PaintEventArgs e) { }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (Width < 2 || Height < 2) return;
            var g = e.Graphics;
            var parentSize = Parent?.ClientSize ?? ClientSize;
            CaptionPanel.PaintSurface(g, new Rectangle(-Left, -Top, parentSize.Width, parentSize.Height), ClientRectangle);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var scale = DeviceDpi / 96f;
            if (_hovered || _pressed)
            {
                using var surface = DrawingExtensions.RoundedRectangle(new Rectangle(1, 1, Width - 2, Height - 2), Math.Max(1, (int)Math.Round(5 * scale)));
                var color = _kind == CaptionButtonKind.Close ? Theme.Danger : Color.FromArgb(60, 66, 103);
                if (_pressed) color = ControlPaint.Dark(color, .12f);
                using var fill = new SolidBrush(color);
                g.FillPath(fill, surface);
            }
            var x = Width / 2f;
            var y = Height / 2f + (_pressed ? scale : 0);
            using var pen = new Pen(Enabled ? Theme.Text : Theme.Muted, Math.Max(1, 1.15f * scale))
            { StartCap = LineCap.Square, EndCap = LineCap.Square, LineJoin = LineJoin.Miter };
            switch (_kind)
            {
                case CaptionButtonKind.Close:
                    g.DrawLine(pen, x - 4 * scale, y - 4 * scale, x + 4 * scale, y + 4 * scale);
                    g.DrawLine(pen, x + 4 * scale, y - 4 * scale, x - 4 * scale, y + 4 * scale);
                    break;
                case CaptionButtonKind.Minimize:
                    g.DrawLine(pen, x - 4.5f * scale, y + 2 * scale, x + 4.5f * scale, y + 2 * scale);
                    break;
                default:
                    if (RestoreGlyph)
                    {
                        g.DrawLines(pen, [new PointF(x - 1 * scale, y - 5 * scale), new PointF(x + 5 * scale, y - 5 * scale),
                            new PointF(x + 5 * scale, y + 1 * scale)]);
                        g.DrawRectangle(pen, x - 5 * scale, y - 1 * scale, 6 * scale, 6 * scale);
                    }
                    else g.DrawRectangle(pen, x - 4.5f * scale, y - 4.5f * scale, 9 * scale, 9 * scale);
                    break;
            }
        }
    }

    private sealed class CaptionMark : Control
    {
        internal CaptionMark()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            AccessibleName = "MTK Chat";
            TabStop = false;
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            if (Parent is null) { e.Graphics.Clear(Theme.Rail); return; }
            var state = e.Graphics.Save();
            e.Graphics.TranslateTransform(-Left, -Top);
            InvokePaintBackground(Parent, new PaintEventArgs(e.Graphics, Bounds));
            e.Graphics.Restore(state);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            if (Width < 3 || Height < 3) return;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var bounds = new Rectangle(1, 1, Width - 2, Height - 2);
            using var gradient = new LinearGradientBrush(bounds, Theme.Accent, Theme.GradientEnd, LinearGradientMode.ForwardDiagonal);
            e.Graphics.FillEllipse(gradient, bounds);
            using var border = new Pen(Color.FromArgb(110, Theme.Violet));
            e.Graphics.DrawEllipse(border, bounds);
            using var glint = new SolidBrush(Color.FromArgb(205, Theme.Cyan));
            var edge = Math.Max(2, Width * .2f);
            e.Graphics.FillEllipse(glint, Width * .23f, Height * .23f, edge, edge);
        }
    }
    private void ToggleMaximize()
    {
        if (!MaximizeBox) return;
        MaximizedBounds = Screen.FromControl(this).WorkingArea;
        WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized;
    }
    protected void WrapFixedContent()
    {
        var children = Controls.Cast<Control>().Where(c => c != _caption).ToArray();
        var host = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Canvas };
        foreach (var child in children) host.Controls.Add(child);
        ClientSize = new Size(ClientSize.Width, ClientSize.Height + Padding.Vertical);
        Controls.Add(host);
        _caption.BringToFront();
    }
    protected override CreateParams CreateParams
    {
        get
        {
            var p = base.CreateParams;
            // One owner for the frame: DevExpress editors stay in the client area, while this form
            // owns the caption. Never inherit native caption/border bits when handles are recreated.
            p.Style &= ~(WsCaption | WsMinimizeBox | WsMaximizeBox | WsThickFrame);
            p.Style |= WsSysMenu;
            if (MinimizeBox) p.Style |= WsMinimizeBox;
            if (MaximizeBox) p.Style |= WsMaximizeBox | WsThickFrame;
            return p;
        }
    }
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // NCCALCSIZE also needs to run at initial creation, before the first visible paint.
        // Otherwise Windows can retain the old non-client strip until a resize occurs.
        SetWindowPos(Handle, 0, 0, 0, 0, 0, 0x0027); // FRAMECHANGED | NOMOVE | NOSIZE | NOZORDER
        LayoutCaption();
    }
    protected override void WndProc(ref Message m)
    {
        if (m.Msg is WmNcCalcSize or WmNcPaint) { m.Result = 0; return; }
        // DefWindowProc can paint the classic active/inactive caption even with our client caption.
        // Keep activation normal, but leave non-client painting entirely to the custom form.
        if (m.Msg == WmNcActivate) { m.Result = 1; return; }
        base.WndProc(ref m);
        if (m.Msg == WmNcHitTest && MaximizeBox && WindowState == FormWindowState.Normal)
        {
            var p = PointToClient(new Point(unchecked((short)(long)m.LParam), unchecked((short)((long)m.LParam >> 16))));
            var edge = Math.Max(5, DeviceDpi * 6 / 96);
            var left = p.X < edge; var right = p.X >= ClientSize.Width - edge;
            var top = p.Y < edge; var bottom = p.Y >= ClientSize.Height - edge;
            if (left || right || top || bottom) m.Result = top ? left ? 13 : right ? 14 : 12 : bottom ? left ? 16 : right ? 17 : 15 : left ? 10 : 11;
        }
    }
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern nint SendMessage(nint handle, int message, nint wParam, nint lParam);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint handle, nint insertAfter, int x, int y, int width, int height, uint flags);
}
