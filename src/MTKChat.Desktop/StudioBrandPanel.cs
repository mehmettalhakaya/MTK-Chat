using System.Drawing.Drawing2D;

namespace MTKChat.Desktop;

// Flat code-native wordmark. The application/taskbar icon remains its own MTK asset;
// this display mark echoes the reference's chat bubble without a second card/header.
internal sealed class StudioBrandPanel : Control
{
    internal StudioBrandPanel()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Sidebar;
        AccessibleRole = AccessibleRole.StaticText;
        AccessibleName = "MTK Chat";
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (Width < 2 || Height < 2) return;
        var g = e.Graphics;
        g.Clear(Theme.Sidebar);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var scale = DeviceDpi / 96f;
        int S(float value) => (int)Math.Round(value * scale);
        var bubble = new Rectangle(S(2), (Height - S(42)) / 2, S(50), S(36));
        using var shape = new GraphicsPath();
        var diameter = S(22);
        shape.AddArc(bubble.Left, bubble.Top, diameter, diameter, 180, 90);
        shape.AddArc(bubble.Right - diameter, bubble.Top, diameter, diameter, 270, 90);
        shape.AddArc(bubble.Right - diameter, bubble.Bottom - diameter, diameter, diameter, 0, 90);
        shape.AddLine(bubble.Right - S(11), bubble.Bottom, bubble.Left + S(26), bubble.Bottom);
        shape.AddLine(bubble.Left + S(26), bubble.Bottom, bubble.Left + S(10), bubble.Bottom + S(8));
        shape.AddLine(bubble.Left + S(10), bubble.Bottom + S(8), bubble.Left + S(14), bubble.Bottom);
        shape.AddArc(bubble.Left, bubble.Bottom - diameter, diameter, diameter, 90, 90);
        shape.CloseFigure();
        using var fill = new LinearGradientBrush(new Rectangle(bubble.Left, bubble.Top, bubble.Width, bubble.Height + S(8)),
            Color.FromArgb(75, 145, 255), Color.FromArgb(120, 50, 255), LinearGradientMode.Vertical);
        g.FillPath(fill, shape);
        using var highlight = new Pen(Color.FromArgb(125, 145, 199, 255));
        g.DrawPath(highlight, shape);
        using var dot = new SolidBrush(Color.FromArgb(248, 248, 255));
        for (var index = 0; index < 3; index++)
        {
            var x = bubble.Left + S(14 + index * 10);
            var y = bubble.Top + S(17);
            var radius = S(index == 1 ? 3.4f : 2.8f);
            g.FillPolygon(dot, [new Point(x, y - radius), new Point(x + radius, y), new Point(x, y + radius), new Point(x - radius, y)]);
        }
        using var nameFont = Theme.Font(24, FontStyle.Bold);
        var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding;
        var left = S(69);
        var brandWidth = TextRenderer.MeasureText(g, "MTK", nameFont, new Size(int.MaxValue, Height), flags).Width;
        TextRenderer.DrawText(g, "MTK", nameFont, new Rectangle(left, 0, brandWidth + S(2), Height), Theme.Text, flags);
        var chatLeft = left + brandWidth + S(8);
        TextRenderer.DrawText(g, "Chat", nameFont, new Rectangle(chatLeft, 0, Math.Max(0, Width - chatLeft), Height),
            Color.FromArgb(133, 104, 255), flags | TextFormatFlags.EndEllipsis);
    }
}
