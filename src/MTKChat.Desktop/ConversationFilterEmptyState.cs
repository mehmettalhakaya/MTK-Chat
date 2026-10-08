using System.Drawing.Drawing2D;

namespace MTKChat.Desktop;

// Separate from the list's card collection: list reconciliation and scrolling
// only ever see actual conversations, not a synthetic "empty" conversation.
internal sealed class ConversationFilterEmptyState : Panel
{
    private readonly FilterArtwork _artwork = new();
    private readonly Label _title = new() { TextAlign = ContentAlignment.MiddleCenter,
        ForeColor = Theme.Text, Font = Theme.Font(13f), BackColor = Color.Transparent, UseMnemonic = false };
    private readonly Label _detail = new() { TextAlign = ContentAlignment.MiddleCenter,
        ForeColor = Theme.Muted, Font = Theme.Font(9.5f), BackColor = Color.Transparent, UseMnemonic = false };
    private readonly ModernButton _all = Theme.Button("Tüm sohbetleri görüntüle", ButtonKind.Ghost);
    private bool _showAll;
    internal event EventHandler? ShowAllRequested;
    internal string Title => _title.Text;
    internal string Detail => _detail.Text;
    internal bool AllActionVisible => _all.Visible;
    internal bool AllReadArtworkForQa => _artwork.AllRead;

    internal ConversationFilterEmptyState()
    {
        _artwork.Name = "FilterEmptyArtwork"; _title.Name = "FilterEmptyTitle";
        _detail.Name = "FilterEmptyDetail"; _all.Name = "FilterEmptyShowAll";
        BackColor = Color.Transparent;
        AccessibleName = "Sohbet filtresi sonuçları";
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        _all.Font = Theme.Font(9.5f);
        _all.ForeColor = Theme.AccentHover;
        _all.CornerRadius = 12;
        _all.Click += (_, _) => ShowAllRequested?.Invoke(this, EventArgs.Empty);
        Controls.AddRange([_artwork, _title, _detail, _all]);
        Resize += (_, _) => Arrange();
        DpiChangedAfterParent += (_, _) => Arrange();
        VisibleChanged += (_, _) => Arrange();
        FontChanged += (_, _) => Arrange();
    }

    internal void Configure(string title, string detail, bool allRead, bool showAll)
    {
        _title.Text = title;
        _detail.Text = detail;
        _artwork.AllRead = allRead;
        _showAll = showAll;
        _all.Visible = showAll;
        Arrange();
    }

    internal void ShowAllForQa() => _all.PerformClick();

    private void Arrange()
    {
        if (IsDisposed || ClientSize.Width == 0 || ClientSize.Height == 0) return;
        int Pixels(int logical) => Math.Max(1, (int)Math.Round(logical * DeviceDpi / 96f));
        var textEdge = Pixels(12);
        var textWidth = Math.Max(1, ClientSize.Width - 2 * textEdge);
        int TextHeight(Label label) => Math.Max(label.Font.Height, TextRenderer.MeasureText(label.Text, label.Font,
            new Size(textWidth, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height);
        var titleHeight = TextHeight(_title);
        var detailHeight = TextHeight(_detail);
        // The getter includes ancestor visibility, but Configure is commonly
        // called before showing the overlay. Desired visibility controls layout.
        var actionHeight = _showAll ? Math.Max(Pixels(34), _all.Font.Height + Pixels(12)) : 0;
        var fixedHeight = titleHeight + detailHeight + actionHeight;
        var spare = Math.Max(0, ClientSize.Height - fixedHeight);
        // The old 54/48px label blocks plus a 112px illustration required 280
        // logical pixels even if the compact sidebar's list slot was much shorter.
        // Reserve measured copy/action first; decoration consumes only spare space.
        var outerGap = Math.Min(Pixels(8), spare / 4);
        var detailGap = Math.Min(Pixels(4), Math.Max(0, spare - 2 * outerGap));
        var actionGap = actionHeight > 0 ? Math.Min(Pixels(10), Math.Max(0, spare - 2 * outerGap - detailGap)) : 0;
        var decorationRoom = Math.Max(0, spare - 2 * outerGap - detailGap - actionGap);
        var artGap = Math.Min(Pixels(12), decorationRoom);
        var edge = Math.Min(Pixels(112), Math.Min(Math.Max(0, ClientSize.Width - Pixels(24)), decorationRoom - artGap));
        var showArtwork = edge >= Pixels(36);
        _artwork.Visible = showArtwork;
        if (!showArtwork) edge = artGap = 0;
        var blockHeight = fixedHeight + detailGap + actionGap + edge + artGap;
        var y = Math.Max(0, (ClientSize.Height - blockHeight) / 2);
        _artwork.SetBounds((ClientSize.Width - edge) / 2, y, edge, edge);
        y += edge + artGap;
        _title.SetBounds(textEdge, y, textWidth, titleHeight);
        y += titleHeight + detailGap;
        _detail.SetBounds(textEdge, y, textWidth, detailHeight);
        y += detailHeight + actionGap;
        _all.SetBounds(textEdge, y, textWidth, actionHeight);
    }

    internal IReadOnlyList<string> VerifyLayout()
    {
        Arrange();
        var checks = new List<string>();
        void Require(bool valid, string check)
        {
            if (!valid) throw new InvalidOperationException("Empty filter geometry: " + check);
            checks.Add(check);
        }
        Require(Visible && IsHandleCreated && Parent is not null && Width > 0 && Height > 0,
            "Empty-state overlay is a nonempty native visible control");
        Require(Parent!.ClientRectangle.Contains(Bounds) && Parent.Controls.GetChildIndex(this) == 0,
            $"Empty overlay occupies its host and native front z-order; bounds={Bounds}, host={Parent.ClientRectangle}");
        var children = Controls.Cast<Control>().Where(child => child.Visible).ToArray();
        foreach (var child in children)
            Require(child.Width > 0 && child.Height > 0 && ClientRectangle.Contains(child.Bounds),
                $"{child.Name} fits fully inside the clipped empty-state client; child={child.Bounds}, client={ClientRectangle}");
        Require(!children.Any(left => children.Any(right => left != right && left.Bounds.IntersectsWith(right.Bounds))),
            "Artwork, title, detail and return action never overlap");
        foreach (var label in new[] { _title, _detail })
        {
            var measured = TextRenderer.MeasureText(label.Text, label.Font, new Size(label.Width, int.MaxValue),
                TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
            Require(label.Height >= measured.Height, $"{label.Name} contains every measured text line");
        }
        if (_all.Visible)
        {
            var measured = TextRenderer.MeasureText(_all.Text, _all.Font, new Size(int.MaxValue, int.MaxValue),
                TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
            Require(_all.Width >= measured.Width && _all.Height >= measured.Height,
                "The complete return action fits without ellipsis or vertical clipping");
        }
        // Effective child Visible becomes false under a collapsed drawer; QA only
        // calls this on a shown empty state, so hidden decorative art is deliberate.
        checks.Add(_artwork.Visible ? "Decorative artwork fits the available space" :
            "Compact layout hides decoration while retaining complete copy and action");
        return checks;
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        ChatWallpaper.Draw(e.Graphics, this);
        using var tint = new SolidBrush(Color.FromArgb(244, Theme.Sidebar));
        e.Graphics.FillRectangle(tint, ClientRectangle);
    }

    private sealed class FilterArtwork : Control
    {
        private bool _allRead;
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        internal bool AllRead { get => _allRead; set { if (_allRead != value) { _allRead = value; Invalidate(); } } }
        internal FilterArtwork()
        {
            SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.UserPaint |
                ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            BackColor = Color.Transparent;
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            if (Width < 4 || Height < 4) return;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var scale = Math.Min(Width, Height) / 112f;
            PointF Point(float x, float y) => new(x * scale, y * scale);
            using var glow = new SolidBrush(Color.FromArgb(18, Theme.AccentHover));
            e.Graphics.FillEllipse(glow, 0, 0, Width - 1, Height - 1);
            using var bubble = DrawingExtensions.RoundedRectangle(Rectangle.Round(new RectangleF(18 * scale, 25 * scale, 76 * scale, 49 * scale)), (int)Math.Round(16 * scale));
            using var gradient = new LinearGradientBrush(new RectangleF(18 * scale, 25 * scale, 76 * scale, 49 * scale),
                Color.FromArgb(105, 97, 224), Color.FromArgb(70, 68, 178), LinearGradientMode.ForwardDiagonal);
            e.Graphics.FillPath(gradient, bubble);
            using var pale = new Pen(Color.FromArgb(220, 231, 234, 255), 3 * scale)
            { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            e.Graphics.DrawLine(pale, Point(34, 43), Point(78, 43));
            e.Graphics.DrawLine(pale, Point(34, 54), Point(64, 54));
            if (AllRead)
            {
                using var check = new Pen(Theme.Success, 8 * scale)
                { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
                e.Graphics.DrawLines(check, [Point(31, 79), Point(47, 94), Point(81, 60)]);
            }
            else
            {
                using var star = new SolidBrush(Color.FromArgb(204, 194, 255));
                e.Graphics.FillPolygon(star, Enumerable.Range(0, 10).Select(index =>
                {
                    var angle = -Math.PI / 2 + index * Math.PI / 5;
                    var radius = index % 2 == 0 ? 18 : 8;
                    return Point(78 + (float)Math.Cos(angle) * radius, 82 + (float)Math.Sin(angle) * radius);
                }).ToArray());
            }
        }
    }
}
