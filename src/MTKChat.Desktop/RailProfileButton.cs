using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace MTKChat.Desktop;

// The profile image is the real avatar control, not a cached icon screenshot.
// Button keeps standard keyboard activation and owns its image/tooltip lifetime.
internal sealed class RailProfileButton : Button
{
    private readonly AvatarView _avatar;
    private readonly ToolTip _tip;
    private bool _active;
    private bool _hovered;
    private bool _pressed;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal bool Active
    {
        get => _active;
        set { if (_active == value) return; _active = value; InvalidateSurface(); }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal AvatarView Avatar => _avatar;

    internal RailProfileButton(AvatarView avatar)
    {
        _avatar = avatar;
        Text = string.Empty;
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        UseVisualStyleBackColor = false;
        Cursor = Cursors.Hand;
        TabStop = true;
        AccessibleName = "Profilim";
        AccessibleDescription = "Profil fotoğrafını ve hesap bilgilerini açar.";
        AccessibleRole = AccessibleRole.PushButton;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);

        _avatar.TabStop = false;
        _avatar.Cursor = Cursors.Hand;
        _avatar.AccessibleRole = AccessibleRole.Graphic;
        _avatar.AccessibleName = "Profil fotoğrafım";
        _avatar.MouseEnter += (_, _) => SetHovered(true);
        _avatar.MouseLeave += (_, _) => SetHovered(ClientRectangle.Contains(PointToClient(MousePosition)));
        _avatar.MouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Left || !Enabled) return;
            Focus(); _pressed = true; InvalidateSurface();
        };
        _avatar.MouseUp += (_, _) => { _pressed = false; InvalidateSurface(); };
        _avatar.MouseCaptureChanged += (_, _) =>
        {
            if (!_avatar.Capture) { _pressed = false; InvalidateSurface(); }
        };
        _avatar.Click += (_, _) => { if (Enabled) { Focus(); PerformClick(); } };
        Controls.Add(_avatar);
        _tip = new ToolTip { InitialDelay = 250, ReshowDelay = 100 };
        _tip.SetToolTip(this, AccessibleName);
        _tip.SetToolTip(_avatar, AccessibleName);
        ArrangeAvatar();
    }

    private void ArrangeAvatar()
    {
        if (_avatar is null) return; // Button construction may dispatch virtual size notifications.
        // Child pixels have already been DPI scaled. Derive the final square
        // from the actual hit target to avoid a squeezed oval in compact rails.
        var inset = Math.Max(2, (int)Math.Round(4 * DeviceDpi / 96f));
        var available = Math.Max(0, Math.Min(ClientSize.Width, ClientSize.Height) - inset * 2);
        var edge = Math.Min((int)Math.Round(34 * DeviceDpi / 96f), available);
        _avatar.Bounds = new Rectangle((ClientSize.Width - edge) / 2,
            (ClientSize.Height - edge) / 2, edge, edge);
    }

    protected override void OnResize(EventArgs e) { base.OnResize(e); ArrangeAvatar(); }
    protected override void OnDpiChangedAfterParent(EventArgs e) { base.OnDpiChangedAfterParent(e); ArrangeAvatar(); }
    protected override void OnMouseEnter(EventArgs e) { SetHovered(true); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e)
    { SetHovered(ClientRectangle.Contains(PointToClient(MousePosition))); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e)
    { if (e.Button == MouseButtons.Left) _pressed = true; InvalidateSurface(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e)
    { _pressed = false; InvalidateSurface(); base.OnMouseUp(e); }
    protected override void OnMouseCaptureChanged(EventArgs e)
    { if (!Capture) _pressed = false; InvalidateSurface(); base.OnMouseCaptureChanged(e); }
    protected override void OnKeyDown(KeyEventArgs e)
    { if (e.KeyCode is Keys.Space or Keys.Enter) _pressed = true; InvalidateSurface(); base.OnKeyDown(e); }
    protected override void OnKeyUp(KeyEventArgs e)
    { _pressed = false; InvalidateSurface(); base.OnKeyUp(e); }
    protected override void OnGotFocus(EventArgs e) { InvalidateSurface(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e)
    { _pressed = false; InvalidateSurface(); base.OnLostFocus(e); }
    protected override void OnEnabledChanged(EventArgs e)
    { if (!Enabled) _pressed = false; InvalidateSurface(); base.OnEnabledChanged(e); }

    private void SetHovered(bool hovered)
    { if (_hovered == hovered) return; _hovered = hovered; InvalidateSurface(); }

    private void InvalidateSurface()
    {
        Invalidate();
        // AvatarView restores our background under its circular image. Repaint
        // its square corners too when the enclosing tile changes hover/state.
        if (_avatar is not null && !_avatar.IsDisposed) _avatar.Invalidate();
    }

    protected override void OnPaintBackground(PaintEventArgs e) => PaintSurface(e.Graphics);

    private void PaintSurface(Graphics graphics)
    {
        // A child AvatarView may ask for this background while ButtonBase is
        // processing WM_ENABLE rather than a parent erase. Seed every pixel
        // with an opaque rail base before replaying the actual parent paint;
        // a skipped parent erase must never reuse the previous avatar frame.
        using (var backdrop = new SolidBrush(Parent?.BackColor is { A: 255 } color ? color : Theme.Rail))
            graphics.FillRectangle(backdrop, ClientRectangle);
        if (Parent is not null)
        {
            var state = graphics.Save();
            graphics.TranslateTransform(-Left, -Top);
            InvokePaintBackground(Parent, new PaintEventArgs(graphics, Bounds));
            graphics.Restore(state);
        }
        if (Width < 2 || Height < 2) return;
        var surface = !Enabled ? Theme.Surface : _pressed ? Theme.SurfaceHover :
            _hovered ? Theme.SurfaceRaised : Active ? Theme.Surface : Color.Transparent;
        if (surface.A == 0) return;
        var smoothing = graphics.SmoothingMode;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = DrawingExtensions.RoundedRectangle(new Rectangle(0, 0, Width - 1, Height - 1),
            (int)Math.Round(12 * DeviceDpi / 96f));
        using var brush = new SolidBrush(surface);
        graphics.FillPath(brush, path);
        graphics.SmoothingMode = smoothing;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        // ButtonBase can skip OnPaintBackground during native WM_PAINT. Draw the
        // same surface here; child avatar corners and parent tile stay identical.
        PaintSurface(e.Graphics);
        if (Width < 6 || Height < 6 || !Enabled || (!Active && !(Focused && ShowFocusCues))) return;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var inset = Math.Max(1, (int)Math.Round(2 * DeviceDpi / 96f));
        var bounds = new Rectangle(inset, inset, Math.Max(1, Width - inset * 2 - 1), Math.Max(1, Height - inset * 2 - 1));
        using var path = DrawingExtensions.RoundedRectangle(bounds, (int)Math.Round(10 * DeviceDpi / 96f));
        using var pen = new Pen(Focused && ShowFocusCues ? Theme.Violet : Theme.Divider, Math.Max(1, DeviceDpi / 96f));
        e.Graphics.DrawPath(pen, path);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _tip?.Dispose();
        base.Dispose(disposing);
    }
}
