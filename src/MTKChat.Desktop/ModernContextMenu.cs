using System.Drawing.Drawing2D;

namespace MTKChat.Desktop;

// A single menu surface for every app-owned popup, including nested dropdowns.
// BackColor alone does not theme Windows' selection, arrow or image/check margins.
internal sealed class ModernContextMenu : ContextMenuStrip
{
    private static readonly Font MenuFont = Theme.Font(9.5f);
    private static readonly ModernMenuRenderer MenuRenderer = new();
    internal static readonly Color SurfaceColor = Color.FromArgb(24, 28, 51);

    internal ModernContextMenu()
    {
        ApplySurface(this);
        Opening += (_, _) => PrepareItems(this);
        ItemAdded += (_, e) => { if (e.Item is { } item) PrepareItem(item); };
    }

    private static void ApplySurface(ToolStripDropDownMenu menu)
    {
        menu.Renderer = MenuRenderer;
        menu.BackColor = SurfaceColor;
        menu.ForeColor = Theme.Text;
        menu.Font = MenuFont;
        // One native column holds either the vector icon or the checked state;
        // two separate columns wasted space and pushed labels away from their icons.
        menu.ShowImageMargin = true;
        menu.ShowCheckMargin = false;
        var scale = menu.DeviceDpi / 96f;
        int Pixels(int logical) => (int)Math.Round(logical * scale);
        menu.ImageScalingSize = new Size(Pixels(18), Pixels(18));
        menu.Padding = new Padding(Pixels(5));
        menu.MinimumSize = new Size(Pixels(226), 0);
    }

    private static void PrepareItem(ToolStripItem item)
    {
        var scale = (item.Owner?.DeviceDpi ?? 96) / 96f;
        int Pixels(int logical) => (int)Math.Round(logical * scale);
        item.Margin = Padding.Empty;
        // Keep spacing in logical pixels; an opened child dropdown may have a
        // different native DPI from the menu that was originally constructed.
        item.Padding = item is ToolStripSeparator ? new Padding(0, Pixels(3), 0, Pixels(3)) :
            new Padding(Pixels(3), Pixels(5), Pixels(12), Pixels(5));
        item.Font = MenuFont;
        if (item is ToolStripMenuItem { HasDropDownItems: true } parent && parent.DropDown is ToolStripDropDownMenu dropdown)
        {
            ApplySurface(dropdown);
            PrepareItems(dropdown);
        }
    }

    private static void PrepareItems(ToolStrip strip)
    {
        if (strip is ToolStripDropDownMenu dropdown) ApplySurface(dropdown);
        foreach (ToolStripItem item in strip.Items) PrepareItem(item);
    }
}

internal enum ModernMenuIcon
{
    None, Favorite, Star, Archive, Mute, Clock, Unmute, Participants, Info,
    Settings, Edit, Link, Photo, Shield, Clear, Delete, Leave, Pin
}

internal sealed class ModernMenuItem : ToolStripMenuItem
{
    // A shared transparent layout token triggers the native image slot without
    // storing bitmap icon variants, allocating on paint or depending on emoji fonts.
    private static readonly Bitmap ImageSlot = new(18, 18);
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal ModernMenuIcon Icon { get; set; }
    internal ModernMenuItem() => Image = ImageSlot;
}

internal static class ModernMenuActions
{
    internal static ModernMenuItem AddAction(this ToolStripItemCollection items, string text,
        ModernMenuIcon icon, EventHandler? onClick = null)
    {
        var item = new ModernMenuItem { Text = text, Icon = icon };
        if (onClick is not null) item.Click += onClick;
        items.Add(item);
        return item;
    }
}

internal sealed class ModernMenuRenderer : ToolStripProfessionalRenderer
{
    internal ModernMenuRenderer() : base(new ModernMenuColors()) => RoundedEdges = false;
    private static float Scale(ToolStrip? strip) => (strip?.DeviceDpi ?? 96) / 96f;

    private static GraphicsPath Round(RectangleF rect, float radius)
    {
        var path = new GraphicsPath();
        var diameter = Math.Min(radius * 2, Math.Min(rect.Width, rect.Height));
        path.AddArc(rect.Left, rect.Top, diameter, diameter, 180, 90);
        path.AddArc(rect.Right - diameter, rect.Top, diameter, diameter, 270, 90);
        path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rect.Left, rect.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        e.Graphics.Clear(ModernContextMenu.SurfaceColor);
    }

    protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
    {
        using var fill = new SolidBrush(ModernContextMenu.SurfaceColor);
        e.Graphics.FillRectangle(fill, e.AffectedBounds);
    }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (!e.Item.Enabled || !e.Item.Selected) return;
        var scale = Scale(e.ToolStrip);
        // Dropdown layouts may give an item a width larger than the visible popup
        // (notably with a check margin/minimum width). Keep its hover inside the border.
        var available = (e.ToolStrip?.ClientSize.Width ?? e.Item.Width) - e.Item.Bounds.Left - 4 * scale;
        var bounds = new RectangleF(2 * scale, 2 * scale, Math.Min(e.Item.Width - 4 * scale, available), e.Item.Height - 4 * scale);
        if (bounds.Width <= 0 || bounds.Height <= 0) return;
        var state = e.Graphics.Save();
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = Round(bounds, 6 * scale);
        var danger = e.Item.ForeColor == Theme.Danger;
        using var fill = new SolidBrush(danger ? Color.FromArgb(61, 33, 49) : Color.FromArgb(44, 43, 76));
        e.Graphics.FillPath(fill, path);
        e.Graphics.Restore(state);
    }

    protected override void OnRenderItemImage(ToolStripItemImageRenderEventArgs e)
    {
        if (e.Item is not ModernMenuItem item) { base.OnRenderItemImage(e); return; }
        // A checkmark replaces, rather than overlaps, an action icon in the same slot.
        if (item.Checked || item.Icon == ModernMenuIcon.None) return;
        var color = !item.Enabled ? Color.FromArgb(102, 109, 140) : item.ForeColor == Theme.Danger ? Theme.Danger :
            item.Selected ? Theme.Violet : Theme.Muted;
        var rect = e.ImageRectangle;
        var state = e.Graphics.Save();
        var edge = Math.Min(rect.Width, rect.Height);
        e.Graphics.TranslateTransform(rect.Left + (rect.Width - edge) / 2f, rect.Top + (rect.Height - edge) / 2f);
        e.Graphics.ScaleTransform(edge / 20f, edge / 20f);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(color, 1.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        PaintIcon(e.Graphics, pen, item.Icon);
        e.Graphics.Restore(state);
    }

    private static void PaintIcon(Graphics g, Pen pen, ModernMenuIcon icon)
    {
        switch (icon)
        {
            case ModernMenuIcon.Favorite:
                using (var heart = new GraphicsPath())
                {
                    heart.AddBezier(10, 17, 8, 15, 2, 10, 2, 6);
                    heart.AddBezier(2, 6, 2, 1, 8, 1, 10, 5);
                    heart.AddBezier(10, 5, 12, 1, 18, 1, 18, 6);
                    heart.AddBezier(18, 6, 18, 10, 12, 15, 10, 17);
                    g.DrawPath(pen, heart);
                }
                break;
            case ModernMenuIcon.Star:
                var points = Enumerable.Range(0, 10).Select(i =>
                {
                    var angle = -Math.PI / 2 + i * Math.PI / 5;
                    var radius = i % 2 == 0 ? 8 : 3.6;
                    return new PointF(10 + (float)(Math.Cos(angle) * radius), 10 + (float)(Math.Sin(angle) * radius));
                }).ToArray();
                g.DrawPolygon(pen, points);
                break;
            case ModernMenuIcon.Archive:
                g.DrawRectangle(pen, 2, 3, 16, 4); g.DrawRectangle(pen, 4, 7, 12, 10);
                g.DrawLine(pen, 8, 10, 12, 10);
                break;
            case ModernMenuIcon.Mute:
            case ModernMenuIcon.Unmute:
                using (var bell = new GraphicsPath())
                {
                    bell.AddBezier(5, 13, 7, 11, 5, 4, 10, 4);
                    bell.AddBezier(10, 4, 15, 4, 13, 11, 15, 13);
                    bell.AddLine(15, 13, 5, 13); g.DrawPath(pen, bell);
                }
                g.DrawArc(pen, 8, 13, 4, 4, 0, 180); g.DrawLine(pen, 10, 2, 10, 4);
                if (icon == ModernMenuIcon.Mute) g.DrawLine(pen, 3, 3, 17, 17);
                break;
            case ModernMenuIcon.Clock:
                g.DrawEllipse(pen, 2, 2, 16, 16); g.DrawLines(pen, [new(10, 5), new(10, 10), new(13, 12)]);
                break;
            case ModernMenuIcon.Participants:
                g.DrawEllipse(pen, 4, 3, 6, 6); g.DrawArc(pen, 2, 11, 10, 11, 180, 180);
                g.DrawArc(pen, 10, 4, 5, 5, 270, 180); g.DrawArc(pen, 10, 11, 8, 10, 270, 90);
                break;
            case ModernMenuIcon.Info:
                g.DrawEllipse(pen, 2, 2, 16, 16); g.DrawLine(pen, 10, 9, 10, 14); g.DrawLine(pen, 10, 5.5f, 10, 5.8f);
                break;
            case ModernMenuIcon.Settings:
                g.DrawLine(pen, 2, 5, 18, 5); g.DrawLine(pen, 2, 10, 18, 10); g.DrawLine(pen, 2, 15, 18, 15);
                using (var surface = new SolidBrush(ModernContextMenu.SurfaceColor))
                    foreach (var (x, y) in new[] { (7, 5), (13, 10), (7, 15) })
                    { g.FillEllipse(surface, x - 2, y - 2, 4, 4); g.DrawEllipse(pen, x - 2, y - 2, 4, 4); }
                break;
            case ModernMenuIcon.Edit:
                g.DrawPolygon(pen, [new(3, 13), new(13, 3), new(17, 7), new(7, 17), new(2, 18)]);
                g.DrawLine(pen, 11, 5, 15, 9);
                break;
            case ModernMenuIcon.Link:
                using (var chain = new GraphicsPath())
                {
                    chain.AddBezier(8, 6, 15, -1, 21, 5, 14, 12);
                    chain.StartFigure(); chain.AddBezier(12, 14, 5, 21, -1, 15, 6, 8);
                    chain.StartFigure(); chain.AddLine(7, 13, 13, 7); g.DrawPath(pen, chain);
                }
                break;
            case ModernMenuIcon.Photo:
                g.DrawRectangle(pen, 2, 3, 16, 14); g.DrawEllipse(pen, 5, 6, 3, 3);
                g.DrawLines(pen, [new(3, 16), new(9, 10), new(13, 14), new(16, 11), new(18, 13)]);
                break;
            case ModernMenuIcon.Shield:
                using (var shield = new GraphicsPath())
                {
                    shield.AddLines([new PointF(10, 2), new(17, 5), new(16, 12)]);
                    shield.AddBezier(16, 12, 15, 15, 12, 17, 10, 18);
                    shield.AddBezier(10, 18, 8, 17, 5, 15, 4, 12);
                    shield.AddLine(4, 12, 3, 5); shield.CloseFigure(); g.DrawPath(pen, shield);
                }
                g.DrawLines(pen, [new(7, 10), new(9, 12), new(13, 8)]);
                break;
            case ModernMenuIcon.Clear:
                g.DrawLine(pen, 11, 8, 16, 2); g.DrawPolygon(pen, [new(7, 7), new(13, 11), new(10, 18), new(2, 13)]);
                g.DrawLine(pen, 4, 14, 8, 10); g.DrawLine(pen, 7, 16, 10, 12);
                break;
            case ModernMenuIcon.Delete:
                g.DrawLine(pen, 3, 5, 17, 5); g.DrawRectangle(pen, 7, 2, 6, 3);
                g.DrawLines(pen, [new(5, 5), new(6, 17), new(14, 17), new(15, 5)]);
                g.DrawLine(pen, 8, 8, 8, 14); g.DrawLine(pen, 12, 8, 12, 14);
                break;
            case ModernMenuIcon.Leave:
                g.DrawLines(pen, [new(8, 3), new(3, 3), new(3, 17), new(8, 17)]);
                g.DrawLine(pen, 7, 10, 18, 10); g.DrawLines(pen, [new(14, 6), new(18, 10), new(14, 14)]);
                break;
            case ModernMenuIcon.Pin:
                g.DrawPolygon(pen, [new(7, 2), new(14, 2), new(13, 8), new(17, 12), new(4, 12), new(8, 8)]);
                g.DrawLine(pen, 10.5f, 12, 10.5f, 18);
                break;
        }
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        var color = e.Item.Enabled ? e.Item.ForeColor : Theme.Muted;
        // Reuse framework layout/mnemonic flags, but don't let its disabled-item
        // fallback replace the theme's muted color with Windows GrayText.
        // Paint once: DrawText followed by base would overlap menu labels.
        TextRenderer.DrawText(e.Graphics, e.Text, e.TextFont, e.TextRectangle, color, e.TextFormat);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        var scale = Scale(e.Item?.Owner);
        var center = new PointF(e.ArrowRectangle.Left + e.ArrowRectangle.Width / 2f,
            e.ArrowRectangle.Top + e.ArrowRectangle.Height / 2f);
        var direction = e.Direction == ArrowDirection.Left ? -1 : 1;
        var state = e.Graphics.Save();
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(e.Item?.Enabled != false ? Theme.Muted : Theme.Divider, 1.6f * scale)
        { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        e.Graphics.DrawLines(pen, [new PointF(center.X - direction * 2 * scale, center.Y - 4 * scale),
            new PointF(center.X + direction * 2 * scale, center.Y),
            new PointF(center.X - direction * 2 * scale, center.Y + 4 * scale)]);
        e.Graphics.Restore(state);
    }

    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        var scale = Scale(e.ToolStrip);
        var rect = e.ImageRectangle;
        var center = new PointF(rect.Left + rect.Width / 2f, rect.Top + rect.Height / 2f);
        var state = e.Graphics.Save();
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(e.Item.Enabled ? Theme.Accent : Theme.Muted, 1.8f * scale)
        { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        e.Graphics.DrawLines(pen, [new PointF(center.X - 4 * scale, center.Y), new PointF(center.X - scale, center.Y + 3 * scale),
            new PointF(center.X + 5 * scale, center.Y - 4 * scale)]);
        e.Graphics.Restore(state);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        var scale = Scale(e.ToolStrip);
        using var pen = new Pen(Theme.Divider);
        var right = Math.Min(e.Item.Width, (e.ToolStrip?.Width ?? e.Item.Width + e.Item.Bounds.Left) - e.Item.Bounds.Left);
        e.Graphics.DrawLine(pen, 10 * scale, e.Item.Height / 2f, right - 10 * scale, e.Item.Height / 2f);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        var state = e.Graphics.Save();
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = Round(new RectangleF(.5f, .5f, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1), 9 * Scale(e.ToolStrip));
        using var border = new Pen(Color.FromArgb(57, 63, 91));
        e.Graphics.DrawPath(border, path);
        e.Graphics.Restore(state);
    }

    private sealed class ModernMenuColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => ModernContextMenu.SurfaceColor;
        public override Color ImageMarginGradientBegin => ModernContextMenu.SurfaceColor;
        public override Color ImageMarginGradientMiddle => ModernContextMenu.SurfaceColor;
        public override Color ImageMarginGradientEnd => ModernContextMenu.SurfaceColor;
        public override Color MenuBorder => Theme.Divider;
        public override Color MenuItemSelected => Theme.SurfaceHover;
        public override Color MenuItemBorder => Theme.SurfaceHover;
        public override Color MenuItemSelectedGradientBegin => Theme.SurfaceHover;
        public override Color MenuItemSelectedGradientEnd => Theme.SurfaceHover;
        public override Color CheckBackground => ModernContextMenu.SurfaceColor;
        public override Color CheckSelectedBackground => Theme.SurfaceHover;
        public override Color CheckPressedBackground => Theme.SurfaceHover;
        public override Color SeparatorDark => Theme.Divider;
        public override Color SeparatorLight => Theme.Divider;
    }
}
