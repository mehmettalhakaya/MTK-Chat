using DevExpress.Utils;
using DevExpress.XtraEditors;
using DevExpress.XtraEditors.Controls;
using System.ComponentModel;
using MTKChat.Contracts;

namespace MTKChat.Desktop;

// Selection belongs to stable user IDs, never filtered or recycled controls.
internal sealed class ConversationPickerForm : ModernForm
{
    private readonly ChatUser[] _users;
    private readonly bool _group;
    private readonly HashSet<Guid> _selected = [];
    private readonly TextEdit _title = new(), _search = new();
    private readonly Label _error = new(), _summary = new();
    private readonly ModernButton _create, _clear;
    private readonly PickerPeopleViewport _people;
    private bool _selectionLimited;
    internal string GroupTitle => _title.Text.Trim();
    internal Guid[] Selected => _users.Where(user => _selected.Contains(user.Id)).Select(user => user.Id).ToArray();

    internal ConversationPickerForm(IEnumerable<ChatUser> users, bool group, AvatarCache? avatars = null)
    {
        _users = users.DistinctBy(user => user.Id).OrderBy(user => user.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToArray();
        _group = group;
        Text = group ? "MTK Chat · Yeni grup" : "MTK Chat · Yeni sohbet";
        Icon = Theme.AppIcon(); BackColor = Theme.Canvas;
        AutoScaleMode = AutoScaleMode.Dpi; AutoScaleDimensions = new SizeF(96, 96);
        Size = new Size(560, group ? 740 : 650); MinimumSize = new Size(430, 520);
        MaximizeBox = false; MinimizeBox = false; StartPosition = FormStartPosition.CenterParent;
        var body = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, Padding = new Padding(24, 20, 24, 18),
            RowCount = 7, ColumnCount = 1, BackColor = Theme.Canvas, Margin = Padding.Empty
        };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var height in new[] { 78, group ? 88 : 0, 58, 44 }) body.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        body.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        body.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));

        var heading = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 58));
        var headingText = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, Margin = Padding.Empty };
        headingText.RowStyles.Add(new RowStyle(SizeType.Absolute, 38)); headingText.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        headingText.Controls.Add(new Label
        {
            Text = group ? "Birlikte konuşun." : "Yeni bir sohbet.", Dock = DockStyle.Fill,
            ForeColor = Theme.Text, Font = Theme.Font(20, FontStyle.Bold), Margin = Padding.Empty
        }, 0, 0);
        headingText.Controls.Add(new Label
        {
            Text = group ? "Grubuna katılacak kişileri seç." : "Konuşmaya başlamak için bir kişi seç.",
            Dock = DockStyle.Fill, ForeColor = Theme.Muted, Font = Theme.Font(10), Margin = Padding.Empty
        }, 0, 1);
        var icon = Theme.Button("", ButtonKind.Secondary);
        icon.VectorIcon = group ? ModernButtonIcon.Participants : ModernButtonIcon.Chat;
        icon.Dock = DockStyle.Top; icon.Height = 52; icon.TabStop = false; icon.Enabled = false; icon.Margin = new Padding(6, 0, 0, 0);
        heading.Controls.Add(headingText, 0, 0); heading.Controls.Add(icon, 1, 0); body.Controls.Add(heading, 0, 0);

        var titleArea = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, Margin = Padding.Empty, Visible = group };
        titleArea.RowStyles.Add(new RowStyle(SizeType.Absolute, 26)); titleArea.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        titleArea.Controls.Add(new Label { Text = "Grup adı", Dock = DockStyle.Fill, ForeColor = Theme.Muted, Font = Theme.Font(9), Margin = Padding.Empty }, 0, 0);
        var titleSurface = new RoundedPanel
        {
            Dock = DockStyle.Fill, FillColor = Theme.Surface, BorderColor = Theme.Divider, CornerRadius = 12,
            Padding = new Padding(14, 1, 12, 1), Margin = new Padding(0, 0, 0, 14)
        };
        StyleInput(_title, "Grubuna bir ad ver", "Grup adı"); _title.Properties.MaxLength = 80;
        titleSurface.Controls.Add(_title); titleArea.Controls.Add(titleSurface, 0, 1); body.Controls.Add(titleArea, 0, 1);
        var searchSurface = new RoundedPanel
        {
            Dock = DockStyle.Fill, FillColor = Theme.Surface, BorderColor = Theme.Divider,
            CornerRadius = 12, Padding = new Padding(12, 1, 10, 1), Margin = new Padding(0, 0, 0, 10)
        };
        var searchIcon = Theme.Button("", ButtonKind.Ghost);
        searchIcon.VectorIcon = ModernButtonIcon.Search; searchIcon.Enabled = false; searchIcon.TabStop = false;
        searchIcon.Dock = DockStyle.Left; searchIcon.Width = 30;
        StyleInput(_search, "İsim veya kullanıcı ara...", "Kişi ara");
        searchSurface.Controls.Add(_search); searchSurface.Controls.Add(searchIcon); body.Controls.Add(searchSurface, 0, 2);

        var selection = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 1, ColumnCount = 2, Margin = Padding.Empty };
        selection.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); selection.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 88));
        _summary.Dock = DockStyle.Fill; _summary.ForeColor = Theme.Muted; _summary.Font = Theme.Font(9);
        _summary.TextAlign = ContentAlignment.MiddleLeft; _summary.Margin = Padding.Empty;
        _clear = Theme.Button("Temizle", ButtonKind.Secondary); _clear.Dock = DockStyle.Fill; _clear.Margin = new Padding(0, 2, 0, 6);
        _clear.Click += (_, _) => { _selected.Clear(); _selectionLimited = false; _error.Text = ""; RefreshSelection(); };
        selection.Controls.Add(_summary, 0, 0); selection.Controls.Add(_clear, 1, 0); body.Controls.Add(selection, 0, 3);
        _people = new PickerPeopleViewport(group, avatars, IsSelected, SetSelected)
        { Dock = DockStyle.Fill, Margin = Padding.Empty, AccessibleName = "Sohbet katılımcıları" };
        body.Controls.Add(_people, 0, 4);
        _error.Dock = DockStyle.Fill; _error.ForeColor = Theme.Warning; _error.Font = Theme.Font(9);
        _error.TextAlign = ContentAlignment.MiddleLeft; _error.Margin = new Padding(0, 3, 0, 0); body.Controls.Add(_error, 0, 5);
        var actions = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 1, ColumnCount = 2, Margin = Padding.Empty };
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 102)); actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var cancel = Theme.Button("Vazgeç", ButtonKind.Secondary); cancel.Dock = DockStyle.Fill;
        cancel.Margin = new Padding(0, 8, 10, 0); cancel.DialogResult = DialogResult.Cancel;
        _create = Theme.Button(group ? "Grubu oluştur" : "Sohbeti aç", ButtonKind.Primary);
        _create.Dock = DockStyle.Fill; _create.Margin = new Padding(0, 8, 0, 0); _create.Click += (_, _) => Confirm();
        actions.Controls.Add(cancel, 0, 0); actions.Controls.Add(_create, 1, 0); body.Controls.Add(actions, 0, 6);
        AcceptButton = _create; CancelButton = cancel;
        _search.TextChanged += (_, _) => FilterUsers();
        _title.TextChanged += (_, _) => { _error.Text = ""; RefreshSelection(); };
        Controls.Add(body); FilterUsers();
        Shown += (_, _) =>
        {
            var work = Screen.FromControl(this).WorkingArea;
            Size = new Size(Math.Min(Width, work.Width - 24), Math.Min(Height, work.Height - 24));
            if (group) _title.Focus(); else _search.Focus();
        };
    }

    private static void StyleInput(TextEdit edit, string prompt, string accessibleName)
    {
        edit.Dock = DockStyle.Fill; edit.Margin = Padding.Empty; edit.AccessibleName = accessibleName;
        edit.Properties.AutoHeight = false; edit.Properties.NullValuePrompt = prompt; edit.Properties.BorderStyle = BorderStyles.NoBorder;
        edit.Properties.Appearance.BackColor = Theme.Surface; edit.Properties.Appearance.ForeColor = Theme.Text;
        edit.Properties.Appearance.Font = Theme.Font(11);
        edit.Properties.Appearance.Options.UseBackColor = edit.Properties.Appearance.Options.UseForeColor = edit.Properties.Appearance.Options.UseFont = true;
    }
    private bool IsSelected(Guid id) => _selected.Contains(id);
    private void SetSelected(Guid id, bool selected)
    {
        if (!_users.Any(user => user.Id == id)) return;
        if (selected && !_selected.Contains(id) && _group && _selected.Count >= 49)
        {
            _selectionLimited = true; _error.Text = "Bir gruba en fazla 49 kişi ekleyebilirsin.";
            _people.RefreshSelection(); return;
        }
        if (!_group && selected) _selected.Clear();
        if (selected) _selected.Add(id); else _selected.Remove(id);
        _selectionLimited = false; _error.Text = ""; RefreshSelection();
    }
    private void RefreshSelection()
    {
        _summary.Text = _group ? $"{_selected.Count} kişi seçildi · en fazla 49" : _selected.Count == 0 ? "Bir kişi seç" : "1 kişi seçildi";
        _clear.Enabled = _selected.Count > 0;
        _create.Enabled = _selected.Count is >= 1 and <= 49 && (!_group || ValidGroupTitle());
        _people.RefreshSelection();
    }
    private void FilterUsers()
    {
        var search = _search.Text.Trim();
        _people.SetUsers(_users.Where(user => user.DisplayName.Contains(search, StringComparison.CurrentCultureIgnoreCase)).ToArray());
        if (!_selectionLimited) _error.Text = ""; RefreshSelection();
    }
    private void Confirm()
    {
        if (_group && !ValidGroupTitle() || Selected.Length is < 1 or > 49)
        { _error.Text = _group ? "Grup adı gir ve 1–49 kişi seç." : "Bir kullanıcı seç."; return; }
        DialogResult = DialogResult.OK; Close();
    }
    internal void SearchForQa(string value) => _search.Text = value;
    internal void TitleForQa(string value) => _title.Text = value;
    internal void SelectForQa(Guid id, bool value) => SetSelected(id, value);
    internal void ClearForQa() => _clear.PerformClick();
    internal bool ConfirmEnabledForQa => _create.Enabled;
    internal PickerPeopleViewport PeopleForQa => _people;
    private bool ValidGroupTitle() => GroupTitle.Length is >= 1 and <= 80 && !_title.Text.Any(char.IsControl);
    internal bool ValidateForQa() => (!_group || ValidGroupTitle()) && Selected.Length is >= 1 and <= 49;
}

// The control pool is bounded by viewport height, not the site's account count.
// Rebinding uses AvatarCache's revision guard and AvatarView's owned GIF lifecycle.
internal sealed class PickerPeopleViewport : UserControl
{
    private readonly Panel _clip = new() { BackColor = Theme.Canvas };
    private readonly DevExpress.XtraEditors.VScrollBar _scroll = new() { TabStop = false };
    private readonly Label _empty = new()
    {
        Text = "Kişi bulunamadı", Dock = DockStyle.Fill, ForeColor = Theme.Muted,
        Font = Theme.Font(11), TextAlign = ContentAlignment.MiddleCenter, BackColor = Theme.Canvas
    };
    private readonly bool _group;
    private readonly AvatarCache? _avatars;
    private readonly Func<Guid, bool> _selected;
    private readonly Action<Guid, bool> _change;
    private readonly List<PickerPersonRow> _rows = [];
    private ChatUser[] _users = [];
    private bool _arranging;
    private int RowHeight => S(76);
    private int S(int value) => Math.Max(1, (int)Math.Round(value * DeviceDpi / 96f));
    internal int VisibleRowCountForQa => _rows.Count;
    internal IReadOnlyList<PickerPersonRow> RowsForQa => _rows;
    internal int FilteredCountForQa => _users.Length;
    internal PickerPeopleViewport(bool group, AvatarCache? avatars, Func<Guid, bool> selected, Action<Guid, bool> change)
    {
        _group = group; _avatars = avatars; _selected = selected; _change = change;
        BackColor = Theme.Canvas; SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
        Controls.Add(_clip); Controls.Add(_scroll); _clip.Controls.Add(_empty);
        _scroll.ValueChanged += (_, _) => ArrangeRows();
        _clip.MouseWheel += (_, args) => ScrollWheel(args.Delta);
    }
    internal void SetUsers(ChatUser[] users) { _users = users; _scroll.Value = 0; ArrangeRows(); }
    internal void RefreshSelection()
    { foreach (var row in _rows) if (row.User is { } user) row.ApplySelection(_selected(user.Id)); }
    protected override void OnLayout(LayoutEventArgs e) { base.OnLayout(e); ArrangeRows(); }
    private void ArrangeRows()
    {
        if (_arranging || IsDisposed || _clip.IsDisposed) return;
        _arranging = true;
        try
        {
            var contentHeight = _users.Length * RowHeight; var overflow = contentHeight > ClientSize.Height;
            var scrollbarWidth = overflow ? S(12) : 0;
            _clip.Bounds = new Rectangle(0, 0, Math.Max(0, Width - scrollbarWidth), Height);
            _scroll.Bounds = new Rectangle(Math.Max(0, Width - scrollbarWidth), 0, scrollbarWidth, Height);
            _scroll.Visible = overflow; _scroll.Minimum = 0; _scroll.LargeChange = Math.Max(1, Height); _scroll.SmallChange = RowHeight;
            _scroll.Maximum = Math.Max(0, contentHeight - 1);
            var maxOffset = Math.Max(0, contentHeight - Height);
            if (_scroll.Value > maxOffset) _scroll.Value = maxOffset;
            if (!overflow && _scroll.Value != 0) _scroll.Value = 0;
            var first = Math.Min(_users.Length, _scroll.Value / RowHeight);
            var count = Math.Min(_users.Length - first, Math.Max(0, (Height + RowHeight - 1) / RowHeight + 1));
            while (_rows.Count > count) { var row = _rows[^1]; _rows.RemoveAt(_rows.Count - 1); row.Dispose(); }
            while (_rows.Count < count)
            {
                var row = new PickerPersonRow(_group, _change, Navigate); row.Wheel += ScrollWheel;
                _rows.Add(row); _clip.Controls.Add(row);
            }
            _empty.Visible = _users.Length == 0;
            for (var slot = 0; slot < _rows.Count; slot++)
            {
                var index = first + slot; var row = _rows[slot]; var user = _users[index];
                row.Bounds = new Rectangle(0, index * RowHeight - _scroll.Value, Math.Max(0, _clip.Width - S(3)), RowHeight - S(6));
                row.Bind(user, _selected(user.Id), _avatars);
            }
        }
        finally { _arranging = false; }
    }
    private void ScrollWheel(int delta)
    {
        if (!_scroll.Visible) return;
        _scroll.Value = Math.Clamp(_scroll.Value - Math.Sign(delta) * RowHeight * 2, 0, Math.Max(0, _users.Length * RowHeight - Height));
    }
    private bool Navigate(Guid id, Keys key)
    {
        var current = Array.FindIndex(_users, user => user.Id == id); if (current < 0) return false;
        var next = key switch
        {
            Keys.Down => current + 1, Keys.Up => current - 1,
            Keys.PageDown => current + Math.Max(1, Height / RowHeight), Keys.PageUp => current - Math.Max(1, Height / RowHeight),
            Keys.Home => 0, Keys.End => _users.Length - 1, _ => current
        };
        if (next == current) return false;
        next = Math.Clamp(next, 0, _users.Length - 1);
        var top = next * RowHeight; var maxOffset = Math.Max(0, _users.Length * RowHeight - Height);
        if (top < _scroll.Value) _scroll.Value = Math.Clamp(top, 0, maxOffset);
        else if (top + RowHeight > _scroll.Value + Height) _scroll.Value = Math.Clamp(top + RowHeight - Height, 0, maxOffset);
        ArrangeRows(); _rows.FirstOrDefault(row => row.User?.Id == _users[next].Id)?.FocusSelector(); return true;
    }
    internal void ScrollToEndForQa() { _scroll.Value = Math.Max(0, _users.Length * RowHeight - Height); ArrangeRows(); }
    internal bool NavigateForQa(Guid id, Keys key) => Navigate(id, key);
}

internal sealed class PickerPersonRow : RoundedPanel
{
    private readonly AvatarView _avatar = new();
    private readonly Label _name = new(), _role = new();
    private readonly SelectionCheckEdit _selector = new();
    private readonly Action<Guid, bool> _change;
    private bool _syncing, _selected, _hovered;
    internal ChatUser? User { get; private set; }
    internal event Action<int>? Wheel;
    internal CheckEdit SelectorForQa => _selector;
    internal PickerPersonRow(bool group, Action<Guid, bool> change, Func<Guid, Keys, bool> navigate)
    {
        _change = change; CornerRadius = 14; BorderColor = Color.Transparent;
        FillColor = Theme.Surface; Cursor = Cursors.Hand; TabStop = false;
        _name.ForeColor = Theme.Text; _name.Font = Theme.Font(11, FontStyle.Bold); _name.AutoEllipsis = true;
        _name.TextAlign = ContentAlignment.MiddleLeft; _name.BackColor = Color.Transparent;
        _role.ForeColor = Theme.Muted; _role.Font = Theme.Font(9); _role.AutoEllipsis = true;
        _role.TextAlign = ContentAlignment.MiddleLeft; _role.BackColor = Color.Transparent;
        _selector.Text = ""; _selector.Properties.Caption = ""; _selector.Properties.AutoHeight = false;
        _selector.Properties.AllowGrayed = false;
        _selector.Properties.CheckBoxOptions.Style = group ? CheckBoxStyle.SvgCheckBox1 : CheckBoxStyle.SvgRadio2;
        _selector.Properties.CheckBoxOptions.SvgColorChecked = Theme.AccentHover;
        _selector.Properties.CheckBoxOptions.SvgColorUnchecked = Theme.Muted;
        _selector.Properties.CheckBoxOptions.SvgImageSize = new Size(24, 24);
        _selector.Properties.GlyphAlignment = HorzAlignment.Center; _selector.Properties.BorderStyle = BorderStyles.NoBorder;
        _selector.Properties.Appearance.BackColor = Theme.Surface; _selector.Properties.Appearance.Options.UseBackColor = true;
        _selector.Cursor = Cursors.Hand;
        _selector.CheckedChanged += (_, _) => { if (!_syncing && User is { } user) _change(user.Id, _selector.Checked); };
        _selector.GotFocus += (_, _) => UpdateSurface();
        _selector.LostFocus += (_, _) => UpdateSurface();
        // DevExpress can consume arrows before KeyDown. Route navigation from
        // ProcessCmdKey while leaving Space/Enter to its checkbox/dialog logic.
        _selector.Navigate = key => User is { } user && navigate(user.Id, key);
        foreach (var child in new Control[] { _avatar, _name, _role })
        { child.Cursor = Cursors.Hand; child.Click += (_, _) => ToggleFromRow(); child.MouseWheel += (_, args) => Wheel?.Invoke(args.Delta); }
        Click += (_, _) => ToggleFromRow(); MouseWheel += (_, args) => Wheel?.Invoke(args.Delta);
        _selector.MouseWheel += (_, args) => Wheel?.Invoke(args.Delta); Controls.AddRange([_avatar, _name, _role, _selector]);
        // Hovering an avatar or label is still hovering the same selectable card.
        // Leaving one child for another must not flash back to the default fill.
        foreach (var surface in new Control[] { this, _avatar, _name, _role, _selector })
        {
            surface.MouseEnter += (_, _) => { _hovered = true; UpdateSurface(); };
            surface.MouseLeave += (_, _) =>
            {
                if (ClientRectangle.Contains(PointToClient(System.Windows.Forms.Cursor.Position))) return;
                _hovered = false; UpdateSurface();
            };
        }
    }
    internal void Bind(ChatUser user, bool selected, AvatarCache? avatars)
    {
        if (User != user)
        {
            User = user; _name.Text = user.DisplayName; _role.Text = UserPresentation.Role(user);
            _role.ForeColor = UserPresentation.RoleColor(user); AccessibleName = UserPresentation.Heading(user);
            _selector.AccessibleName = user.DisplayName + " seçimi";
            if (avatars is not null) _ = avatars.ApplyAsync(_avatar, user);
            else { _avatar.SetPhoto(null); _avatar.Tag = null; _avatar.Initials = UserPresentation.Initials(user.DisplayName); _avatar.Invalidate(); }
        }
        ApplySelection(selected);
    }
    internal void ApplySelection(bool selected)
    {
        _selected = selected; _syncing = true;
        try { _selector.Checked = selected; } finally { _syncing = false; }
        UpdateSurface();
        AccessibleDescription = selected ? "Seçili" : "Seçili değil";
    }
    private void UpdateSurface()
    {
        FillColor = _selected ? Theme.SurfaceHover : _hovered ? Theme.SurfaceRaised : Theme.Surface;
        BorderColor = _selector.Focused ? Theme.Violet : _selected ? Theme.AccentHover : _hovered ? Theme.Divider : Color.Transparent;
        _selector.Properties.Appearance.BackColor = FillColor;
    }
    private void ToggleFromRow() { if (User is { } user) { _selector.Focus(); _change(user.Id, !_selected); } }
    internal void ClickForQa() => ToggleFromRow();
    internal void FocusSelector() => _selector.Focus();
    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        int S(int value) => Math.Max(1, (int)Math.Round(value * DeviceDpi / 96f));
        var avatar = Math.Min(S(42), Math.Max(0, Height - S(18)));
        _avatar.Bounds = new Rectangle(S(14), (Height - avatar) / 2, avatar, avatar);
        var selectorWidth = Math.Min(S(42), Math.Max(0, Width));
        _selector.Bounds = new Rectangle(Math.Max(0, Width - selectorWidth - S(10)), Math.Max(0, (Height - S(40)) / 2), selectorWidth, Math.Min(S(40), Height));
        var left = _avatar.Right + S(12); var textWidth = Math.Max(0, _selector.Left - left - S(6));
        _name.Bounds = new Rectangle(left, Math.Max(0, Height / 2 - S(24)), textWidth, S(26));
        _role.Bounds = new Rectangle(left, Height / 2 + S(2), textWidth, S(20));
    }

    private sealed class SelectionCheckEdit : CheckEdit
    {
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        internal Func<Keys, bool>? Navigate { get; set; }
        protected override bool ProcessCmdKey(ref Message message, Keys keys) =>
            Navigate?.Invoke(keys) == true || base.ProcessCmdKey(ref message, keys);
    }
}
