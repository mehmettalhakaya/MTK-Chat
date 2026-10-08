using System.Drawing.Drawing2D;

namespace MTKChat.Desktop;

internal sealed class PinnedMessageBanner : RoundedPanel
{
    private readonly Label _heading = new() { ForeColor = Theme.Violet, BackColor = Color.Transparent,
        Font = Theme.Font(8.5f, FontStyle.Bold), UseMnemonic = false, AutoEllipsis = true };
    private readonly Label _preview = new() { ForeColor = Theme.Text, BackColor = Color.Transparent,
        Font = Theme.Font(9.5f), UseMnemonic = false, AutoEllipsis = true };
    private readonly ModernButton _next = Theme.Button("1 / 1", ButtonKind.Secondary);
    private readonly PinGlyph _pin = new();
    internal event EventHandler? JumpRequested;
    internal event EventHandler? NextRequested;
    internal Guid? MessageId { get; private set; }
    internal string PreviewForQa => _preview.Text;
    internal int RequiredTextHeight => _heading.Font.Height + _preview.Font.Height;

    internal PinnedMessageBanner()
    {
        FillColor = Color.FromArgb(27, 30, 54); BorderColor = Color.FromArgb(68, 61, 111);
        CornerRadius = 11; Cursor = Cursors.Hand; TabStop = true;
        AccessibleRole = AccessibleRole.Link;
        _next.Font = Theme.Font(8.5f); _next.CornerRadius = 9;
        _next.AccessibleName = "Sonraki sabitlenen mesaj";
        _next.Click += (_, _) => NextRequested?.Invoke(this, EventArgs.Empty);
        foreach (var child in new Control[] { _heading, _preview, _pin })
            child.Click += (_, _) => JumpRequested?.Invoke(this, EventArgs.Empty);
        Click += (_, _) => JumpRequested?.Invoke(this, EventArgs.Empty);
        KeyDown += (_, e) =>
        {
            if (e.KeyCode is Keys.Enter or Keys.Space) { e.Handled = true; JumpRequested?.Invoke(this, EventArgs.Empty); }
            if (e.KeyCode is Keys.Right or Keys.Down) { e.Handled = true; NextRequested?.Invoke(this, EventArgs.Empty); }
        };
        Controls.AddRange([_heading, _preview, _next, _pin]);
        Resize += (_, _) => Arrange(); DpiChangedAfterParent += (_, _) => Arrange();
    }

    internal void SetMessage(Guid messageId, string sender, string preview, int index, int count)
    {
        MessageId = messageId;
        _heading.Text = "Sabitlenen mesaj · " + sender;
        var singleLine = preview.Replace('\r', ' ').Replace('\n', ' ');
        var length = Math.Min(240, singleLine.Length);
        if (length < singleLine.Length && char.IsHighSurrogate(singleLine[length - 1])) length--;
        _preview.Text = singleLine.Length > length ? singleLine[..length] + "…" : singleLine;
        _next.Text = $"{index + 1} / {count}"; _next.Enabled = count > 1;
        AccessibleName = $"Sabitlenen mesaj {index + 1}/{count}. {sender}: {_preview.Text}. Sohbette göster.";
        Arrange();
    }

    internal void ClearMessage()
    {
        MessageId = null; _heading.Text = _preview.Text = AccessibleName = "";
    }

    private void Arrange()
    {
        int S(int logical) => Math.Max(1, (int)Math.Round(logical * DeviceDpi / 96d));
        var pad = S(11); var edge = S(24); var nextWidth = S(58);
        _pin.SetBounds(pad, (Height - edge) / 2, edge, edge);
        _next.SetBounds(Math.Max(pad, Width - pad - nextWidth), (Height - S(30)) / 2, nextWidth, S(30));
        var left = _pin.Right + S(9); var width = Math.Max(1, _next.Left - S(10) - left);
        var titleHeight = Math.Max(S(17), _heading.Font.Height + S(1));
        var bodyHeight = Math.Max(S(21), _preview.Font.Height + S(1));
        var top = Math.Max(S(3), (Height - titleHeight - bodyHeight) / 2);
        _heading.SetBounds(left, top, width, titleHeight);
        _preview.SetBounds(left, _heading.Bottom, width, bodyHeight);
    }

    private sealed class PinGlyph : Control
    {
        internal PinGlyph()
        {
            SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.UserPaint |
                ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            BackColor = Color.Transparent;
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var state = e.Graphics.Save();
            e.Graphics.ScaleTransform(Width / 24f, Height / 24f);
            using var pen = new Pen(Theme.Violet, 1.7f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            e.Graphics.DrawLines(pen, [new PointF(8, 4), new(18, 4), new(16, 7), new(16, 12), new(19, 15), new(6, 15), new(9, 12), new(9, 7), new(8, 4)]);
            e.Graphics.DrawLine(pen, 12.5f, 15, 12.5f, 21);
            e.Graphics.Restore(state);
        }
    }
}
