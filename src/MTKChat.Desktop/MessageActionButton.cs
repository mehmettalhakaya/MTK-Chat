using System.Drawing.Drawing2D;

namespace MTKChat.Desktop;

// Keep the tab target alive while its ink is hidden. Keyboard users can reveal
// the same affordance without a mouse; pointer hover never changes row geometry.
internal sealed class MessageActionButton : Button
{
    private bool _revealed;
    private bool _hovered;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal bool Revealed
    {
        get => _revealed || Focused;
        set { if (_revealed == value) return; _revealed = value; Invalidate(); }
    }

    internal MessageActionButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
            ControlStyles.SupportsTransparentBackColor, true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        UseVisualStyleBackColor = false;
        BackColor = Color.Transparent;
        Cursor = Cursors.Hand;
        Text = string.Empty;
        AccessibleName = "Mesaj seçenekleri";
        AccessibleDescription = "Mesajı yıldızla, kopyala veya sil. Enter, Boşluk ya da aşağı ok ile aç.";
        TabStop = true;
        Visible = false; // A standalone preview row has no menu to open.
    }

    protected override void OnPaintBackground(PaintEventArgs e) => ChatWallpaper.Draw(e.Graphics, this);

    protected override void OnPaint(PaintEventArgs e)
    {
        // Explicitly restore the wallpaper on every frame, including hover exit.
        // Native ButtonBase can otherwise leave a rectangular/stale backing fill.
        ChatWallpaper.Draw(e.Graphics, this);
        if (!Revealed || !Enabled || Width < 2 || Height < 2) return;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var scale = DeviceDpi / 96f;
        var bounds = new Rectangle(1, 1, Width - 3, Height - 3);
        using var path = DrawingExtensions.RoundedRectangle(bounds, (int)Math.Round(9 * scale));
        using var fill = new SolidBrush(_hovered || Focused ? Theme.SurfaceHover : ModernContextMenu.SurfaceColor);
        using var border = new Pen(Focused ? Theme.Violet : Theme.Divider, Math.Max(1, scale));
        e.Graphics.FillPath(fill, path);
        e.Graphics.DrawPath(border, path);
        var cx = Width / 2f;
        var cy = Height / 2f;
        using var chevron = new Pen(_hovered || Focused ? Theme.Text : Theme.Muted, 1.6f * scale)
        { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        e.Graphics.DrawLines(chevron, [new PointF(cx - 4 * scale, cy - 2 * scale),
            new PointF(cx, cy + 2 * scale), new PointF(cx + 4 * scale, cy - 2 * scale)]);
    }

    protected override void OnMouseEnter(EventArgs e) { _hovered = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hovered = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

    protected override bool IsInputKey(Keys keyData) => (keyData & Keys.KeyCode) == Keys.Down || base.IsInputKey(keyData);
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Down) { e.Handled = true; e.SuppressKeyPress = true; PerformClick(); }
        base.OnKeyDown(e);
    }
}
