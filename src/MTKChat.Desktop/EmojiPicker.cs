using System.Drawing.Drawing2D;
using DevExpress.XtraEditors;
using DevExpress.XtraEditors.Controls;

namespace MTKChat.Desktop;

// A real hosted control replaces the old text-only menu. Choosing inserts Unicode,
// not a bitmap, URL or remote asset, so the existing encrypted message path is unchanged.
internal sealed class EmojiPicker : ToolStripDropDown
{
    private readonly ToolStripControlHost _host;
    internal EmojiPickerPanel Panel { get; } = new();
    internal event Action<EmojiDefinition>? EmojiChosen;

    internal EmojiPicker()
    {
        AutoSize = false; Padding = new Padding(1); Margin = Padding.Empty;
        BackColor = Theme.Divider; DropShadowEnabled = true;
        Renderer = new PickerRenderer();
        _host = new ToolStripControlHost(Panel) { AutoSize = false, Margin = Padding.Empty, Padding = Padding.Empty };
        Items.Add(_host);
        Panel.EmojiChosen += emoji => { EmojiChosen?.Invoke(emoji); Close(ToolStripDropDownCloseReason.ItemClicked); };
        Panel.CloseRequested += () => Close(ToolStripDropDownCloseReason.Keyboard);
        Closed += (_, _) => Panel.StopInteraction();
    }

    internal void ShowFor(Control anchor)
    {
        if (IsDisposed || anchor.IsDisposed || !anchor.IsHandleCreated) return;
        var scale = anchor.DeviceDpi / 96f;
        var area = Screen.FromControl(anchor).WorkingArea;
        Size = new Size(Math.Min((int)Math.Round(392 * scale), area.Width - 8),
            Math.Min((int)Math.Round(444 * scale), area.Height - 8));
        _host.Size = Panel.Size = new Size(Math.Max(1, Width - Padding.Horizontal), Math.Max(1, Height - Padding.Vertical));
        Panel.Grid.OwnerForm = anchor.FindForm();
        Panel.Reset();
        var point = anchor.PointToScreen(new Point(0, -Height - (int)Math.Round(8 * scale)));
        point.X = Math.Clamp(point.X, area.Left, Math.Max(area.Left, area.Right - Width));
        point.Y = Math.Clamp(point.Y, area.Top, Math.Max(area.Top, area.Bottom - Height));
        Show(point); Panel.FocusSearch();
        EmojiAnimationScheduler.RefreshVisibility();
    }

    protected override bool ProcessCmdKey(ref Message message, Keys keyData)
    {
        if (keyData == Keys.Escape) { Close(ToolStripDropDownCloseReason.Keyboard); return true; }
        return base.ProcessCmdKey(ref message, keyData);
    }

    protected override void OnSizeChanged(EventArgs args)
    {
        base.OnSizeChanged(args);
        if (Width <= 0 || Height <= 0) return;
        using var path = EmojiGrid.Round(new RectangleF(0, 0, Width, Height), 15 * DeviceDpi / 96f);
        var old = Region; Region = new Region(path); old?.Dispose();
        if (_host is not null) _host.Size = Panel.Size = new Size(Math.Max(1, Width - Padding.Horizontal), Math.Max(1, Height - Padding.Vertical));
    }

    private sealed class PickerRenderer : ToolStripRenderer
    {
        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e) => e.Graphics.Clear(Theme.Surface);
        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        { using var pen = new Pen(Theme.Divider); e.Graphics.DrawRectangle(pen, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1); }
    }
}

internal sealed class EmojiPickerPanel : Panel
{
    private readonly Label _title = new() { Text = "Emojiler", Font = Theme.Font(15, FontStyle.Bold), ForeColor = Theme.Text };
    private readonly ModernButton _close = Theme.GlyphButton("", "Emoji panelini kapat", ButtonKind.Ghost);
    private readonly RoundedPanel _searchSurface = new() { FillColor = Theme.SurfaceRaised, BorderColor = Theme.Divider, CornerRadius = 11 };
    private readonly TextEdit _search = new();
    private readonly ModernButton _searchIcon = Theme.GlyphButton("\uE721", "Emoji ara", ButtonKind.Ghost);
    private readonly EmojiCategoryBar _categories = new();
    internal EmojiGrid Grid { get; } = new();
    private readonly Label _description = new() { ForeColor = Theme.Muted, Font = Theme.Font(9), TextAlign = ContentAlignment.MiddleLeft };
    private readonly Label _motion = new() { ForeColor = Theme.Violet, Font = Theme.Font(8), TextAlign = ContentAlignment.MiddleRight };
    private EmojiCategory? _category;
    internal event Action<EmojiDefinition>? EmojiChosen;
    internal event Action? CloseRequested;

    internal EmojiPickerPanel()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Surface; AccessibleName = "Emoji seçici";
        _close.VectorIcon = ModernButtonIcon.Close;
        _close.Click += (_, _) => CloseRequested?.Invoke();
        _search.Properties.BorderStyle = BorderStyles.NoBorder;
        _search.Properties.MaxLength = 128;
        _search.Properties.AutoHeight = false; _search.Properties.NullValuePrompt = "Emoji ara";
        _search.Properties.ShowNullValuePrompt = ShowNullValuePromptOptions.EmptyValue | ShowNullValuePromptOptions.EditorFocused;
        _search.Properties.NullValuePromptForeColor = Theme.Muted;
        _search.Properties.Appearance.BackColor = Theme.SurfaceRaised; _search.Properties.Appearance.ForeColor = Theme.Text;
        _search.Properties.Appearance.Font = Theme.Font(10); _search.AccessibleName = "Emoji ara";
        // A persistent search affordance remains after DevExpress intentionally
        // hides its null prompt once an already-edited focused field is cleared.
        _searchIcon.Dock = DockStyle.Left; _searchIcon.TabStop = false;
        _searchIcon.Click += (_, _) => _search.Focus();
        _search.Dock = DockStyle.Fill; _searchSurface.Padding = new Padding(12, 4, 8, 4);
        _searchSurface.Controls.AddRange([_search, _searchIcon]);
        _search.TextChanged += (_, _) => Filter();
        _search.KeyDown += (_, args) =>
        {
            if (ProcessSearchKey(args.KeyCode)) args.SuppressKeyPress = true;
        };
        _categories.CategoryChosen += category => { _category = category; Filter(); };
        Grid.EmojiChosen += emoji => EmojiChosen?.Invoke(emoji);
        Grid.HighlightChanged += emoji =>
        {
            _description.Text = emoji?.Name ?? (Grid.Items.Count == 0 ? "Emoji bulunamadı" : "Bir emoji seç");
            _motion.Text = emoji?.Animated == true ? "Hareketli" : "";
        };
        Controls.AddRange([_title, _close, _searchSurface, _categories, Grid, _description, _motion]);
        Resize += (_, _) => Reflow(); Filter();
    }

    internal void Reset() { _category = null; _categories.SelectedCategory = null; _search.Text = ""; Filter(); }
    internal void FocusSearch() => _search.Focus();
    internal void SetSearchForQa(string value) => _search.Text = value;
    internal void SetCategoryForQa(EmojiCategory? value) { _category = value; _categories.SelectedCategory = value; Filter(); }
    internal void StopInteraction() { Grid.ClearHighlight(); EmojiAnimationScheduler.RefreshVisibility(); }
    private void Filter()
    {
        Grid.SetItems(EmojiCatalog.Search(_search.Text, _category));
        _description.Text = Grid.Items.Count == 0 ? "Emoji bulunamadı" : "Bir emoji seç"; _motion.Text = "";
    }
    private void Reflow()
    {
        int S(int value) => (int)Math.Round(value * DeviceDpi / 96d);
        var pad = S(14); var inner = Math.Max(1, ClientSize.Width - 2 * pad);
        _searchSurface.Padding = new Padding(S(12), S(4), S(8), S(4));
        _searchIcon.Width = S(28);
        _title.SetBounds(pad + S(2), S(13), Math.Max(1, inner - S(40)), S(32));
        _close.SetBounds(ClientSize.Width - pad - S(32), S(12), S(32), S(32));
        _searchSurface.SetBounds(pad, S(54), inner, S(42));
        _categories.SetBounds(pad, S(104), inner, S(40));
        var footerHeight = S(40); var gridTop = S(154);
        Grid.SetBounds(pad, gridTop, inner, Math.Max(1, ClientSize.Height - footerHeight - gridTop));
        _description.SetBounds(pad + S(4), ClientSize.Height - footerHeight, Math.Max(1, inner - S(88)), footerHeight);
        _motion.SetBounds(ClientSize.Width - pad - S(88), ClientSize.Height - footerHeight, S(84), footerHeight);
    }
    protected override void OnDpiChangedAfterParent(EventArgs args) { base.OnDpiChangedAfterParent(args); Reflow(); }
    internal bool InvokeSearchKeyForQa(Keys key) => ProcessSearchKey(key);
    private bool ProcessSearchKey(Keys key)
    {
        if (key == Keys.Down) { Grid.Focus(); Grid.MoveSelection(0); return true; }
        if (key == Keys.Enter) { Grid.MoveSelection(0); Grid.ChooseSelected(); return true; }
        if (key == Keys.Escape) { CloseRequested?.Invoke(); return true; }
        return false;
    }
}

internal sealed class EmojiCategoryBar : Control
{
    private readonly EmojiCategory?[] _values = [null, .. Enum.GetValues<EmojiCategory>().Select(value => (EmojiCategory?)value)];
    private readonly ToolTip _hint = new();
    private int _hover = -1, _selected;
    internal event Action<EmojiCategory?>? CategoryChosen;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal EmojiCategory? SelectedCategory
    {
        get => _values[_selected];
        set { _selected = Math.Max(0, Array.IndexOf(_values, value)); Invalidate(); }
    }
    internal EmojiCategoryBar()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
        BackColor = Theme.Surface; Cursor = Cursors.Hand; TabStop = true; AccessibleName = "Emoji kategorileri";
    }
    protected override void OnMouseMove(MouseEventArgs args)
    {
        base.OnMouseMove(args); var index = ClientRectangle.Contains(args.Location) ? Math.Clamp(args.X * _values.Length / Math.Max(1, Width), 0, _values.Length - 1) : -1;
        if (_hover == index) return; _hover = index; _hint.SetToolTip(this, index < 0 ? "" : CategoryName(_values[index])); Invalidate();
    }
    protected override void OnMouseLeave(EventArgs args) { base.OnMouseLeave(args); _hover = -1; Invalidate(); }
    protected override void OnMouseDown(MouseEventArgs args)
    {
        base.OnMouseDown(args); if (args.Button != MouseButtons.Left || Width < 1 || !ClientRectangle.Contains(args.Location)) return;
        Select(Math.Clamp(args.X * _values.Length / Width, 0, _values.Length - 1));
    }
    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Left or Keys.Right || base.IsInputKey(keyData);
    protected override void OnKeyDown(KeyEventArgs args)
    {
        base.OnKeyDown(args);
        if (args.KeyCode is Keys.Left or Keys.Right) { Select((_selected + (args.KeyCode == Keys.Left ? _values.Length - 1 : 1)) % _values.Length); args.Handled = true; }
    }
    private void Select(int index) { _selected = index; CategoryChosen?.Invoke(_values[index]); Invalidate(); }
    internal void DragOutsideForQa()
    {
        foreach (var x in new[] { -200, -1, Width + 1, Width + 200 })
        { OnMouseMove(new MouseEventArgs(MouseButtons.Left, 1, x, 5, 0)); OnMouseDown(new MouseEventArgs(MouseButtons.Left, 1, x, 5, 0)); }
    }
    protected override void OnPaint(PaintEventArgs args)
    {
        base.OnPaint(args); var g = args.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
        var scale = DeviceDpi / 96f; var width = Width / (float)_values.Length;
        for (var index = 0; index < _values.Length; index++)
        {
            var cell = new RectangleF(index * width + 2 * scale, 1 * scale, width - 4 * scale, Height - 5 * scale);
            if (index == _hover || index == _selected)
            { using var path = EmojiGrid.Round(cell, 9 * scale); using var fill = new SolidBrush(index == _selected ? Color.FromArgb(55, 44, 99) : Theme.SurfaceRaised); g.FillPath(fill, path); }
            var edge = Math.Min(23 * scale, cell.Height - 4 * scale);
            var art = new RectangleF(cell.X + (cell.Width - edge) / 2, cell.Y + (cell.Height - edge) / 2, edge, edge);
            if (_values[index] is { } category && EmojiCatalog.All.FirstOrDefault(emoji => emoji.Category == category) is { } emoji)
                EmojiPainter.Draw(g, art, emoji, 0, false);
            else
            {
                using var pen = new Pen(index == _selected ? Theme.Violet : Theme.Muted, 1.6f * scale);
                var half = edge * .34f; var gap = edge * .17f;
                for (var y = 0; y < 2; y++) for (var x = 0; x < 2; x++)
                    g.DrawRectangle(pen, art.X + x * (half + gap), art.Y + y * (half + gap), half, half);
            }
        }
    }
    internal static string CategoryName(EmojiCategory? category) => category switch
    { EmojiCategory.Faces => "Yüzler", EmojiCategory.Love => "Sevgi", EmojiCategory.Gestures => "İşaretler", EmojiCategory.Celebration => "Kutlama", EmojiCategory.Nature => "Doğa", EmojiCategory.Objects => "Nesneler", _ => "Tümü" };
    protected override void Dispose(bool disposing) { if (disposing) _hint.Dispose(); base.Dispose(disposing); }
}

// One lightweight, virtualized grid. Only its highlighted animated tile repaints;
// it does not allocate a native button or independent timer for every emoji.
internal sealed class EmojiGrid : Control, IEmojiAnimationTarget
{
    private IReadOnlyList<EmojiDefinition> _items = [];
    private int _selected = -1, _scroll;
    internal IReadOnlyList<EmojiDefinition> Items => _items;
    internal int SelectedIndex => _selected;
    private int Cell => Math.Max(1, (int)Math.Round(44 * DeviceDpi / 96d));
    private int Columns => Math.Max(1, (Width - 8) / Cell);
    internal int ColumnsForQa => Columns;
    private int ContentHeight => ((_items.Count + Columns - 1) / Columns) * Cell;
    public Control AnimationControl => this;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal Form? OwnerForm { get; set; }
    public Form? AnimationOwner => OwnerForm;
    public bool WantsAnimation => _selected >= 0 && _selected < _items.Count && _items[_selected].Animated;
    public void InvalidateAnimation() { if (_selected >= 0) Invalidate(Tile(_selected)); }
    internal event Action<EmojiDefinition>? EmojiChosen;
    internal event Action<EmojiDefinition?>? HighlightChanged;
    internal EmojiGrid()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        BackColor = Theme.Surface; Cursor = Cursors.Hand; TabStop = true; AccessibleName = "Emojiler"; AccessibleRole = AccessibleRole.List;
    }
    internal void SetItems(IReadOnlyList<EmojiDefinition> items)
    { _items = items; _scroll = 0; _selected = -1; Invalidate(); HighlightChanged?.Invoke(null); EmojiAnimationScheduler.RefreshVisibility(); }
    internal void ClearHighlight() { _selected = -1; Invalidate(); HighlightChanged?.Invoke(null); }
    internal void SelectForQa(int index) => Highlight(index);
    internal void ChooseSelected() { if (_selected >= 0 && _selected < _items.Count) EmojiChosen?.Invoke(_items[_selected]); }
    internal void MoveSelection(int delta)
    {
        if (_items.Count == 0) return;
        Highlight(Math.Clamp(_selected < 0 ? 0 : _selected + delta, 0, _items.Count - 1));
        var rect = Tile(_selected);
        if (rect.Top < 0) _scroll = Math.Max(0, _scroll + rect.Top);
        else if (rect.Bottom > Height) _scroll = Math.Min(Math.Max(0, ContentHeight - Height), _scroll + rect.Bottom - Height);
        Invalidate();
    }
    internal Rectangle Tile(int index) => new(index % Columns * Cell, index / Columns * Cell - _scroll, Cell, Cell);
    private int Hit(Point point)
    {
        if (!ClientRectangle.Contains(point) || point.X >= Columns * Cell) return -1;
        var index = (point.Y + _scroll) / Cell * Columns + point.X / Cell;
        return index >= 0 && index < _items.Count ? index : -1;
    }
    private void Highlight(int index)
    {
        if (index == _selected) return;
        var previous = _selected; _selected = index;
        if (previous >= 0) Invalidate(Tile(previous)); if (index >= 0) Invalidate(Tile(index));
        HighlightChanged?.Invoke(index >= 0 && index < _items.Count ? _items[index] : null);
        EmojiAnimationScheduler.RefreshVisibility();
    }
    protected override void OnMouseMove(MouseEventArgs args) { base.OnMouseMove(args); Highlight(Hit(args.Location)); }
    protected override void OnMouseLeave(EventArgs args) { base.OnMouseLeave(args); if (!Focused) Highlight(-1); }
    protected override void OnMouseDown(MouseEventArgs args)
    { base.OnMouseDown(args); if (args.Button == MouseButtons.Left) { Highlight(Hit(args.Location)); ChooseSelected(); } }
    protected override void OnMouseWheel(MouseEventArgs args)
    {
        base.OnMouseWheel(args); _scroll = Math.Clamp(_scroll - Math.Sign(args.Delta) * Cell * 2, 0, Math.Max(0, ContentHeight - Height));
        Highlight(-1); Invalidate();
    }
    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Left or Keys.Right or Keys.Up or Keys.Down or Keys.Home or Keys.End || base.IsInputKey(keyData);
    protected override void OnKeyDown(KeyEventArgs args)
    {
        base.OnKeyDown(args);
        var delta = args.KeyCode switch { Keys.Left => -1, Keys.Right => 1, Keys.Up => -Columns, Keys.Down => Columns, Keys.Home => -_items.Count, Keys.End => _items.Count, _ => 0 };
        if (delta != 0) { MoveSelection(delta); args.Handled = true; }
        if (args.KeyCode == Keys.Enter || args.KeyCode == Keys.Space) { ChooseSelected(); args.SuppressKeyPress = true; }
    }
    protected override void OnResize(EventArgs args) { base.OnResize(args); _scroll = Math.Min(_scroll, Math.Max(0, ContentHeight - Height)); }
    protected override void OnHandleCreated(EventArgs args) { base.OnHandleCreated(args); EmojiAnimationScheduler.Subscribe(this); }
    protected override void OnHandleDestroyed(EventArgs args) { EmojiAnimationScheduler.Unsubscribe(this); base.OnHandleDestroyed(args); }
    protected override void Dispose(bool disposing) { if (disposing) EmojiAnimationScheduler.Unsubscribe(this); base.Dispose(disposing); }
    protected override void OnPaint(PaintEventArgs args)
    {
        base.OnPaint(args); var g = args.Graphics; var scale = DeviceDpi / 96f; g.SmoothingMode = SmoothingMode.AntiAlias;
        if (_items.Count == 0)
        {
            using var font = Theme.Font(11);
            TextRenderer.DrawText(g, "Emoji bulunamadı", font, ClientRectangle, Theme.Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix); return;
        }
        var first = _scroll / Cell * Columns;
        var last = Math.Min(_items.Count, ((_scroll + Height) / Cell + 1) * Columns);
        for (var index = first; index < last; index++)
        {
            var tile = Tile(index); if (!args.ClipRectangle.IntersectsWith(tile)) continue;
            if (index == _selected)
            { using var path = Round(new RectangleF(tile.X + scale, tile.Y + scale, tile.Width - 2 * scale, tile.Height - 2 * scale), 11 * scale);
                using var fill = new SolidBrush(Color.FromArgb(51, 45, 86)); g.FillPath(fill, path); }
            var inset = 5 * scale;
            EmojiPainter.Draw(g, new RectangleF(tile.X + inset, tile.Y + inset, tile.Width - 2 * inset, tile.Height - 2 * inset),
                _items[index], EmojiAnimationScheduler.Seconds, index == _selected && EmojiAnimationScheduler.MotionEnabled);
        }
        if (ContentHeight > Height)
        {
            var height = Math.Max(20 * scale, Height * Height / (float)ContentHeight);
            var top = _scroll / (float)Math.Max(1, ContentHeight - Height) * (Height - height);
            using var path = Round(new RectangleF(Width - 4 * scale, top, 3 * scale, height), 1.5f * scale);
            using var fill = new SolidBrush(Color.FromArgb(94, 87, 139)); g.FillPath(fill, path);
        }
    }
    internal static GraphicsPath Round(RectangleF rect, float radius)
    {
        var path = new GraphicsPath(); var size = Math.Min(2 * radius, Math.Min(rect.Width, rect.Height));
        if (size <= 0) return path;
        path.AddArc(rect.X, rect.Y, size, size, 180, 90); path.AddArc(rect.Right - size, rect.Y, size, size, 270, 90);
        path.AddArc(rect.Right - size, rect.Bottom - size, size, size, 0, 90); path.AddArc(rect.X, rect.Bottom - size, size, size, 90, 90); path.CloseFigure(); return path;
    }
    protected override AccessibleObject CreateAccessibilityInstance() => new GridAccessibility(this);
    private sealed class GridAccessibility(EmojiGrid grid) : ControlAccessibleObject(grid)
    {
        public override int GetChildCount() => grid._items.Count;
        public override AccessibleObject? GetChild(int index) => index >= 0 && index < grid._items.Count ? new ItemAccessibility(grid, index, this) : null;
    }
    private sealed class ItemAccessibility(EmojiGrid grid, int index, AccessibleObject parent) : AccessibleObject
    {
        private readonly EmojiDefinition _emoji = grid._items[index];
        private bool Current => index < grid._items.Count && grid._items[index].Symbol == _emoji.Symbol;
        public override string? Name { get => _emoji.Name; set { } }
        public override AccessibleRole Role => AccessibleRole.ListItem;
        public override AccessibleObject? Parent => parent;
        public override string? DefaultAction => "Ekle";
        private bool Interactive => !grid.IsDisposed && Current && grid.IsHandleCreated && grid.Visible && grid.OwnerForm?.WindowState != FormWindowState.Minimized;
        public override Rectangle Bounds
        {
            get
            {
                if (!Interactive) return Rectangle.Empty;
                var visible = Rectangle.Intersect(grid.ClientRectangle, grid.Tile(index));
                return visible.Width > 0 && visible.Height > 0 ? grid.RectangleToScreen(visible) : Rectangle.Empty;
            }
        }
        public override AccessibleStates State => AccessibleStates.Selectable | (grid._selected == index ? AccessibleStates.Selected : AccessibleStates.None) |
            (Bounds.IsEmpty ? AccessibleStates.Offscreen : AccessibleStates.None) | (!Current ? AccessibleStates.Unavailable : AccessibleStates.None);
        public override void DoDefaultAction() { if (!Interactive) return; grid.Highlight(index); grid.ChooseSelected(); }
    }
}
