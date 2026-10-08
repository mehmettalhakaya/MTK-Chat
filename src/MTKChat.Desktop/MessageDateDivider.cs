using System.Drawing.Drawing2D;
using System.Globalization;

namespace MTKChat.Desktop;

// A date is presentation metadata only. It shares the real message flow so scroll/read
// calculations continue to use the same child coordinates, without an overlay.
internal sealed class MessageDateDivider : Control
{
    private static readonly CultureInfo Turkish = CultureInfo.GetCultureInfo("tr-TR");
    private readonly DateTime _date;

    internal MessageDateDivider(DateTime localDate)
    {
        _date = localDate.Date;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
            ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        ForeColor = Color.FromArgb(194, 200, 223);
        Font = Theme.Font(8.5f);
        TabStop = false;
        ApplyMetrics();
        AccessibleName = DateText();
    }

    private int Scale(int value) => Math.Max(1, (int)Math.Round(value * DeviceDpi / 96f));

    protected override void OnPaintBackground(PaintEventArgs args) => ChatWallpaper.Draw(args.Graphics, this);

    protected override void OnLocationChanged(EventArgs args)
    {
        base.OnLocationChanged(args);
        // Date separators are movable message-flow children too, not fixed overlays.
        Invalidate();
    }

    private void ApplyMetrics()
    {
        Height = Scale(32);
        Margin = new Padding(0, Scale(4), 0, Scale(12));
    }

    protected override void OnDpiChangedAfterParent(EventArgs args)
    {
        base.OnDpiChangedAfterParent(args);
        ApplyMetrics();
    }

    private string DateText()
    {
        var date = _date.ToString("d MMMM yyyy", Turkish);
        return _date == DateTime.Now.Date ? "Bugün, " + date :
            _date == DateTime.Now.Date.AddDays(-1) ? "Dün, " + date : date;
    }

    protected override void OnPaint(PaintEventArgs args)
    {
        base.OnPaint(args);
        if (Width < 2 || Height < 2) return;
        var text = DateText();
        var flags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine |
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter;
        var desired = TextRenderer.MeasureText(text, Font, Size.Empty, flags).Width + Scale(26);
        var width = Math.Min(Math.Max(0, Width - Scale(8)), desired);
        var bounds = new Rectangle((Width - width) / 2, Scale(2), width, Math.Max(2, Height - Scale(4)));
        args.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = DrawingExtensions.RoundedRectangle(bounds, Scale(10));
        using var fill = new SolidBrush(Color.FromArgb(20, 25, 45));
        using var border = new Pen(Color.FromArgb(49, 57, 87));
        args.Graphics.FillPath(fill, path);
        args.Graphics.DrawPath(border, path);
        TextRenderer.DrawText(args.Graphics, text, Font, bounds, ForeColor, flags);
    }
}
