using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace MTKChat.Desktop;

/// <summary>
/// Wraps an existing vertical FlowLayoutPanel without replacing its controls, selection or
/// native scroll coordinates. Only the native scrollbar lies outside the visible clip.
/// </summary>
internal sealed class ModernConversationViewport : Control
{
    private readonly FlowLayoutPanel _list;
    private readonly WallpaperClip _clip;
    private readonly ConversationScrollBar _scrollbar;
    private readonly ConversationWindow _window;
    private bool _arranging;
    private bool _synchronizing;
    private bool _queued;

    // Counts complete geometry passes, not paints. QA can detect expensive work
    // in an idle/animated list without relying on a particular computer's speed.
    internal long ArrangementPassesForQa { get; private set; }
    internal long SynchronizationPassesForQa { get; private set; }
    internal bool ThumbMatchesForQa => _scrollbar.Value == Offset && _scrollbar.Maximum == MaximumOffset;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal Color Tint { get; set; } = Color.FromArgb(244, Theme.Sidebar);

    internal int ViewportWidth => _clip.ClientSize.Width;
    internal int MaximumOffset => _list.VerticalScroll.Visible
        ? Math.Max(0, _list.VerticalScroll.Maximum - _list.VerticalScroll.LargeChange + 1) : 0;
    internal int Offset => Math.Clamp(-_list.AutoScrollPosition.Y, 0, MaximumOffset);

    internal ModernConversationViewport(FlowLayoutPanel list)
    {
        ArgumentNullException.ThrowIfNull(list);
        _list = list;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
            ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Margin = Padding.Empty;
        Dock = DockStyle.Fill;
        _clip = new WallpaperClip(this);
        _scrollbar = new ConversationScrollBar(this);
        _window = new ConversationWindow(QueueSynchronization, UpdateThumb);
        // DockStyle.Fill would expose the native bar inside the clip and overwrite our bounds.
        _list.Dock = DockStyle.None;
        _list.Margin = Padding.Empty;
        _list.AutoScroll = true;
        _clip.Controls.Add(_list);
        Controls.Add(_clip);
        Controls.Add(_scrollbar);
        _list.Layout += ListLayout;
        _list.Scroll += ListScroll;
        _list.MouseWheel += ListWheel;
        _list.HandleCreated += ListHandleCreated;
        _list.HandleDestroyed += ListHandleDestroyed;
        _list.VisibleChanged += ListVisibleChanged;
        _list.ControlAdded += ListControlsChanged;
        _list.ControlRemoved += ListControlsChanged;
        if (_list.IsHandleCreated) _window.AssignHandle(_list.Handle);
    }

    protected override void OnPaintBackground(PaintEventArgs args) => DrawSurface(args.Graphics, this);

    private void DrawSurface(Graphics graphics, Control surface)
    {
        ChatWallpaper.Draw(graphics, surface);
        if (!Tint.IsEmpty)
        {
            using var tint = new SolidBrush(Tint);
            graphics.FillRectangle(tint, surface.ClientRectangle);
        }
    }

    protected override void OnLayout(LayoutEventArgs args)
    {
        base.OnLayout(args);
        Arrange();
    }

    private void Arrange()
    {
        if (_arranging || IsDisposed || Disposing || _list.IsDisposed || _list.Disposing) return;
        _arranging = true;
        ArrangementPassesForQa++;
        var offset = Math.Max(0, -_list.AutoScrollPosition.Y);
        try
        {
            var barWidth = Math.Max(12, (int)Math.Round(14 * DeviceDpi / 96d));
            var width = Math.Max(0, ClientSize.Width - barWidth);
            _clip.SetBounds(0, 0, width, ClientSize.Height);
            _scrollbar.SetBounds(width, 0, barWidth, ClientSize.Height);
            // Keep ClientSize.Width == visible width both with and without overflow. Existing
            // card sizing based on ClientSize therefore never extends under the clipped rail.
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var nativeWidth = _list.VerticalScroll.Visible
                    ? SystemInformation.GetVerticalScrollBarWidthForDpi(_list.DeviceDpi) : 0;
                var desired = new Rectangle(0, 0, width + nativeWidth, ClientSize.Height);
                if (_list.Bounds == desired) break;
                _list.Bounds = desired;
            }
            // FlowLayoutPanel can retain the pre-resize layout extent even after
            // card Resize handlers shrink their widths. Reflow before calculating
            // ranges, otherwise a stale wide extent creates a native horizontal rail.
            var actualWidth = _list.Controls.Cast<Control>().Where(control => control.Visible)
                .Select(control => control.Right - _list.AutoScrollPosition.X + control.Margin.Right + _list.Padding.Right)
                .DefaultIfEmpty(_list.Padding.Horizontal).Max();
            // Do not force every Arrange through Layout: that would requeue native
            // paint/layout indefinitely, including an intentionally over-wide child.
            if (_list.HorizontalScroll.Visible && _list.DisplayRectangle.Width > Math.Max(actualWidth, _list.ClientSize.Width))
                _list.PerformLayout();
            SynchronizeNow();
            var restoredOffset = Math.Min(offset, MaximumOffset);
            // Setting even the same native offset can repaint and move every child.
            if (_list.AutoScrollPosition.X != 0 || -_list.AutoScrollPosition.Y != restoredOffset)
                _list.AutoScrollPosition = new Point(0, restoredOffset);
            UpdateThumb();
        }
        finally { _arranging = false; }
    }

    internal void SynchronizeNow()
    {
        if (_synchronizing || IsDisposed || Disposing || _list.IsDisposed || _list.Disposing) return;
        _synchronizing = true;
        SynchronizationPassesForQa++;
        try
        {
            var offset = Math.Max(0, -_list.AutoScrollPosition.Y);
            var contentHeight = _list.Controls.Cast<Control>().Where(control => control.Visible)
                .Select(control => control.Bottom + offset + control.Margin.Bottom + _list.Padding.Bottom)
                .DefaultIfEmpty(0).Max();
            // Include actual reflowed row bounds, not padding-deflated DisplayRectangle.Height.
            if (_list.AutoScrollMinSize.Height != contentHeight)
                _list.AutoScrollMinSize = new Size(0, contentHeight);
            UpdateThumb();
        }
        finally { _synchronizing = false; }
    }

    private void UpdateThumb()
    {
        if (IsDisposed || Disposing || _list.IsDisposed || _list.Disposing) return;
        _scrollbar.SetMetrics(Offset, MaximumOffset, _list.ClientSize.Height,
            Math.Max(_list.ClientSize.Height,
                _list.VerticalScroll.Visible ? _list.VerticalScroll.Maximum + 1 : _list.ClientSize.Height));
    }

    private void QueueSynchronization()
    {
        if (_queued || IsDisposed || Disposing || _list.IsDisposed || !IsHandleCreated) return;
        _queued = true;
        try
        {
            BeginInvoke(() =>
            {
                _queued = false;
                if (IsDisposed || Disposing || _list.IsDisposed || _list.Disposing) return;
                Arrange();
            });
        }
        catch (InvalidOperationException) { _queued = false; }
    }

    private void ScrollTo(int offset)
    {
        _list.AutoScrollPosition = new Point(0, Math.Clamp(offset, 0, MaximumOffset));
        UpdateThumb();
    }

    private void ScrollWheel(int delta)
    {
        var lines = SystemInformation.MouseWheelScrollLines;
        if (lines == 0) return;
        var step = lines < 0 ? _list.ClientSize.Height : Math.Max(1, lines) * 32;
        ScrollTo(Offset - (int)Math.Round(delta / 120d * step));
    }

    // Snapshot-only regression checks exercise the actual protected input-handler paths.
    internal IReadOnlyList<string> VerifyScrolling()
    {
        var checks = new List<string>();
        void Require(bool valid, string name)
        {
            if (!valid) throw new InvalidOperationException("Conversation viewport regression: " + name);
            checks.Add(name);
        }
        Arrange();
        _list.PerformLayout();
        SynchronizeNow();
        var originalOffset = Offset;
        var maximum = MaximumOffset;
        Require(!_list.HorizontalScroll.Visible && _list.ClientSize.Height == _clip.ClientSize.Height,
            "Vertical-only list has no horizontal rail or lost client height");
        Require(maximum > 0, "Conversation fixture has overflow");
        try
        {
            ScrollTo(maximum);
            Require(Offset == maximum && _scrollbar.Value == Offset && _scrollbar.Maximum == maximum,
                "Programmatic bottom position synchronizes the thumb");
            var visible = _list.Controls.Cast<Control>().Where(control => control.Visible).ToArray();
            var last = visible.MaxBy(control => control.Bottom)!;
            Require(last.Bottom <= _clip.ClientSize.Height,
                $"Last conversation is fully visible; bounds={last.Bounds}, viewport={_clip.ClientSize}, offset={Offset}");
            Require(_list.ClientSize.Width == ViewportWidth &&
                visible.All(control => control.Right <= ViewportWidth - _list.Padding.Right),
                "Card right edges fit the native-client and visible widths");
            Require(_scrollbar.Right == ClientSize.Width && _clip.Right <= _scrollbar.Left,
                "Conversation scrollbar occupies only the right edge");
            _scrollbar.InvokeKey(Keys.PageUp);
            Require(Offset == Math.Max(0, maximum - _list.ClientSize.Height), "PageUp moves one visible page");
            _scrollbar.InvokeKey(Keys.PageDown);
            Require(Offset == maximum, "PageDown returns to the bottom");
            _scrollbar.InvokeKey(Keys.Home);
            Require(Offset == 0 && _scrollbar.Value == 0, "Home reaches the first conversation");
            _scrollbar.InvokeWheel(-120);
            Require(SystemInformation.MouseWheelScrollLines == 0 ? Offset == 0 : Offset > 0,
                "Actual scrollbar wheel handler respects the Windows wheel preference");
            var expected = _scrollbar.InvokeDrag(.35);
            Require(Offset == expected && _scrollbar.Value == Offset,
                "Mouse down/move/up thumb handlers reach the requested position");
            _list.AutoScrollPosition = new Point(0, maximum / 2);
            SynchronizeNow();
            Require(Offset == maximum / 2 && _scrollbar.Value == Offset,
                "External native offset assignments synchronize the thumb");
            _list.ScrollControlIntoView(last);
            SynchronizeNow();
            Require(last.Bottom <= _clip.ClientSize.Height && _scrollbar.Value == Offset,
                "Native ScrollControlIntoView reveals the last conversation");
            _scrollbar.InvokeKey(Keys.End);
            Require(Offset == maximum, "End reaches the final conversation");
        }
        finally { ScrollTo(originalOffset); }
        return checks;
    }

    private void ListLayout(object? sender, LayoutEventArgs args)
    {
        SynchronizeNow();
        QueueSynchronization();
    }
    private void ListScroll(object? sender, ScrollEventArgs args) => UpdateThumb();
    private void ListWheel(object? sender, MouseEventArgs args) => UpdateThumb();
    private void ListVisibleChanged(object? sender, EventArgs args) => QueueSynchronization();
    private void ListControlsChanged(object? sender, ControlEventArgs args) => QueueSynchronization();
    private void ListHandleCreated(object? sender, EventArgs args)
    {
        _window.AssignHandle(_list.Handle);
        QueueSynchronization();
    }
    private void ListHandleDestroyed(object? sender, EventArgs args) => _window.ReleaseHandle();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _list.Layout -= ListLayout;
            _list.Scroll -= ListScroll;
            _list.MouseWheel -= ListWheel;
            _list.HandleCreated -= ListHandleCreated;
            _list.HandleDestroyed -= ListHandleDestroyed;
            _list.VisibleChanged -= ListVisibleChanged;
            _list.ControlAdded -= ListControlsChanged;
            _list.ControlRemoved -= ListControlsChanged;
            _window.ReleaseHandle();
        }
        base.Dispose(disposing);
    }

    private sealed class WallpaperClip(ModernConversationViewport owner) : Panel
    {
        protected override void OnPaintBackground(PaintEventArgs args) => owner.DrawSurface(args.Graphics, this);
    }

    private sealed class ConversationWindow(Action geometryChanged, Action metricsChanged) : NativeWindow
    {
        protected override void WndProc(ref Message message)
        {
            const int wmPrint = 0x0317;
            if (message.Msg == wmPrint)
            {
                // Printing a child window can bypass the parent's non-client clip. Omit only
                // the offscreen native rail; runtime scrolling and client painting stay native.
                message.LParam = new IntPtr(message.LParam.ToInt64() & ~0x0002L);
            }
            var id = message.Msg;
            base.WndProc(ref message);
            if (id is 0x0005 or 0x0047) geometryChanged();
            // Painting (including GIF frames / WM_PRINT) does not change card
            // geometry. Only synchronize the O(1) thumb metrics; never enumerate
            // all conversations or enqueue Arrange for each frame.
            else if (id is 0x000f or 0x0115 or 0x020a or wmPrint) metricsChanged();
        }
    }

    private sealed class ConversationScrollBar : Control
    {
        private readonly ModernConversationViewport _owner;
        private int _value;
        private int _maximum;
        private int _page;
        private int _content;
        private bool _hovered;
        private bool _dragging;
        private int _grab;
        internal int Value => _value;
        internal int Maximum => _maximum;

        internal ConversationScrollBar(ModernConversationViewport owner)
        {
            _owner = owner;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            AccessibleRole = AccessibleRole.ScrollBar;
            AccessibleName = "Sohbet listesini kaydır";
            AccessibleDescription = "Sürükle veya ok, PageUp/PageDown, Home/End tuşlarıyla sohbetler arasında gezin.";
            TabStop = false;
        }

        internal void SetMetrics(int value, int maximum, int page, int content)
        {
            maximum = Math.Max(0, maximum);
            value = Math.Clamp(value, 0, maximum);
            page = Math.Max(1, page);
            content = Math.Max(page, content);
            if (_maximum == maximum && _value == value && _page == page && _content == content) return;
            _maximum = maximum;
            _value = value;
            _page = page;
            _content = content;
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
                if (_maximum == 0 || track == 0) return Rectangle.Empty;
                var minimum = Math.Min(track, Math.Max(20, (int)Math.Round(36 * DeviceDpi / 96d)));
                var height = Math.Clamp((int)Math.Round(track * _page / (double)_content), minimum, track);
                var width = Math.Max(1, Math.Min(Width - 2, _hovered || _dragging || Focused ? 8 : 6));
                return new Rectangle((Width - width) / 2,
                    inset + (int)Math.Round(_value / (double)_maximum * (track - height)), width, height);
            }
        }

        protected override void OnPaintBackground(PaintEventArgs args) => _owner.DrawSurface(args.Graphics, this);
        protected override void OnPaint(PaintEventArgs args)
        {
            var thumb = Thumb;
            if (thumb.IsEmpty) return;
            args.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var brush = new SolidBrush(_hovered || _dragging || Focused ? Theme.Muted : Theme.SurfaceHover);
            using var path = DrawingExtensions.RoundedRectangle(thumb, Math.Max(1, thumb.Width / 2));
            args.Graphics.FillPath(brush, path);
        }

        protected override void OnMouseEnter(EventArgs args) { _hovered = true; Invalidate(); base.OnMouseEnter(args); }
        protected override void OnMouseLeave(EventArgs args) { _hovered = false; Invalidate(); base.OnMouseLeave(args); }
        protected override void OnGotFocus(EventArgs args) { Invalidate(); base.OnGotFocus(args); }
        protected override void OnLostFocus(EventArgs args) { Invalidate(); base.OnLostFocus(args); }
        protected override void OnMouseWheel(MouseEventArgs args) { _owner.ScrollWheel(args.Delta); base.OnMouseWheel(args); }
        internal void InvokeWheel(int delta) => OnMouseWheel(new MouseEventArgs(MouseButtons.None, 0, Width / 2, Height / 2, delta));
        internal void InvokeKey(Keys key) => OnKeyDown(new KeyEventArgs(key));
        internal int InvokeDrag(double fraction)
        {
            var thumb = Thumb;
            var grab = thumb.Height / 2;
            OnMouseDown(new MouseEventArgs(MouseButtons.Left, 1, Width / 2, thumb.Top + grab, 0));
            var inset = Math.Max(4, (int)Math.Round(6 * DeviceDpi / 96d));
            var travel = Math.Max(0, Height - inset * 2 - Thumb.Height);
            var movement = (int)Math.Round(Math.Clamp(fraction, 0, 1) * travel);
            var expected = travel == 0 ? _value : (int)Math.Round(movement / (double)travel * _maximum);
            OnMouseMove(new MouseEventArgs(MouseButtons.Left, 0, Width / 2, inset + movement + grab, 0));
            OnMouseUp(new MouseEventArgs(MouseButtons.Left, 1, Width / 2, inset + movement + grab, 0));
            return expected;
        }
        protected override void OnMouseDown(MouseEventArgs args)
        {
            base.OnMouseDown(args);
            if (args.Button != MouseButtons.Left || _maximum == 0) return;
            Focus();
            var thumb = Thumb;
            if (args.Y >= thumb.Top && args.Y <= thumb.Bottom)
            {
                _dragging = true; _grab = args.Y - thumb.Top; Capture = true;
            }
            else _owner.ScrollTo(_value + (args.Y < thumb.Top ? -_page : _page));
            Invalidate();
        }
        protected override void OnMouseMove(MouseEventArgs args)
        {
            base.OnMouseMove(args);
            if (!_dragging) return;
            var inset = Math.Max(4, (int)Math.Round(6 * DeviceDpi / 96d));
            var travel = Math.Max(0, Height - inset * 2 - Thumb.Height);
            if (travel > 0)
                _owner.ScrollTo((int)Math.Round(Math.Clamp((args.Y - _grab - inset) / (double)travel, 0, 1) * _maximum));
        }
        protected override void OnMouseUp(MouseEventArgs args)
        {
            base.OnMouseUp(args);
            if (args.Button != MouseButtons.Left) return;
            _dragging = false; Capture = false; Invalidate();
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
            var requested = args.KeyCode switch
            {
                Keys.Up => _value - 32, Keys.Down => _value + 32,
                Keys.PageUp => _value - _page, Keys.PageDown => _value + _page,
                Keys.Home => 0, Keys.End => _maximum, _ => (int?)null
            };
            if (requested is { } offset)
            {
                _owner.ScrollTo(offset); args.Handled = true; args.SuppressKeyPress = true;
            }
            base.OnKeyDown(args);
        }
    }
}
