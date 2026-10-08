using System.Drawing.Drawing2D;
using System.Diagnostics.CodeAnalysis;
using System.ComponentModel;

namespace MTKChat.Desktop;

internal enum ButtonKind { Primary, Secondary, Ghost, Danger }
internal enum ModernButtonIcon { None, Send, Smile, Attachment, Phone, Participants, More, Close, Search, Settings, Profile, Back, Chat, Shield, Status, Microphone, MicrophoneOff, HangUp, Speaker, SpeakerOff }

internal static class DrawingExtensions
{
    public static GraphicsPath RoundedRectangle(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        if (bounds.Width <= 0 || bounds.Height <= 0) return path;
        var diameter = Math.Min(Math.Max(2, radius * 2), Math.Min(bounds.Width, bounds.Height));
        if (radius <= 0 || diameter < 2)
        {
            path.AddRectangle(bounds);
            return path;
        }
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}

internal class RoundedPanel : Panel
{
    private Color _fillColor = Theme.Surface;
    private Color _borderColor = Color.Transparent;
    private Color _gradientEndColor = Color.Empty;
    private bool _accentGlow;
    private int _borderWidth = 1;
    private int _cornerRadius = 16;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal Color FillColor { get => _fillColor; set { _fillColor = value; Invalidate(); } }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal Color BorderColor { get => _borderColor; set { _borderColor = value; Invalidate(); } }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal Color GradientEndColor { get => _gradientEndColor; set { _gradientEndColor = value; Invalidate(); } }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal bool AccentGlow { get => _accentGlow; set { _accentGlow = value; Invalidate(); } }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal int CornerRadius { get => _cornerRadius; set { _cornerRadius = value; UpdateRegion(); Invalidate(); } }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal int BorderWidth { get => _borderWidth; set { _borderWidth = Math.Max(0, value); Invalidate(); } }

    public RoundedPanel()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Color.Transparent;
    }

    protected override void OnResize(EventArgs eventargs)
    {
        base.OnResize(eventargs);
        UpdateRegion();
    }

    private void UpdateRegion()
    {
        if (Width <= 0 || Height <= 0) return;
        using var path = DrawingExtensions.RoundedRectangle(new Rectangle(0, 0, Width, Height), Math.Min(ScaledRadius, Math.Min(Width, Height) / 2));
        var previous = Region;
        Region = new Region(path);
        previous?.Dispose();
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        if (Width < 2 || Height < 2) return;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        // Even an opaque fill has partially covered antialiased edge pixels.
        // The native Region is the outside boundary, while the inset fill/border
        // path also leaves its last row/column to the base. Restore that base for
        // every frame; otherwise opaque cards retain black or previous-frame
        // fringes that a fresh DrawToBitmap capture can conceal.
        if (Parent is null) ChatWallpaper.Draw(e.Graphics, this);
        else
        {
            var state = e.Graphics.Save();
            e.Graphics.TranslateTransform(-Left, -Top);
            InvokePaintBackground(Parent, new PaintEventArgs(e.Graphics, Bounds));
            e.Graphics.Restore(state);
        }
        var bounds = new Rectangle(0, 0, Width - 1, Height - 1);
        using var path = DrawingExtensions.RoundedRectangle(bounds, Math.Min(ScaledRadius, Math.Min(Width, Height) / 2));
        using Brush brush = GradientEndColor.IsEmpty
            ? new SolidBrush(FillColor)
            : new LinearGradientBrush(bounds, FillColor, GradientEndColor, LinearGradientMode.ForwardDiagonal);
        e.Graphics.FillPath(brush, path);
        if (AccentGlow)
        {
            // Keep the accent inside the card instead of drawing a shadow across neighboring controls.
            var state = e.Graphics.Save();
            e.Graphics.SetClip(path, CombineMode.Intersect);
            using var glow = new LinearGradientBrush(bounds, Color.FromArgb(32, Theme.Accent), Color.Transparent, LinearGradientMode.ForwardDiagonal);
            e.Graphics.FillRectangle(glow, bounds);
            e.Graphics.Restore(state);
        }
        if (BorderColor != Color.Transparent && BorderWidth > 0)
        {
            using var pen = new Pen(BorderColor, Math.Max(1, BorderWidth * DeviceDpi / 96f)) { Alignment = PenAlignment.Inset };
            e.Graphics.DrawPath(pen, path);
        }
    }

    private int ScaledRadius => Math.Max(0, (int)Math.Round(CornerRadius * DeviceDpi / 96f));
}

internal sealed class ModernButton : Button
{
    private bool _hovered;
    private bool _pressed;
    private ButtonKind _kind;
    private int _cornerRadius = 12;
    private ModernButtonIcon _vectorIcon;
    private bool _circularSurface;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal int CornerRadius { get => _cornerRadius; set { _cornerRadius = Math.Max(0, value); Invalidate(); } }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal ButtonKind Kind { get => _kind; set { _kind = value; Invalidate(); } }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal Color HoverColor { get; set; } = Color.Empty;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal ModernButtonIcon VectorIcon { get => _vectorIcon; set { _vectorIcon = value; Invalidate(); } }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal bool CircularSurface { get => _circularSurface; set { _circularSurface = value; Invalidate(); } }

    public ModernButton()
    {
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        UseVisualStyleBackColor = false;
        TabStop = true;
        SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.AllPaintingInWmPaint, true);
    }

    protected override void OnMouseEnter(EventArgs e) { _hovered = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hovered = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) _pressed = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _pressed = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnMouseCaptureChanged(EventArgs e) { if (!Capture) _pressed = false; Invalidate(); base.OnMouseCaptureChanged(e); }
    protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode is Keys.Space or Keys.Enter) _pressed = true; Invalidate(); base.OnKeyDown(e); }
    protected override void OnKeyUp(KeyEventArgs e) { _pressed = false; Invalidate(); base.OnKeyUp(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { _pressed = false; Invalidate(); base.OnLostFocus(e); }
    protected override void OnEnabledChanged(EventArgs e) { if (!Enabled) _pressed = false; Invalidate(); base.OnEnabledChanged(e); }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        PaintParentSurface(e.Graphics);
    }

    private void PaintParentSurface(Graphics graphics)
    {
        if (Parent is null) { graphics.Clear(Theme.Canvas); return; }
        // Sample the actual parent paint, including its gradient. A flat fallback left visible
        // rectangles around small controls inside gradient cards when hover/focus repainted them.
        var state = graphics.Save();
        graphics.TranslateTransform(-Left, -Top);
        InvokePaintBackground(Parent, new PaintEventArgs(graphics, Bounds));
        graphics.Restore(state);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (Width < 2 || Height < 2) return;
        // Flat ButtonBase inherits Opaque: normal WM_PAINT may skip OnPaintBackground.
        // Always restore the parent surface before drawing this frame. Otherwise old
        // glyphs/text/rounded corners survive text, hover, focus or Kind changes.
        PaintParentSurface(e.Graphics);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var background = Kind switch
        {
            ButtonKind.Primary => _hovered ? Theme.AccentHover : Theme.Accent,
            ButtonKind.Danger => Color.FromArgb(_hovered ? 84 : 50, Theme.Danger),
            ButtonKind.Ghost => _hovered ? Theme.SurfaceRaised : Color.Transparent,
            _ => _hovered ? Theme.SurfaceHover : Theme.SurfaceRaised
        };
        if (_hovered && !HoverColor.IsEmpty) background = HoverColor;
        if (_pressed && background.A > 0) background = ControlPaint.Dark(background, .08f);
        if (!Enabled) background = Kind == ButtonKind.Ghost ? Color.Transparent : Theme.Surface;
        var foreground = !Enabled ? Theme.Muted : Kind switch
        {
            ButtonKind.Primary => Theme.AccentInk,
            ButtonKind.Danger => Theme.Danger,
            _ => ForeColor.IsEmpty || ForeColor == SystemColors.ControlText ? Theme.Text : ForeColor
        };
        if (_hovered && HoverColor == Theme.Danger) foreground = Color.White;
        var bounds = new Rectangle(0, 0, Width - 1, Height - 1);
        var radius = Math.Min((int)Math.Round(CornerRadius * DeviceDpi / 96f), Math.Min(Width, Height) / 2);
        // The composer row is not necessarily square (drafts, DPI rounding and
        // layout margins change its height). A maximum corner radius still
        // produces a capsule in that rectangle. The send affordance is a true
        // circle centered in the actual client, independently of its container.
        // Shape is a control style, not icon content: removing/swapping ink must
        // not change the silhouette or leave a second edge on a reused frame.
        var circular = CircularSurface;
        var surfaceBounds = circular ? CenteredCircleBounds(ClientSize) : bounds;
        using var path = circular ? new GraphicsPath() : DrawingExtensions.RoundedRectangle(bounds, radius);
        if (circular) path.AddEllipse(surfaceBounds);
        if (Kind == ButtonKind.Primary && Enabled)
        {
            var end = _hovered ? ControlPaint.Light(Theme.GradientEnd, .05f) : Theme.GradientEnd;
            if (_pressed) end = ControlPaint.Dark(end, .08f);
            using var gradient = new LinearGradientBrush(surfaceBounds, background, end, LinearGradientMode.Horizontal);
            e.Graphics.FillPath(gradient, path);
            // The translucent exterior stroke overlapped the antialiased fill edge,
            // creating a second pale rim on small round buttons. Keep one clean edge;
            // keyboard focus still has its distinct inset cue below.
        }
        else if (background.A > 0)
        {
            var secondaryTop = _hovered ? Color.FromArgb(42, 47, 80) : Color.FromArgb(30, 36, 62);
            var secondaryBottom = _hovered ? Color.FromArgb(31, 37, 65) : Color.FromArgb(20, 26, 47);
            if (_pressed) { secondaryTop = ControlPaint.Dark(secondaryTop, .08f); secondaryBottom = ControlPaint.Dark(secondaryBottom, .08f); }
            if (!Enabled) secondaryTop = secondaryBottom = Theme.Surface;
            using Brush brush = Kind == ButtonKind.Secondary
                ? new LinearGradientBrush(bounds, secondaryTop, secondaryBottom, LinearGradientMode.Vertical)
                : new SolidBrush(background);
            e.Graphics.FillPath(brush, path);
            if (Kind == ButtonKind.Secondary)
            {
                using var border = new Pen(_hovered ? Color.FromArgb(115, 114, 195) : Color.FromArgb(61, 69, 107)) { Alignment = PenAlignment.Inset };
                e.Graphics.DrawPath(border, path);
            }
        }
        if (Focused && ShowFocusCues && Enabled)
        {
            var inset = Math.Max(2, DeviceDpi * 3 / 96);
            var focusBounds = RectangleF.Inflate(surfaceBounds, -inset, -inset);
            using var focusPath = circular ? new GraphicsPath() :
                DrawingExtensions.RoundedRectangle(Rectangle.Round(focusBounds), Math.Max(0, radius - inset));
            if (circular && focusBounds.Width > 0 && focusBounds.Height > 0) focusPath.AddEllipse(focusBounds);
            using var focusPen = new Pen(Kind == ButtonKind.Primary ? Color.FromArgb(160, Color.White) : Theme.Accent);
            e.Graphics.DrawPath(focusPen, focusPath);
        }
        var textBounds = new Rectangle(Padding.Left, Padding.Top, Math.Max(0, Width - Padding.Horizontal), Math.Max(0, Height - Padding.Vertical));
        if (_pressed) textBounds.Offset(0, Math.Max(1, DeviceDpi / 96));
        if (VectorIcon != ModernButtonIcon.None)
            PaintVectorIcon(e.Graphics, VectorIcon, textBounds, foreground);
        else
            TextRenderer.DrawText(e.Graphics, Text, Font, textBounds, foreground,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
    }

    internal static RectangleF CenteredCircleBounds(Size client)
    {
        // Half-pixel breathing room keeps the anti-aliased outer edge inside
        // WM_PAINT's clipping rectangle on every side; neither edge is cut off.
        var diameter = Math.Max(0, Math.Min(client.Width, client.Height) - 1f);
        return new RectangleF((client.Width - diameter) / 2f, (client.Height - diameter) / 2f, diameter, diameter);
    }

    private static void PaintVectorIcon(Graphics graphics, ModernButtonIcon icon, Rectangle bounds, Color foreground)
    {
        if (bounds.Width < 4 || bounds.Height < 4) return;
        // Small toolbar glyphs used to go through GDI text measurement with
        // EndEllipsis. A point font can outgrow its fixed-height slot at 125/150%
        // DPI, so even a single smile/paperclip turned into a glyph plus dots.
        // Scale vectors from actual client pixels: no font, ellipsis or double DPI.
        var edge = Math.Min(bounds.Width, bounds.Height) * (icon == ModernButtonIcon.Send ? .43f : .56f);
        var scale = edge / 24f;
        var left = bounds.Left + (bounds.Width - edge) / 2f;
        var top = bounds.Top + (bounds.Height - edge) / 2f;
        PointF Point(float x, float y) => new(left + x * scale, top + y * scale);
        using var pen = new Pen(foreground, Math.Max(1.2f, edge / 15f))
        { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
        using var ink = new SolidBrush(foreground);
        using var shape = new GraphicsPath();
        void Ellipse(float x, float y, float width, float height, bool filled = false)
        {
            var origin = Point(x, y);
            var rectangle = new RectangleF(origin.X, origin.Y, width * scale, height * scale);
            if (filled) graphics.FillEllipse(ink, rectangle); else graphics.DrawEllipse(pen, rectangle);
        }
        switch (icon)
        {
            case ModernButtonIcon.Send:
                shape.AddPolygon([Point(2, 3), Point(22, 12), Point(2, 21), Point(6, 12)]);
                graphics.DrawPath(pen, shape);
                graphics.DrawLine(pen, Point(6, 12), Point(22, 12));
                break;
            case ModernButtonIcon.Smile:
                Ellipse(3, 3, 18, 18);
                Ellipse(7.25f, 8, 2, 2, true); Ellipse(14.75f, 8, 2, 2, true);
                graphics.DrawBezier(pen, Point(7, 14), Point(9, 18), Point(15, 18), Point(17, 14));
                break;
            case ModernButtonIcon.Attachment:
                shape.AddBezier(Point(8, 14), Point(8, 11), Point(8, 5), Point(12, 5));
                shape.AddBezier(Point(12, 5), Point(16, 5), Point(16, 8), Point(16, 11));
                shape.AddLine(Point(16, 11), Point(16, 16));
                shape.AddBezier(Point(16, 16), Point(16, 24), Point(5, 24), Point(5, 16));
                shape.AddLine(Point(5, 16), Point(5, 8));
                shape.AddBezier(Point(5, 8), Point(5, -1), Point(19, -1), Point(19, 8));
                shape.AddLine(Point(19, 8), Point(19, 16));
                graphics.DrawPath(pen, shape);
                break;
            case ModernButtonIcon.Phone:
                shape.AddLines([Point(5, 3), Point(8, 3), Point(10, 8), Point(7.5f, 10.5f)]);
                shape.AddBezier(Point(7.5f, 10.5f), Point(9, 13), Point(11, 15), Point(13.5f, 16.5f));
                shape.AddLines([Point(13.5f, 16.5f), Point(16, 14), Point(21, 16), Point(21, 19)]);
                shape.AddBezier(Point(21, 19), Point(21, 24), Point(13, 20), Point(9, 16));
                shape.AddBezier(Point(9, 16), Point(5, 12), Point(0, 5), Point(5, 3));
                graphics.DrawPath(pen, shape);
                break;
            case ModernButtonIcon.Microphone:
            case ModernButtonIcon.MicrophoneOff:
                shape.AddArc(Point(8, 2).X, Point(8, 2).Y, 8 * scale, 8 * scale, 180, 180);
                shape.AddLine(Point(16, 6), Point(16, 11));
                shape.AddArc(Point(8, 7).X, Point(8, 7).Y, 8 * scale, 8 * scale, 0, 180);
                shape.CloseFigure(); graphics.DrawPath(pen, shape);
                graphics.DrawArc(pen, new RectangleF(Point(5, 6), new SizeF(14 * scale, 13 * scale)), 0, 180);
                graphics.DrawLine(pen, Point(5, 9), Point(5, 12)); graphics.DrawLine(pen, Point(19, 9), Point(19, 12));
                graphics.DrawLine(pen, Point(12, 19), Point(12, 22)); graphics.DrawLine(pen, Point(8, 22), Point(16, 22));
                if (icon == ModernButtonIcon.MicrophoneOff) graphics.DrawLine(pen, Point(2, 2), Point(22, 22));
                break;
            case ModernButtonIcon.HangUp:
                shape.AddBezier(Point(2, 12), Point(6, 5), Point(18, 5), Point(22, 12));
                shape.AddLines([Point(22, 12), Point(19, 16), Point(15, 14), Point(15, 11)]);
                shape.AddBezier(Point(15, 11), Point(13, 10), Point(11, 10), Point(9, 11));
                shape.AddLines([Point(9, 11), Point(9, 14), Point(5, 16), Point(2, 12)]);
                graphics.DrawPath(pen, shape);
                break;
            case ModernButtonIcon.Speaker:
            case ModernButtonIcon.SpeakerOff:
                graphics.DrawPolygon(pen, [Point(3, 9), Point(7, 9), Point(12, 4), Point(12, 20), Point(7, 15), Point(3, 15)]);
                if (icon == ModernButtonIcon.Speaker)
                {
                    graphics.DrawArc(pen, new RectangleF(Point(11, 7), new SizeF(8 * scale, 10 * scale)), -65, 130);
                    graphics.DrawArc(pen, new RectangleF(Point(10, 3), new SizeF(13 * scale, 18 * scale)), -60, 120);
                }
                else { graphics.DrawLine(pen, Point(16, 8), Point(22, 16)); graphics.DrawLine(pen, Point(22, 8), Point(16, 16)); }
                break;
            case ModernButtonIcon.Participants:
                Ellipse(6, 3, 7, 7); Ellipse(15, 5, 5, 5);
                shape.AddBezier(Point(2, 21), Point(2, 10), Point(17, 10), Point(17, 21));
                shape.StartFigure();
                shape.AddBezier(Point(16, 13), Point(21, 13), Point(22, 16), Point(22, 20));
                graphics.DrawPath(pen, shape);
                break;
            case ModernButtonIcon.More:
                Ellipse(2, 10.5f, 3, 3, true); Ellipse(10.5f, 10.5f, 3, 3, true); Ellipse(19, 10.5f, 3, 3, true);
                break;
            case ModernButtonIcon.Close:
                graphics.DrawLine(pen, Point(5, 5), Point(19, 19));
                graphics.DrawLine(pen, Point(19, 5), Point(5, 19));
                break;
            case ModernButtonIcon.Search:
                Ellipse(3, 3, 13, 13);
                graphics.DrawLine(pen, Point(14.5f, 14.5f), Point(21, 21));
                break;
            case ModernButtonIcon.Settings:
                // Radial vector teeth keep the rail independent of installed
                // MDL2 font versions and consistent with the toolbar stroke.
                var teeth = new PointF[32];
                for (var index = 0; index < teeth.Length; index++)
                {
                    var angle = (index - .5f) * MathF.PI / 16f;
                    var radiusForTooth = index % 4 is 0 or 3 ? 10f : 7.5f;
                    teeth[index] = Point(12 + MathF.Cos(angle) * radiusForTooth,
                        12 + MathF.Sin(angle) * radiusForTooth);
                }
                shape.AddPolygon(teeth);
                graphics.DrawPath(pen, shape);
                Ellipse(8.25f, 8.25f, 7.5f, 7.5f);
                break;
            case ModernButtonIcon.Profile:
                Ellipse(8, 3, 8, 8);
                shape.AddBezier(Point(3, 21), Point(3, 10), Point(21, 10), Point(21, 21));
                graphics.DrawPath(pen, shape);
                break;
            case ModernButtonIcon.Back:
                graphics.DrawLines(pen, [Point(11, 4), Point(3, 12), Point(11, 20)]);
                graphics.DrawLine(pen, Point(3, 12), Point(21, 12));
                break;
            case ModernButtonIcon.Chat:
                shape.AddLines([Point(4, 3), Point(20, 3), Point(20, 17),
                    Point(10, 17), Point(4, 22), Point(4, 3)]);
                graphics.DrawPath(pen, shape);
                break;
            case ModernButtonIcon.Shield:
                shape.AddLines([Point(12, 2), Point(21, 6), Point(20, 13)]);
                shape.AddBezier(Point(20, 13), Point(19, 18), Point(15, 21), Point(12, 22));
                shape.AddBezier(Point(12, 22), Point(9, 21), Point(5, 18), Point(4, 13));
                shape.AddLines([Point(4, 13), Point(3, 6), Point(12, 2)]);
                graphics.DrawPath(pen, shape);
                graphics.DrawLines(pen, [Point(8, 12), Point(11, 15), Point(16, 9)]);
                break;
            case ModernButtonIcon.Status:
                // A segmented status ring is a vector, never a font glyph whose
                // fallback metrics can break on another Windows installation.
                var ring = new RectangleF(Point(3, 3), new SizeF(Point(18, 18).X - Point(0, 0).X, Point(18, 18).Y - Point(0, 0).Y));
                graphics.DrawArc(pen, ring, -84, 100); graphics.DrawArc(pen, ring, 36, 100); graphics.DrawArc(pen, ring, 156, 100);
                Ellipse(9, 9, 6, 6, true);
                break;
        }
    }
}

internal sealed class ModernTextBox : UserControl
{
    public TextBox Input { get; } = new();
    private bool _focused;
    private int _cornerRadius = 13;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal string PlaceholderText { get => Input.PlaceholderText; set => Input.PlaceholderText = value; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal bool UseSystemPasswordChar { get => Input.UseSystemPasswordChar; set => Input.UseSystemPasswordChar = value; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal bool Multiline
    {
        get => Input.Multiline;
        set
        {
            Input.Multiline = value;
            Input.AcceptsReturn = value;
            LayoutInput();
        }
    }
    [AllowNull]
    public override string Text { get => Input.Text; set => Input.Text = value ?? string.Empty; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal int CornerRadius { get => _cornerRadius; set { _cornerRadius = value; Invalidate(); } }

    public ModernTextBox()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Color.Transparent;
        Padding = new Padding(14, 8, 14, 8);
        Input.BorderStyle = BorderStyle.None;
        Input.BackColor = Theme.SurfaceRaised;
        Input.ForeColor = Theme.Text;
        Input.Font = Theme.Font(10f);
        Input.Dock = DockStyle.None;
        Input.GotFocus += (_, _) => { _focused = true; Invalidate(); };
        Input.LostFocus += (_, _) => { _focused = false; Invalidate(); };
        Input.TextChanged += (_, _) => OnTextChanged(EventArgs.Empty);
        Controls.Add(Input);
        Height = 44;
    }

    protected override void OnResize(EventArgs e) { base.OnResize(e); LayoutInput(); }

    private void LayoutInput()
    {
        Input.SetBounds(Padding.Left, Input.Multiline ? Padding.Top : Math.Max(Padding.Top, (Height - Input.PreferredHeight) / 2),
            Math.Max(10, Width - Padding.Horizontal), Input.Multiline ? Math.Max(10, Height - Padding.Vertical) : Input.PreferredHeight);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = DrawingExtensions.RoundedRectangle(new Rectangle(0, 0, Width - 1, Height - 1), Math.Min(CornerRadius, Height / 2));
        using var brush = new SolidBrush(Theme.SurfaceRaised);
        using var pen = new Pen(_focused ? Theme.Accent : Theme.Divider, _focused ? 2 : 1);
        e.Graphics.FillPath(brush, path);
        e.Graphics.DrawPath(pen, path);
    }
}

internal sealed class AvatarView : Control
{
    private Image? _photo;
    private MemoryStream? _photoStream;
    private bool _photoAnimating;
    private int _animationFrameQueued;
    private readonly List<Control> _animationVisibilityAncestors = [];
    private void OnAnimationFrame(object? sender, EventArgs args)
    {
        if (IsDisposed || Disposing || !IsHandleCreated || !Visible) return;
        // ImageAnimator runs independently of the UI pump. On a busy/slow machine
        // every GIF frame used to enqueue another delegate, including hidden rows.
        // One pending notification already represents the newest available frame.
        if (Interlocked.Exchange(ref _animationFrameQueued, 1) != 0) return;
        try
        {
            BeginInvoke(() =>
            {
                Interlocked.Exchange(ref _animationFrameQueued, 0);
                if (!IsDisposed && !Disposing && Visible) Invalidate();
            });
        }
        catch (InvalidOperationException) { Interlocked.Exchange(ref _animationFrameQueued, 0); }
    }
    internal void SetEncodedPhoto(byte[]? bytes)
    {
        StopPhoto();
        if (bytes is not null)
        {
            try
            {
                // Keep the stream alive: GDI+ lazily reads later GIF frames.
                _photoStream = new MemoryStream(bytes.ToArray(), writable: false);
                _photo = Image.FromStream(_photoStream, false, true);
                UpdatePhotoAnimation();
            }
            catch { StopPhoto(); throw; }
        }
        Invalidate();
    }
    private void StopPhoto()
    {
        StopAnimation();
        _photo?.Dispose(); _photo = null;
        _photoStream?.Dispose(); _photoStream = null;
    }
    private void StopAnimation()
    {
        if (_photo is not null && _photoAnimating) ImageAnimator.StopAnimate(_photo, OnAnimationFrame);
        _photoAnimating = false;
    }
    private void UpdatePhotoAnimation()
    {
        if (_photo is null || !IsHandleCreated || !Visible || IsDisposed || Disposing)
        { StopAnimation(); return; }
        if (!_photoAnimating && ImageAnimator.CanAnimate(_photo))
        {
            ImageAnimator.Animate(_photo, OnAnimationFrame);
            _photoAnimating = true;
        }
    }
    private void ObserveAnimationVisibilityAncestors()
    {
        foreach (var ancestor in _animationVisibilityAncestors)
        {
            ancestor.VisibleChanged -= AnimationAncestorVisibilityChanged;
            ancestor.ParentChanged -= AnimationAncestorParentChanged;
        }
        _animationVisibilityAncestors.Clear();
        if (IsDisposed || Disposing) return;
        // WinForms does not always raise this child's VisibleChanged when an
        // ancestor Form hides. Observe the owning chain explicitly so hidden
        // windows stop the animation source, not merely skip its UI callbacks.
        for (var ancestor = Parent; ancestor is not null; ancestor = ancestor.Parent)
        {
            _animationVisibilityAncestors.Add(ancestor);
            ancestor.VisibleChanged += AnimationAncestorVisibilityChanged;
            ancestor.ParentChanged += AnimationAncestorParentChanged;
        }
    }
    private void AnimationAncestorVisibilityChanged(object? sender, EventArgs args) => UpdatePhotoAnimation();
    private void AnimationAncestorParentChanged(object? sender, EventArgs args)
    { ObserveAnimationVisibilityAncestors(); UpdatePhotoAnimation(); }
    protected override void OnParentChanged(EventArgs e)
    { base.OnParentChanged(e); ObserveAnimationVisibilityAncestors(); UpdatePhotoAnimation(); }
    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        UpdatePhotoAnimation();
        if (Visible) Invalidate();
    }
    protected override void OnHandleCreated(EventArgs e)
    { base.OnHandleCreated(e); UpdatePhotoAnimation(); }
    protected override void OnHandleDestroyed(EventArgs e)
    {
        StopAnimation();
        // A callback posted to the old HWND may never be dispatched. Do not let
        // that abandoned notification suppress every frame after handle recreation.
        Interlocked.Exchange(ref _animationFrameQueued, 0);
        base.OnHandleDestroyed(e);
    }
    internal void SetPhoto(Image? image)
    {
        StopPhoto();
        _photo = image is null ? null : new Bitmap(image);
        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (var ancestor in _animationVisibilityAncestors)
            {
                ancestor.VisibleChanged -= AnimationAncestorVisibilityChanged;
                ancestor.ParentChanged -= AnimationAncestorParentChanged;
            }
            _animationVisibilityAncestors.Clear();
            StopPhoto();
        }
        base.Dispose(disposing);
    }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal string Initials { get; set; } = "M";
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal Color AvatarColor { get; set; } = Theme.Accent;

    public AvatarView()
    {
        // The square HWND is transparent even though its circular photo/initials
        // are opaque. WinForms then forwards parent invalidations to this child:
        // a conversation-card hover must also refresh these outside-circle
        // pixels, not retain the previous idle surface until the next click.
        // Keep the parent-paint sampling below so gradients remain continuous;
        // invalidating every child of every RoundedPanel would do needless work.
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
            ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Size = new Size(42, 42);
    }

    protected override void OnPaintBackground(PaintEventArgs pevent)
    {
        if (Parent is null) { pevent.Graphics.Clear(Theme.Canvas); return; }
        // An avatar's square client corners must match the gradient beneath its circle.
        var state = pevent.Graphics.Save();
        pevent.Graphics.TranslateTransform(-Left, -Top);
        InvokePaintBackground(Parent, new PaintEventArgs(pevent.Graphics, Bounds));
        pevent.Graphics.Restore(state);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var brush = new SolidBrush(AvatarColor);
        e.Graphics.FillEllipse(brush, 0, 0, Width - 1, Height - 1);
        if (_photo is not null)
        {
            if (ImageAnimator.CanAnimate(_photo)) ImageAnimator.UpdateFrames(_photo);
            var state = e.Graphics.Save();
            using var clip = new GraphicsPath();
            clip.AddEllipse(1, 1, Width - 2, Height - 2);
            e.Graphics.SetClip(clip);
            e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            var edge = Math.Min(_photo.Width, _photo.Height);
            e.Graphics.DrawImage(_photo, new Rectangle(0, 0, Width, Height),
                new Rectangle((_photo.Width - edge) / 2, (_photo.Height - edge) / 2, edge, edge), GraphicsUnit.Pixel);
            e.Graphics.Restore(state);
            return;
        }
        var perceivedBrightness = (AvatarColor.R * .299f + AvatarColor.G * .587f + AvatarColor.B * .114f) / 255f;
        var textColor = perceivedBrightness > .58f ? Theme.Canvas : Theme.Text;
        // Height has already been scaled by WinForms. Points would scale this value a second
        // time at 125/150% DPI and make initials disproportionately large; use actual pixels.
        var letters = Math.Max(1, Initials.Length);
        var fontPixels = Math.Max(8f, Math.Min(Height * .31f, Width * (letters > 2 ? .22f : .32f)));
        using var familyFont = Theme.Font(10);
        using var font = new Font(familyFont.FontFamily, fontPixels, FontStyle.Bold, GraphicsUnit.Pixel);
        TextRenderer.DrawText(e.Graphics, Initials, font, ClientRectangle,
            textColor, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
    }
}
