using System.Drawing.Drawing2D;
using System.ComponentModel;

namespace MTKChat.Desktop;

/// <summary>
/// Retains WinForms' scroll layout and child coordinates while exposing only the clipped viewport.
/// In particular, receipt visibility and saved scroll offsets must use the same coordinate system.
/// </summary>
internal sealed class ModernMessageList : FlowLayoutPanel
{
    private bool _updatingRange;
    private Point _wallpaperOffset;
    internal event EventHandler? MetricsChanged;
    internal int ViewportWidth => Parent?.ClientSize.Width ?? ClientSize.Width;
    internal Rectangle ViewportRectangle => new(0, 0, ViewportWidth, ClientSize.Height);
    // DisplayRectangle is deflated by Padding; using its height clips the last bubble.
    internal int MaximumOffset => VerticalScroll.Visible
        ? Math.Max(0, VerticalScroll.Maximum - VerticalScroll.LargeChange + 1) : 0;
    internal int ContentExtent => Math.Max(ClientSize.Height,
        VerticalScroll.Visible ? VerticalScroll.Maximum + 1 : ClientSize.Height);
    internal int Offset => Math.Clamp(-base.AutoScrollPosition.Y, 0, MaximumOffset);

    public ModernMessageList()
    {
        DoubleBuffered = true;
        AutoScroll = true;
        TabStop = false;
    }

    protected override void OnPaintBackground(PaintEventArgs e) => ChatWallpaper.Draw(e.Graphics, this);

    // Programmatic scrolling does not always raise Scroll. Explicitly notify the companion thumb.
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public new Point AutoScrollPosition
    {
        get => base.AutoScrollPosition;
        set
        {
            base.AutoScrollPosition = value;
            RepaintWindowAnchoredBackground();
            MetricsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public new void ScrollControlIntoView(Control activeControl)
    {
        base.ScrollControlIntoView(activeControl);
        RepaintWindowAnchoredBackground();
        MetricsChanged?.Invoke(this, EventArgs.Empty);
    }

    internal void ScrollToOffset(int offset)
    {
        AutoScrollPosition = new Point(0, Math.Clamp(offset, 0, MaximumOffset));
    }

    protected override void OnLayout(LayoutEventArgs args)
    {
        base.OnLayout(args);
        // Flow layout updates its preferred height after ScrollableControl's range calculation.
        // Refresh once against final bubble heights, including reflows caused by a width change.
        if (!_updatingRange)
        {
            _updatingRange = true;
            try
            {
                // FlowLayout's cached preferred height can differ from actual reflowed rows
                // after DPI/font scaling. Include the last row and its real bottom margin.
                var offset = Math.Max(0, -base.AutoScrollPosition.Y);
                var contentHeight = Controls.Cast<Control>().Where(control => control.Visible)
                    .Select(control => control.Bottom + offset + control.Margin.Bottom + Padding.Bottom)
                    .DefaultIfEmpty(0).Max();
                if (AutoScrollMinSize.Height != contentHeight)
                    AutoScrollMinSize = new Size(0, contentHeight);
                AdjustFormScrollbars(AutoScroll);
            }
            finally { _updatingRange = false; }
        }
        RepaintWindowAnchoredBackground();
        MetricsChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void WndProc(ref Message message)
    {
        const int wmPrint = 0x0317;
        const long printNonClient = 0x0002;
        if (message.Msg == wmPrint)
        {
            // WM_PRINT/DrawToBitmap ignores the parent clip for child non-client areas.
            // Do not print the offscreen native rail; actual on-screen native scrolling is retained.
            message.LParam = new IntPtr(message.LParam.ToInt64() & ~printNonClient);
        }
        base.WndProc(ref message);
        // Native scroll messages also bypass our public AutoScrollPosition setter. The
        // scrollbar/range stays native; only stale wallpaper pixels are invalidated.
        if (message.Msg is 0x0114 or 0x0115 or 0x020a or 0x0005)
            RepaintWindowAnchoredBackground();
    }

    protected override void OnScroll(ScrollEventArgs args)
    {
        base.OnScroll(args);
        RepaintWindowAnchoredBackground();
        MetricsChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnMouseWheel(MouseEventArgs args)
    {
        base.OnMouseWheel(args);
        RepaintWindowAnchoredBackground();
        MetricsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RepaintWindowAnchoredBackground()
    {
        var offset = base.AutoScrollPosition;
        if (offset == _wallpaperOffset || IsDisposed) return;
        _wallpaperOffset = offset;
        // ScrollableControl uses ScrollWindowEx to carry old pixels and child HWNDs.
        // That optimization is valid for content-anchored backgrounds, not our fixed
        // window canvas: repaint both row gaps and every moved child after scrolling.
        // Invalidating instead of Refresh coalesces successive wheel/drag events.
        Invalidate(invalidateChildren: true);
    }
}

/// <summary>
/// The native scrollbar stays operational but lies beyond a clipped child panel. A separately
/// painted scrollbar is always on the right edge of the chat, even when bubbles are centered.
/// This avoids hiding the native bar with ShowScrollBar (which changes the scroll layout/range).
/// </summary>
internal sealed class ModernMessageViewport : Control
{
    private readonly Panel _clip = new WallpaperClip { Margin = Padding.Empty };
    private readonly ModernMessageList _list;
    private readonly SlimMessageScrollBar _scrollbar = new();
    private bool _arranging;
    internal event EventHandler? ContentWidthChanged;

    public ModernMessageViewport(ModernMessageList list)
    {
        _list = list;
        BackColor = Theme.Canvas;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        _clip.Controls.Add(_list);
        Controls.Add(_clip);
        Controls.Add(_scrollbar);
        _scrollbar.ScrollRequested += offset => _list.ScrollToOffset(offset);
        _list.MetricsChanged += Synchronize;
        _scrollbar.MouseWheel += (_, args) => ScrollWheel(args.Delta);
    }

    protected override void OnPaintBackground(PaintEventArgs e) => ChatWallpaper.Draw(e.Graphics, this);

    private sealed class WallpaperClip : Panel
    {
        protected override void OnPaintBackground(PaintEventArgs e) => ChatWallpaper.Draw(e.Graphics, this);
    }

    protected override void OnLocationChanged(EventArgs args)
    {
        base.OnLocationChanged(args);
        // Parent layout can move the whole viewport without moving its local rows.
        // Their fixed-window wallpaper coordinates still need a new sample.
        Invalidate(invalidateChildren: true);
    }

    protected override void OnLayout(LayoutEventArgs args)
    {
        base.OnLayout(args);
        if (_arranging) return;
        _arranging = true;
        try
        {
            var barWidth = Math.Max(12, (int)Math.Round(14 * DeviceDpi / 96d));
            var availableWidth = Math.Max(0, ClientSize.Width - barWidth);
            var contentWidth = availableWidth;
            var oldWidth = _clip.ClientSize.Width;
            _clip.SetBounds((availableWidth - contentWidth) / 2, 0, contentWidth, ClientSize.Height);
            // Reserve exactly the native non-client bar width beyond the clip, not inside the content.
            _list.SetBounds(0, 0, contentWidth + SystemInformation.GetVerticalScrollBarWidthForDpi(_list.DeviceDpi), ClientSize.Height);
            _scrollbar.SetBounds(availableWidth, 0, barWidth, ClientSize.Height);
            if (oldWidth != contentWidth) ContentWidthChanged?.Invoke(this, EventArgs.Empty);
            Synchronize(this, EventArgs.Empty);
        }
        finally { _arranging = false; }
    }

    private void Synchronize(object? sender, EventArgs args)
    {
        if (IsDisposed) return;
        _scrollbar.SetMetrics(_list.Offset, _list.MaximumOffset, _list.ClientSize.Height,
            _list.ContentExtent);
    }

    private void ScrollWheel(int delta)
    {
        var lines = SystemInformation.MouseWheelScrollLines;
        if (lines == 0) return; // Respect Windows' disabled-wheel preference.
        var step = lines < 0 ? _list.ClientSize.Height : Math.Max(1, lines) * 32;
        _list.ScrollToOffset(_list.Offset - (int)Math.Round(delta / 120d * step));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _list.MetricsChanged -= Synchronize;
        base.Dispose(disposing);
    }

    // App-owned regression probes run only in snapshot mode; no real account or network is involved.
    internal IReadOnlyList<string> VerifyScrolling()
    {
        var checks = new List<string>();
        void Require(bool valid, string name)
        {
            if (!valid) throw new InvalidOperationException("Message viewport regression: " + name);
            checks.Add(name);
        }
        PerformLayout();
        _list.PerformLayout();
        var maximum = _list.MaximumOffset;
        Require(maximum > 0, "Overflow has a vertical range");
        _list.ScrollToOffset(maximum);
        Require(Math.Abs(_list.Offset - maximum) <= 1, "Programmatic bottom scroll matches thumb");
        Require(_scrollbar.Value == _list.Offset, "Thumb synchronizes with programmatic scrolling");
        _scrollbar.ApplyKeyboardScroll(Keys.PageUp);
        Require(_list.Offset == Math.Max(0, maximum - _list.ClientSize.Height), "PageUp uses the visible page");
        _scrollbar.ApplyKeyboardScroll(Keys.PageDown);
        Require(_list.Offset == maximum, "PageDown returns to the latest page");
        _scrollbar.ApplyKeyboardScroll(Keys.Home);
        Require(_list.Offset == 0, "Home reaches first message");
        ScrollWheel(-120);
        Require(_list.Offset > 0 && _scrollbar.Value == _list.Offset, "Wheel preserves native child coordinates");
        _scrollbar.DragToFraction(.35);
        Require(Math.Abs(_list.Offset - (int)Math.Round(maximum * .35)) <= 1, "Thumb drag reaches requested range");
        _list.ScrollControlIntoView(_list.Controls[^1]);
        Require(_list.Offset > 0 && _scrollbar.Value == _list.Offset, "ScrollControlIntoView synchronizes the thumb");
        _scrollbar.ApplyKeyboardScroll(Keys.End);
        Require(_list.Offset == maximum, "End reaches latest message");
        var last = _list.Controls[^1];
        Require(last.Bottom <= _list.ViewportRectangle.Bottom,
            $"Latest message is fully visible; list={_list.Bounds}, client={_list.ClientRectangle}, clip={_clip.Bounds}, " +
            $"display={_list.DisplayRectangle}, offset={_list.Offset}, maximum={maximum}, " +
            $"nativeMaximum={_list.VerticalScroll.Maximum}, nativeLargeChange={_list.VerticalScroll.LargeChange}, " +
            $"last={last.Bounds}, dpi={_list.DeviceDpi}");
        Require(_scrollbar.Right == ClientSize.Width && _clip.Right <= _scrollbar.Left,
            "Scrollbar sits at the far right, outside centered bubbles");
        Require(_list.ViewportRectangle.Width == _clip.ClientSize.Width,
            "Receipt viewport excludes the offscreen native bar");
        return checks;
    }
}

internal sealed class SlimMessageScrollBar : Control
{
    private int _value;
    private int _maximum;
    private int _page;
    private int _content;
    private bool _hovered;
    private bool _dragging;
    private int _dragGrab;
    internal event Action<int>? ScrollRequested;
    internal int Value => _value;

    public SlimMessageScrollBar()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        BackColor = Theme.Canvas;
        AccessibleRole = AccessibleRole.ScrollBar;
        AccessibleName = "Mesajları kaydır";
        AccessibleDescription = "Yukarı/aşağı oklar, PageUp/PageDown, Home/End veya sürükleyerek mesajlarda gezin.";
        TabStop = false;
        Cursor = Cursors.Default;
    }

    protected override AccessibleObject CreateAccessibilityInstance() => new ScrollBarAccessibility(this);

    private sealed class ScrollBarAccessibility(SlimMessageScrollBar owner) : ControlAccessibleObject(owner)
    {
        public override string? Value
        {
            get => $"{(owner._maximum == 0 ? 0 : (int)Math.Round(100d * owner._value / owner._maximum))}%";
            set
            {
                if (int.TryParse(value?.Trim().TrimEnd('%'), out var percentage))
                    owner.DragToFraction(percentage / 100d);
            }
        }

        public override string? DefaultAction => "Bir sayfa aşağı kaydır";
        public override void DoDefaultAction() => owner.ApplyKeyboardScroll(Keys.PageDown);
    }

    internal void SetMetrics(int value, int maximum, int page, int content)
    {
        maximum = Math.Max(0, maximum);
        page = Math.Max(1, page);
        content = Math.Max(page, content);
        value = Math.Clamp(value, 0, maximum);
        if (_maximum == maximum && _page == page && _content == content && _value == value) return;
        _maximum = maximum; _page = page; _content = content; _value = value;
        TabStop = _maximum > 0;
        if (_maximum == 0 && _dragging) { _dragging = false; Capture = false; }
        Invalidate();
    }

    private Rectangle Thumb
    {
        get
        {
            var inset = Math.Max(4, (int)Math.Round(6 * DeviceDpi / 96d));
            var track = Math.Max(0, Height - inset * 2);
            if (_maximum == 0 || track <= 0) return Rectangle.Empty;
            var minimumThumb = Math.Min(track, Math.Max(20, (int)Math.Round(36 * DeviceDpi / 96d)));
            var height = Math.Clamp((int)Math.Round(track * _page / (double)_content), minimumThumb, track);
            var width = Math.Min(Width - 2, _hovered || _dragging || Focused ? 8 : 6);
            var top = inset + (int)Math.Round((_value / (double)_maximum) * (track - height));
            return new Rectangle((Width - width) / 2, top, Math.Max(1, width), height);
        }
    }

    protected override void OnPaintBackground(PaintEventArgs args) => ChatWallpaper.Draw(args.Graphics, this);

    protected override void OnPaint(PaintEventArgs args)
    {
        var thumb = Thumb;
        if (thumb.IsEmpty) return;
        args.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var brush = new SolidBrush(_dragging || _hovered || Focused ? Theme.Muted : Theme.SurfaceHover);
        using var path = DrawingExtensions.RoundedRectangle(thumb, Math.Max(1, thumb.Width / 2));
        args.Graphics.FillPath(brush, path);
    }

    protected override void OnMouseEnter(EventArgs args) { _hovered = true; Invalidate(); base.OnMouseEnter(args); }
    protected override void OnMouseLeave(EventArgs args) { _hovered = false; Invalidate(); base.OnMouseLeave(args); }
    protected override void OnGotFocus(EventArgs args) { Invalidate(); base.OnGotFocus(args); }
    protected override void OnLostFocus(EventArgs args) { Invalidate(); base.OnLostFocus(args); }

    protected override void OnMouseDown(MouseEventArgs args)
    {
        base.OnMouseDown(args);
        if (args.Button != MouseButtons.Left || _maximum == 0) return;
        Focus();
        var thumb = Thumb;
        if (args.Y >= thumb.Top && args.Y <= thumb.Bottom)
        {
            _dragging = true;
            _dragGrab = args.Y - thumb.Top;
            Capture = true;
        }
        else ScrollRequested?.Invoke(Math.Clamp(_value + (args.Y < thumb.Top ? -_page : _page), 0, _maximum));
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs args)
    {
        base.OnMouseMove(args);
        if (!_dragging) return;
        var thumb = Thumb;
        var inset = Math.Max(4, (int)Math.Round(6 * DeviceDpi / 96d));
        var travel = Math.Max(0, Height - inset * 2 - thumb.Height);
        if (travel > 0) DragToFraction((args.Y - _dragGrab - inset) / (double)travel);
    }

    internal void DragToFraction(double fraction)
    {
        ScrollRequested?.Invoke((int)Math.Round(Math.Clamp(fraction, 0, 1) * _maximum));
    }

    protected override void OnMouseUp(MouseEventArgs args)
    {
        base.OnMouseUp(args);
        if (args.Button != MouseButtons.Left) return;
        _dragging = false;
        Capture = false;
        Invalidate();
    }

    protected override void OnMouseCaptureChanged(EventArgs args)
    {
        if (!Capture) { _dragging = false; Invalidate(); }
        base.OnMouseCaptureChanged(args);
    }

    protected override bool IsInputKey(Keys keyData) => (keyData & Keys.KeyCode) is
        Keys.Up or Keys.Down or Keys.PageUp or Keys.PageDown or Keys.Home or Keys.End || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs args)
    {
        if (ApplyKeyboardScroll(args.KeyCode)) { args.Handled = true; args.SuppressKeyPress = true; }
        base.OnKeyDown(args);
    }

    internal bool ApplyKeyboardScroll(Keys key)
    {
        var requested = key switch
        {
            Keys.Up => _value - 32,
            Keys.Down => _value + 32,
            Keys.PageUp => _value - _page,
            Keys.PageDown => _value + _page,
            Keys.Home => 0,
            Keys.End => _maximum,
            _ => (int?)null
        };
        if (requested is null) return false;
        ScrollRequested?.Invoke(Math.Clamp(requested.Value, 0, _maximum));
        return true;
    }
}
