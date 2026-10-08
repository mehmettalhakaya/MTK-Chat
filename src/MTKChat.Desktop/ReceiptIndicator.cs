using System.Drawing.Drawing2D;
using MTKChat.Contracts;

namespace MTKChat.Desktop;

// Draw the checks as vectors: Unicode check fonts can overlap or disappear at Windows DPI scales.
internal sealed class ReceiptIndicator : Control
{
    private readonly ToolTip _tooltip = new();
    private string _status = "sent";
    internal static readonly Color ReadColor = Color.FromArgb(83, 189, 235);

    internal ReceiptIndicator()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        AccessibleRole = AccessibleRole.StaticText;
        TabStop = false;
        UpdateDelivery(null);
    }

    internal void UpdateDelivery(MessageDelivery? delivery)
    {
        _status = delivery?.Status is "delivered" or "read" ? delivery.Status : "sent";
        var count = delivery?.Recipients.Count ?? 0;
        var delivered = delivery?.Recipients.Count(r => r.DeliveredAt is not null) ?? 0;
        var read = delivery?.Recipients.Count(r => r.ReadAt is not null) ?? 0;
        AccessibleName = _status switch { "read" => "Okundu", "delivered" => "Teslim edildi", _ => "Gönderildi" };
        _tooltip.SetToolTip(this, AccessibleName + (count == 0 ? "" : $" · {delivered}/{count} teslim · {read}/{count} okundu"));
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(_status == "read" ? ReadColor : Theme.Muted, 1.6f)
            { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        var y = Height / 2f;
        e.Graphics.DrawLines(pen, [new PointF(3, y), new PointF(7, y + 4), new PointF(15, y - 4)]);
        if (_status != "sent")
            e.Graphics.DrawLines(pen, [new PointF(11, y + 2), new PointF(13, y + 4), new PointF(21, y - 4)]);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _tooltip.Dispose();
        base.Dispose(disposing);
    }
}
