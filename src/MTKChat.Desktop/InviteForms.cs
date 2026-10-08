using System.ComponentModel;
using DevExpress.XtraEditors;
using DevExpress.XtraEditors.Controls;
using DevExpress.XtraGrid;
using DevExpress.XtraGrid.Views.Grid;
using MTKChat.Contracts;

namespace MTKChat.Desktop;

internal static class InviteFormLayout
{
    internal static void Configure(ModernForm form, string title, Size size, Size minimum)
    {
        // Callers suspend their construction layout until all rows/children
        // exist, so font changes do not consume this 96-DPI design basis early.
        form.Text = title; form.Icon = Theme.AppIcon();
        form.Size = size; form.MinimumSize = minimum;
        form.MaximizeBox = form.MinimizeBox = false; form.StartPosition = FormStartPosition.CenterParent;
        form.AutoScaleMode = AutoScaleMode.Dpi; form.AutoScaleDimensions = new SizeF(96, 96);
        form.BackColor = Theme.Sidebar; form.ForeColor = Theme.Text; form.Font = Theme.Font(10);
    }

    internal static Label Label(string text, float size = 10, Color? color = null, bool bold = false) => new()
    {
        Text = text, Dock = DockStyle.Fill, UseMnemonic = false,
        Font = Theme.Font(size, bold ? FontStyle.Bold : FontStyle.Regular),
        ForeColor = color ?? Theme.Text, BackColor = Color.Transparent,
        TextAlign = ContentAlignment.MiddleLeft, Margin = Padding.Empty
    };

    internal static RoundedPanel Editor(TextEdit editor, bool readOnly, string accessibleName, string prompt)
    {
        var frame = new RoundedPanel { Dock = DockStyle.Fill, CornerRadius = 12, FillColor = Theme.Surface,
            BorderColor = Theme.Divider, Padding = new Padding(14, 12, 14, 12), Margin = new Padding(0, 4, 0, 6) };
        editor.Dock = DockStyle.Fill; editor.AccessibleName = accessibleName;
        editor.Properties.ReadOnly = readOnly; editor.Properties.AutoHeight = false;
        editor.Properties.BorderStyle = BorderStyles.NoBorder; editor.Properties.NullValuePrompt = prompt;
        editor.Properties.Appearance.BackColor = Theme.Surface; editor.Properties.Appearance.ForeColor = Theme.Text;
        editor.Properties.Appearance.Font = Theme.Font(10);
        editor.Properties.Appearance.Options.UseBackColor = editor.Properties.Appearance.Options.UseForeColor =
            editor.Properties.Appearance.Options.UseFont = true;
        editor.Properties.AppearanceFocused.Assign(editor.Properties.Appearance);
        frame.Controls.Add(editor);
        return frame;
    }

    internal static TableLayoutPanel Rows(params int[] heights)
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = heights.Length,
            Padding = new Padding(26, 20, 26, 20), BackColor = Theme.Sidebar, Margin = Padding.Empty };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var height in heights) layout.RowStyles.Add(height == 0
            ? new RowStyle(SizeType.Percent, 100) : new RowStyle(SizeType.Absolute, height));
        return layout;
    }
}

internal sealed class GroupInviteForm : ModernForm
{
    private sealed record InviteRow(GroupInviteEntry Entry)
    {
        public Guid Id => Entry.Id;
        public string Name => "Davet · " + Entry.Id.ToString("N")[..8];
        public string Created => Entry.CreatedAt is { } created ? DateText(created) : "Önceki sürüm";
        public string Expires => Entry.NeverExpires ? "Sınırsız" : DateText(Entry.ExpiresAt);
        public string Status => IsActive(Entry) ? "Aktif" : "Süresi doldu";
    }
    private sealed record DurationChoice(string Label, int Minutes, bool NeverExpires = false) { public override string ToString() => Label; }
    private const int MinimumMinutes = 5, MaximumMinutes = 365 * 24 * 60;
    private readonly ChatApiClient _api;
    private readonly ConversationSummary _group;
    private readonly GridControl _grid = new();
    private readonly GridView _view;
    private readonly Panel _viewport = new() { Dock = DockStyle.Fill, AutoScroll = false, BackColor = Theme.Sidebar };
    private readonly TableLayoutPanel _content;
    private readonly TextEdit _link = new();
    private readonly Label _status = InviteFormLayout.Label("", 9.5f, Theme.Muted);
    private readonly Label _selectionDetails = InviteFormLayout.Label("Bir davet seç veya yeni bağlantı oluştur.", 9.5f, Theme.Muted);
    private readonly ComboBoxEdit _duration = new();
    private readonly SpinEdit _amount = new();
    private readonly ComboBoxEdit _unit = new();
    private readonly ModernButton _create = Theme.Button("Yeni bağlantı", ButtonKind.Primary);
    private readonly ModernButton _refresh = Theme.Button("Yenile", ButtonKind.Secondary);
    private readonly ModernButton _apply = Theme.Button("Süreyi uygula");
    private readonly ModernButton _delete = Theme.Button("Davet Linkini Sil", ButtonKind.Secondary);
    private readonly ModernButton _copy = Theme.Button("Kopyala");
    private readonly ModernButton _revoke = Theme.Button("Tüm Davet Linklerini Sil", ButtonKind.Secondary);
    private readonly ModernButton _close = Theme.Button("Kapat", ButtonKind.Secondary);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly System.Windows.Forms.Timer _expiryTimer = new() { Interval = 1000 };
    private readonly Func<string, bool>? _confirmationForQa;
    private readonly List<GroupInviteEntry> _entries = [];
    private bool _busy, _writing, _lifetimeDisposed, _binding, _reflowing;
    private int _generation;
    private DateTimeOffset? _nextExpiry;
    private GroupInviteEntry? Selected => (_view.GetFocusedRow() as InviteRow)?.Entry;

    internal GroupInviteForm(ChatApiClient api, ConversationSummary group, Func<string, bool>? confirmationForQa = null)
    {
        SuspendLayout();
        _api = api; _group = group; _confirmationForQa = confirmationForQa;
        InviteFormLayout.Configure(this, "MTK Chat · Davet yönetimi", new Size(850, 680), new Size(700, 560));
        // Reserve the complete button footprint, including its vertical margins.
        // A 40px row formerly cropped the default 42px child before layout settled.
        var layout = _content = InviteFormLayout.Rows(46, 26, 0, 72, 22, 54, 52, 30, 48);
        layout.Dock = DockStyle.None; layout.Padding = new Padding(22, 12, 22, 12);
        var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        // Consume the DPI-scaled outer row. The implicit AutoSize row kept the
        // design-height refresh button at 42px on a 120DPI screen despite the
        // containing row having enough room, cutting its readable footprint.
        header.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 94));
        header.Controls.Add(InviteFormLayout.Label("Davet bağlantıları", 21, bold: true), 0, 0);
        _refresh.Dock = DockStyle.Fill; _refresh.Margin = new Padding(8, 4, 0, 4);
        header.Controls.Add(_refresh, 1, 0); layout.Controls.Add(header, 0, 0);
        var subtitle = InviteFormLayout.Label(group.Title + " · Mevcut bağlantıları yönet, her davet için süre belirle.", 10, Theme.Muted);
        subtitle.AutoEllipsis = true; layout.Controls.Add(subtitle, 0, 1);
        _view = new GridView(_grid); ConfigureGrid(); layout.Controls.Add(_grid, 0, 2);
        layout.Controls.Add(BuildDurationEditor(), 0, 3);
        _selectionDetails.AutoEllipsis = true; layout.Controls.Add(_selectionDetails, 0, 4);
        layout.Controls.Add(InviteFormLayout.Editor(_link, true, "Seçilen grup davet bağlantısı", "Bir davet seç"), 0, 5);
        var actions = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1, Margin = Padding.Empty };
        actions.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        for (var column = 0; column < 4; column++) actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        _create.Dock = _apply.Dock = _copy.Dock = _delete.Dock = DockStyle.Fill;
        _create.Margin = new Padding(0, 4, 0, 4);
        _apply.Margin = _copy.Margin = _delete.Margin = new Padding(8, 4, 0, 4);
        _delete.ForeColor = _revoke.ForeColor = Theme.Danger;
        _create.Click += async (_, _) => await CreateAsync();
        _copy.Click += (_, _) => CopyLink();
        _apply.Click += async (_, _) => await ApplyDurationAsync();
        _delete.Click += async (_, _) => await DeleteAsync();
        _refresh.Click += async (_, _) => await LoadAsync();
        _revoke.Click += async (_, _) => await RevokeAsync();
        actions.Controls.Add(_create, 0, 0); actions.Controls.Add(_apply, 1, 0);
        actions.Controls.Add(_copy, 2, 0); actions.Controls.Add(_delete, 3, 0); layout.Controls.Add(actions, 0, 6);
        _status.AutoEllipsis = true; layout.Controls.Add(_status, 0, 7);
        var footer = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty, BackColor = Theme.Sidebar };
        _close.Dock = DockStyle.Right; _close.Width = 100; _close.Click += (_, _) => Close();
        _revoke.Dock = DockStyle.Left; _revoke.Width = 230;
        footer.Controls.Add(_close); footer.Controls.Add(_revoke); layout.Controls.Add(footer, 0, 8);
        _viewport.Controls.Add(layout); Controls.Add(_viewport); CancelButton = _close;
        _viewport.Resize += (_, _) => ReflowContent();
        DpiChanged += (_, _) => ReflowContent();
        FormClosing += (_, e) => { if (_writing) e.Cancel = true; };
        Shown += async (_, _) => { FitToWorkingArea(); ReflowContent(); _expiryTimer.Start(); await LoadAsync(); };
        _expiryTimer.Tick += (_, _) => RefreshExpiredRows();
        UpdateActions();
        ResumeLayout(performLayout: true); ReflowContent();
    }

    private void FitToWorkingArea()
    {
        var work = Screen.FromControl(Owner ?? this).WorkingArea;
        var inset = Math.Min(12, Math.Min(work.Width, work.Height) / 10);
        var available = Rectangle.Inflate(work, -inset, -inset);
        // Windows scales MinimumSize with DPI; lower only the part that cannot
        // fit this monitor. The internal viewport, not clipped buttons, absorbs
        // exceptionally short high-DPI desktop working areas.
        MinimumSize = new Size(Math.Min(MinimumSize.Width, available.Width), Math.Min(MinimumSize.Height, available.Height));
        Size = new Size(Math.Min(Width, available.Width), Math.Min(Height, available.Height));
        Location = new Point(Math.Clamp(Left, available.Left, available.Right - Width),
            Math.Clamp(Top, available.Top, available.Bottom - Height));
    }
    private void ReflowContent()
    {
        if (_reflowing || _content is null || _viewport.IsDisposed) return;
        _reflowing = true;
        try
        {
            // Row styles/padding are already DPI-scaled by WinForms. Reserve a
            // header and one full grid row, then let only the grid expand; the
            // controls keep their usable height even on a very short monitor.
            var fixedHeight = _content.Padding.Vertical + _content.RowStyles.Cast<RowStyle>()
                .Where(row => row.SizeType == SizeType.Absolute).Sum(row => row.Height);
            var minimumHeight = (int)Math.Ceiling(fixedHeight + 80f * DeviceDpi / 96f);
            // Compare against the full control bounds, not ClientSize after an
            // old native scrollbar consumed a few pixels. Keeping AutoScroll on
            // for an exactly-fitted child can retain a one-pixel stale extent and
            // trigger both native bars recursively during a resize.
            var scroll = minimumHeight > _viewport.Height;
            if (!scroll)
            {
                _viewport.AutoScroll = false;
                _viewport.AutoScrollMinSize = Size.Empty;
                _viewport.AutoScrollPosition = Point.Empty;
                _content.Dock = DockStyle.Fill;
                _content.Bounds = _viewport.ClientRectangle;
            }
            else
            {
                _content.Dock = DockStyle.None; _viewport.AutoScroll = true;
                var railWidth = SystemInformation.GetVerticalScrollBarWidthForDpi(DeviceDpi);
                var width = Math.Max(1, _viewport.Width - railWidth - 1);
                _content.Bounds = new Rectangle(_viewport.AutoScrollPosition, new Size(width, minimumHeight));
                _viewport.AutoScrollMinSize = new Size(0, minimumHeight);
            }
        }
        finally { _reflowing = false; }
    }

    private void ConfigureGrid()
    {
        _grid.Dock = DockStyle.Fill; _grid.MainView = _view; _grid.AccessibleName = "Grup davetleri";
        _grid.Margin = new Padding(0, 4, 0, 10);
        _grid.LookAndFeel.UseDefaultLookAndFeel = false; _grid.LookAndFeel.Style = DevExpress.LookAndFeel.LookAndFeelStyle.Flat;
        _view.BorderStyle = BorderStyles.NoBorder; _view.RowHeight = 44;
        _view.OptionsBehavior.Editable = false; _view.OptionsView.ShowGroupPanel = false;
        _view.OptionsView.ShowIndicator = false; _view.OptionsSelection.EnableAppearanceFocusedCell = false;
        _view.OptionsView.ShowVerticalLines = DevExpress.Utils.DefaultBoolean.False;
        _view.OptionsMenu.EnableColumnMenu = false; _view.OptionsCustomization.AllowColumnMoving = false;
        _view.OptionsCustomization.AllowFilter = false; _view.OptionsCustomization.AllowGroup = false;
        foreach (var appearance in new[] { _view.Appearance.Row, _view.Appearance.Empty, _view.Appearance.HeaderPanel,
            _view.Appearance.FocusedRow, _view.Appearance.SelectedRow, _view.Appearance.HideSelectionRow })
        {
            appearance.BackColor = appearance == _view.Appearance.FocusedRow || appearance == _view.Appearance.SelectedRow ||
                appearance == _view.Appearance.HideSelectionRow ? Theme.SurfaceHover : Theme.Surface;
            appearance.ForeColor = Theme.Text; appearance.Font = Theme.Font(9.5f);
            appearance.Options.UseBackColor = appearance.Options.UseForeColor = appearance.Options.UseFont = true;
        }
        _view.Appearance.HeaderPanel.BackColor = Theme.Sidebar; _view.Appearance.HeaderPanel.ForeColor = Theme.Muted;
        _view.Appearance.HeaderPanel.BorderColor = Theme.Divider; _view.Appearance.HeaderPanel.Options.UseBorderColor = true;
        _view.Appearance.HorzLine.BackColor = Theme.Divider; _view.Appearance.HorzLine.Options.UseBackColor = true;
        _view.Columns.AddVisible(nameof(InviteRow.Name), "Bağlantı").Width = 168;
        _view.Columns.AddVisible(nameof(InviteRow.Created), "Oluşturuldu").Width = 180;
        _view.Columns.AddVisible(nameof(InviteRow.Expires), "Son kullanım").Width = 180;
        _view.Columns.AddVisible(nameof(InviteRow.Status), "Durum").Width = 102;
        _view.Columns.AddField(nameof(InviteRow.Id));
        // Date captions are localized strings, not sortable timestamps. Keep the
        // server-independent newest-first ordering and identify selection by ID.
        _view.OptionsCustomization.AllowSort = false;
        _view.FocusedRowChanged += (_, _) => { if (!_binding) UpdateSelection(); };
        _view.RowCellStyle += (_, e) =>
        {
            if (e.Column.FieldName == nameof(InviteRow.Status) && _view.GetRow(e.RowHandle) is InviteRow row)
                e.Appearance.ForeColor = IsActive(row.Entry) ? Theme.Success : Theme.Warning;
        };
    }

    private Control BuildDurationEditor()
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 2, Margin = Padding.Empty,
            BackColor = Theme.Sidebar, Padding = new Padding(0, 4, 0, 6) };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 144));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 132));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 26)); panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var label = InviteFormLayout.Label("Geçerlilik · şimdi başlayan süre", 9.5f, Theme.Muted);
        panel.Controls.Add(label, 0, 0); panel.SetColumnSpan(label, 3);
        ConfigureCombo(_duration); ConfigureCombo(_unit); ConfigureEditor(_amount);
        _duration.Properties.Items.AddRange([new DurationChoice("1 saat", 60), new DurationChoice("1 gün", 1440),
            new DurationChoice("7 gün", 10080), new DurationChoice("30 gün", 43200), new DurationChoice("Özel süre", 0),
            new DurationChoice("Sınırsız", 0, true)]);
        _duration.SelectedIndex = 2; _duration.AccessibleName = "Davetin geçerlilik süresi";
        _unit.Properties.Items.AddRange(["Dakika", "Saat", "Gün"]); _unit.SelectedIndex = 0; _unit.AccessibleName = "Özel süre birimi";
        _amount.Properties.IsFloatValue = false; _amount.Properties.MinValue = 1; _amount.Properties.MaxValue = MaximumMinutes;
        _amount.Properties.Increment = 1; _amount.Properties.Mask.EditMask = "d"; _amount.EditValue = 15;
        _amount.AccessibleName = "Özel süre miktarı";
        _duration.Dock = _amount.Dock = _unit.Dock = DockStyle.Fill;
        _duration.Margin = new Padding(0, 0, 0, 0); _amount.Margin = _unit.Margin = new Padding(10, 0, 0, 0);
        panel.Controls.Add(_duration, 0, 1); panel.Controls.Add(_amount, 1, 1); panel.Controls.Add(_unit, 2, 1);
        _duration.SelectedIndexChanged += (_, _) => UpdateDurationEditors();
        _amount.EditValueChanged += (_, _) => UpdateActions(); _unit.SelectedIndexChanged += (_, _) => UpdateActions();
        UpdateDurationEditors(); return panel;
    }

    private static void ConfigureEditor(BaseEdit editor)
    {
        editor.Properties.AutoHeight = false; editor.Properties.BorderStyle = BorderStyles.Simple;
        editor.Properties.Appearance.BackColor = Theme.Surface; editor.Properties.Appearance.ForeColor = Theme.Text;
        editor.Properties.Appearance.Font = Theme.Font(10);
        editor.Properties.Appearance.Options.UseBackColor = editor.Properties.Appearance.Options.UseForeColor =
            editor.Properties.Appearance.Options.UseFont = true;
        editor.Properties.AppearanceFocused.Assign(editor.Properties.Appearance);
        editor.Properties.AppearanceDisabled.Assign(editor.Properties.Appearance); editor.Properties.AppearanceDisabled.ForeColor = Theme.Muted;
    }
    private static void ConfigureCombo(ComboBoxEdit combo)
    {
        ConfigureEditor(combo); combo.Properties.TextEditStyle = TextEditStyles.DisableTextEditor;
        foreach (var appearance in new[] { combo.Properties.AppearanceDropDown, combo.Properties.AppearanceItemSelected,
            combo.Properties.AppearanceItemHighlight })
        {
            appearance.BackColor = appearance == combo.Properties.AppearanceDropDown ? Theme.Surface : Theme.SurfaceHover;
            appearance.ForeColor = Theme.Text; appearance.Font = Theme.Font(10);
            appearance.Options.UseBackColor = appearance.Options.UseForeColor = appearance.Options.UseFont = true;
        }
    }
    private void UpdateDurationEditors()
    {
        var custom = _duration.SelectedItem is DurationChoice { Minutes: 0, NeverExpires: false };
        _amount.Visible = _unit.Visible = custom; UpdateActions();
    }
    private bool TryDuration(out int minutes, out bool neverExpires)
    {
        minutes = 0;
        neverExpires = false;
        if (_duration.SelectedItem is not DurationChoice choice) return false;
        if (choice.NeverExpires) { neverExpires = true; return true; }
        if (choice.Minutes > 0) minutes = choice.Minutes;
        else
        {
            if (!decimal.TryParse(_amount.EditValue?.ToString(), out var amount) || amount != decimal.Truncate(amount)) return false;
            var multiplier = _unit.SelectedIndex switch { 0 => 1, 1 => 60, 2 => 1440, _ => 0 };
            var total = amount * multiplier;
            if (total < MinimumMinutes || total > MaximumMinutes) return false;
            minutes = (int)total;
        }
        return minutes is >= MinimumMinutes and <= MaximumMinutes;
    }
    private static bool IsActive(GroupInviteEntry entry) => GroupInviteLink.Active(entry.ExpiresAt, entry.NeverExpires);
    private static string DateText(DateTimeOffset value) => value.ToLocalTime().ToString("dd.MM.yyyy HH:mm", System.Globalization.CultureInfo.InvariantCulture);

    private bool Confirm(string prompt, bool all = false)
    {
        if (_confirmationForQa is not null) return _confirmationForQa(prompt);
        using var confirm = new GroupInviteDeletionConfirmationForm(prompt, all);
        return confirm.ShowDialog(this) == DialogResult.OK;
    }

    internal async Task LoadAsync()
    {
        if (_busy || IsDisposed) return;
        var preferred = Selected?.Id;
        var generation = BeginRequest(false, "Davetler yükleniyor…");
        // A failed permission refresh must not leave old capabilities visible.
        Bind([], null);
        try
        {
            var entries = await _api.ListGroupInvitesAsync(_group.Id, _lifetime.Token);
            if (!Current(generation)) return;
            ValidateEntries(entries); Bind(entries, preferred);
            SetStatus(entries.Count == 0 ? "Henüz davet bağlantısı yok." : $"{entries.Count} davet · {_entries.Count(IsActive)} aktif", false);
        }
        catch (Exception ex) { if (Current(generation)) SetStatus(ex.Message, true); }
        finally { EndRequest(generation); }
    }

    internal async Task CreateAsync()
    {
        if (_busy || IsDisposed || !RequireDuration(out var minutes, out var neverExpires)) return;
        var generation = BeginRequest(true, "Yeni davet hazırlanıyor…");
        try
        {
            var result = await _api.CreateManagedGroupInviteAsync(_group.Id, minutes, _lifetime.Token, neverExpires);
            if (!Current(generation)) return;
            ValidateEntries([result]);
            if (result.InviteUrl is null || result.CreatedAt is null || !IsActive(result) || result.NeverExpires != neverExpires || _entries.Any(e => e.Id == result.Id))
                throw new InvalidOperationException("Sunucudan yeni bir davet bağlantısı doğrulanamadı.");
            Bind(_entries.Append(result).ToArray(), result.Id);
            SetStatus("Yeni davet oluşturuldu. Diğer bağlantılar kullanılmaya devam eder.", false);
        }
        catch (Exception ex) { if (Current(generation)) { Bind([], null); SetStatus(ex.Message + " Listeyi yenileyerek kontrol et.", true); } }
        finally { EndRequest(generation); }
    }

    internal async Task ApplyDurationAsync()
    {
        if (_busy || IsDisposed || Selected is not { } selected || !RequireDuration(out var minutes, out var neverExpires)) return;
        var generation = BeginRequest(true, "Seçilen davetin süresi güncelleniyor…");
        try
        {
            var result = await _api.ChangeGroupInviteDurationAsync(_group.Id, selected.Id, minutes, _lifetime.Token, neverExpires);
            if (!Current(generation)) return;
            ValidateEntries([result]);
            if (result.Id != selected.Id || result.InviteUrl != selected.InviteUrl || result.CreatedAt != selected.CreatedAt ||
                !IsActive(result) || result.NeverExpires != neverExpires)
                throw new InvalidOperationException("Seçilen davetin güncellenen süresi doğrulanamadı.");
            Bind(_entries.Select(e => e.Id == selected.Id ? result : e).ToArray(), selected.Id);
            SetStatus("Seçilen davetin süresi güncellendi. Bağlantısı değişmedi.", false);
        }
        catch (Exception ex) { if (Current(generation)) { Bind([], null); SetStatus(ex.Message + " Listeyi yenileyerek kontrol et.", true); } }
        finally { EndRequest(generation); }
    }

    internal async Task DeleteAsync()
    {
        if (_busy || IsDisposed || Selected is not { } selected ||
            !Confirm("Yalnız seçilen davet bağlantısı silinecek. Diğer davetler ve mevcut üyeler korunur. Devam edilsin mi?")) return;
        var generation = BeginRequest(true, "Seçilen davet siliniyor…");
        try
        {
            await _api.DeleteGroupInviteAsync(_group.Id, selected.Id, _lifetime.Token);
            if (!Current(generation)) return;
            Bind(_entries.Where(e => e.Id != selected.Id).ToArray(), null);
            SetStatus("Seçilen davet silindi. Diğer bağlantılar ve üyeler korundu.", false);
        }
        catch (Exception ex) { if (Current(generation)) { Bind([], null); SetStatus(ex.Message + " Listeyi yenileyerek kontrol et.", true); } }
        finally { EndRequest(generation); }
    }

    internal async Task RevokeAsync()
    {
        if (_busy || IsDisposed || _entries.Count == 0 ||
            !Confirm("Bu grubun TÜM davet bağlantıları silinsin mi? Mevcut üyeler grupta kalır.", all: true)) return;
        var generation = BeginRequest(true, "Tüm davetler iptal ediliyor…");
        Bind([], null);
        try
        {
            await _api.RevokeGroupInviteAsync(_group.Id, _lifetime.Token);
            if (!Current(generation)) return;
            SetStatus("Tüm davetler iptal edildi. Mevcut üyeler değişmedi.", false);
        }
        catch (Exception ex) { if (Current(generation)) SetStatus(ex.Message + " Listeyi yenileyerek kontrol et.", true); }
        finally { EndRequest(generation); }
    }

    private void CopyLink()
    {
        if (_busy || Selected is not { InviteUrl: not null } selected || !IsActive(selected)) return;
        try { Clipboard.SetText(selected.InviteUrl); SetStatus("Davet bağlantısı kopyalandı. " + GroupInviteLink.ExpiryText(selected.ExpiresAt, selected.NeverExpires), false); }
        catch { SetStatus("Bağlantı panoya kopyalanamadı. Tekrar deneyebilirsin.", true); }
    }

    private void ValidateEntries(IReadOnlyList<GroupInviteEntry> entries)
    {
        if (entries is null || entries.Count > 1000 || entries.Select(e => e?.Id).Distinct().Count() != entries.Count ||
            entries.Any(e => e is null || e.Id == Guid.Empty || e.ConversationId != _group.Id ||
                !GroupInviteLink.ValidExpiry(e.ExpiresAt, e.NeverExpires) || e.CreatedAt is { } created &&
                    (created == default || created == DateTimeOffset.MaxValue || (!e.NeverExpires && created >= e.ExpiresAt)) ||
                e.InviteUrl is not null && !GroupInviteLink.TryParse(e.InviteUrl, out _)))
            throw new InvalidOperationException("Sunucudan geçerli bir grup davet listesi alınamadı.");
    }
    private void Bind(IEnumerable<GroupInviteEntry> entries, Guid? preferred)
    {
        var materialized = entries.OrderByDescending(e => e.CreatedAt ?? DateTimeOffset.MinValue).ThenBy(e => e.Id).ToArray();
        _binding = true; _view.BeginUpdate();
        try
        {
            _entries.Clear(); _entries.AddRange(materialized); _grid.DataSource = materialized.Select(e => new InviteRow(e)).ToArray();
            _view.FocusedRowHandle = preferred is { } id ? _view.LocateByValue(nameof(InviteRow.Id), id) : -1;
            FindNextExpiry();
        }
        finally { _view.EndUpdate(); _binding = false; }
        UpdateSelection();
    }
    private void FindNextExpiry()
    {
        var now = DateTimeOffset.UtcNow;
        _nextExpiry = _entries.Where(entry => !entry.NeverExpires && entry.ExpiresAt > now).Select(entry => (DateTimeOffset?)entry.ExpiresAt).Min();
    }
    private void RefreshExpiredRows()
    {
        // Do not repaint the entire grid every second. The timer only commits a
        // status change when the nearest live expiry actually crosses its deadline.
        if (_busy || IsDisposed || _nextExpiry is not { } expiry || expiry > DateTimeOffset.UtcNow) return;
        _view.RefreshData(); UpdateSelection(); FindNextExpiry();
    }
    private void UpdateSelection()
    {
        var selected = Selected;
        _link.Text = selected?.InviteUrl ?? "";
        _selectionDetails.Text = selected is null ? "Bir davet seç veya yeni bağlantı oluştur." : selected.InviteUrl is null
            ? selected.CreatedAt is null
                ? "Eski bağlantı yeniden gösterilemiyor. Süresini değiştirebilir veya silebilirsin."
                : "Bağlantı güvenli depodan açılamadı. Süresini değiştirebilir veya silebilirsin."
            : !IsActive(selected) ? "Bu davetin süresi doldu. Yeni süre uygulayabilir veya silebilirsin."
            : GroupInviteLink.ExpiryText(selected.ExpiresAt, selected.NeverExpires);
        UpdateActions();
    }
    private bool RequireDuration(out int minutes, out bool neverExpires)
    {
        if (TryDuration(out minutes, out neverExpires)) return true;
        SetStatus("Sınırsız seç veya 5 dakika ile 365 gün arasında tam sayı süre belirle.", true); return false;
    }
    private int BeginRequest(bool writing, string status)
    {
        _busy = true; _writing = writing; var generation = ++_generation;
        _status.ForeColor = Theme.Muted; _status.Text = status; UpdateActions(); return generation;
    }
    private bool Current(int generation) => !IsDisposed && !_lifetimeDisposed && generation == _generation;
    private void EndRequest(int generation)
    {
        if (!Current(generation)) return;
        _busy = _writing = false; UpdateActions();
    }
    private void SetStatus(string text, bool error) { _status.Text = text; _status.ForeColor = error ? Theme.Danger : Theme.Success; }
    private void UpdateActions()
    {
        var selected = Selected; var durationValid = TryDuration(out _, out _);
        _create.Enabled = !_busy && durationValid; _refresh.Enabled = !_busy;
        _apply.Enabled = !_busy && selected is not null && durationValid;
        _delete.Enabled = !_busy && selected is not null;
        _copy.Enabled = !_busy && selected is { InviteUrl: not null } && IsActive(selected);
        _revoke.Enabled = !_busy && _entries.Count > 0; _close.Enabled = !_writing;
        _grid.Enabled = _duration.Enabled = _amount.Enabled = _unit.Enabled = !_busy;
    }

    internal string LinkForQa => _link.Text;
    internal string StatusForQa => _status.Text;
    internal bool CopyEnabledForQa => _copy.Enabled;
    internal bool BusyForQa => _busy;
    internal int EntryCountForQa => _entries.Count;
    internal Guid? SelectedIdForQa => Selected?.Id;
    internal bool ApplyEnabledForQa => _apply.Enabled;
    internal bool DeleteEnabledForQa => _delete.Enabled;
    internal bool CreateEnabledForQa => _create.Enabled;
    internal string SelectionDetailsForQa => _selectionDetails.Text;
    internal IReadOnlyList<ModernButton> ActionButtonsForQa => [_refresh, _create, _apply, _copy, _delete, _revoke, _close];
    internal IReadOnlyList<Rectangle> ActionBoundsForQa => new[] { _refresh, _create, _apply, _copy, _delete, _revoke, _close }
        .Select(control => RectangleToClient(control.RectangleToScreen(control.ClientRectangle))).ToArray();
    internal Rectangle ContentViewportForQa => RectangleToClient(_viewport.RectangleToScreen(_viewport.ClientRectangle));
    internal bool InternalScrollForQa => _viewport.VerticalScroll.Visible;
    internal bool HorizontalScrollForQa => _viewport.HorizontalScroll.Visible;
    internal void SelectForQa(Guid id)
    {
        for (var handle = 0; handle < _view.DataRowCount; handle++)
            if (_view.GetRow(handle) is InviteRow row && row.Entry.Id == id) { _view.FocusedRowHandle = handle; UpdateSelection(); return; }
        _view.FocusedRowHandle = -1; UpdateSelection();
    }
    internal void SetDurationForQa(int amount, int unit = 0) { _duration.SelectedIndex = 4; _unit.SelectedIndex = unit; _amount.EditValue = amount; UpdateActions(); }
    internal void SetUnlimitedForQa() { _duration.SelectedIndex = 5; UpdateActions(); }
    internal bool CustomDurationVisibleForQa => _amount.Visible || _unit.Visible;
    internal string ExpiryCaptionForQa => (_view.GetFocusedRow() as InviteRow)?.Expires ?? "";
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_lifetimeDisposed)
        {
            _lifetimeDisposed = true; ++_generation; _expiryTimer.Stop(); _expiryTimer.Dispose(); _lifetime.Cancel(); _lifetime.Dispose();
        }
        base.Dispose(disposing);
    }
}

// Keeping the real confirmation as a form also lets visual regression exercise
// its layout without accepting an invitation deletion against a real server.
internal sealed class GroupInviteDeletionConfirmationForm : ModernForm
{
    private readonly ModernButton _cancel = Theme.Button("Vazgeç", ButtonKind.Secondary);
    private readonly ModernButton _delete;
    internal GroupInviteDeletionConfirmationForm(string prompt, bool all)
    {
        SuspendLayout();
        InviteFormLayout.Configure(this, all ? "MTK Chat · Tüm davetleri sil" : "MTK Chat · Davet linkini sil",
            new Size(570, 310), new Size(530, 290));
        var layout = InviteFormLayout.Rows(42, 0, 50);
        layout.Controls.Add(InviteFormLayout.Label(all ? "Tüm davet bağlantılarını sil" : "Davet bağlantısını sil", 17, bold: true), 0, 0);
        layout.Controls.Add(InviteFormLayout.Label(prompt, 10, Theme.Muted), 0, 1);
        var actions = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        actions.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        _delete = Theme.Button(all ? "Tüm Davet Linklerini Sil" : "Davet Linkini Sil", ButtonKind.Secondary);
        _delete.ForeColor = Theme.Danger;
        _cancel.Dock = _delete.Dock = DockStyle.Fill;
        _cancel.Margin = new Padding(0, 4, 0, 4); _delete.Margin = new Padding(8, 4, 0, 4);
        _cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        _delete.Click += (_, _) => { DialogResult = DialogResult.OK; Close(); };
        actions.Controls.Add(_cancel, 0, 0); actions.Controls.Add(_delete, 1, 0); layout.Controls.Add(actions, 0, 2);
        Controls.Add(layout); CancelButton = _cancel;
        // Enter does not turn a focused confirmation into an implicit deletion.
        ResumeLayout(performLayout: true);
    }
    internal IReadOnlyList<ModernButton> ActionButtonsForQa => [_cancel, _delete];
}

internal sealed class JoinGroupInviteForm : ModernForm
{
    private readonly ChatApiClient _api;
    private readonly Guid _actor;
    private readonly TextEdit _link = new();
    private readonly Label _name = InviteFormLayout.Label("", 15, bold: true);
    private readonly Label _details = InviteFormLayout.Label("", 9.5f, Theme.Muted);
    private readonly Label _status = InviteFormLayout.Label("", 9.5f, Theme.Muted);
    private readonly RoundedPanel _groupPreview = new() { Dock = DockStyle.Fill, FillColor = Theme.Surface,
        BorderColor = Theme.Divider, CornerRadius = 14, Padding = new Padding(16, 10, 16, 10), Margin = new Padding(0, 8, 0, 8) };
    private readonly ModernButton _previewButton = Theme.Button("Grubu önizle");
    private readonly ModernButton _join = Theme.Button("Gruba katıl", ButtonKind.Primary);
    private readonly ModernButton _close = Theme.Button("Vazgeç", ButtonKind.Secondary);
    private readonly CancellationTokenSource _lifetime = new();
    private GroupInvitePreview? _preview;
    private string? _verifiedToken;
    private bool _busy, _joining, _lifetimeDisposed;
    internal ConversationSummary? JoinedConversation { get; private set; }

    internal JoinGroupInviteForm(ChatApiClient api, Guid actor)
    {
        SuspendLayout();
        _api = api; _actor = actor;
        InviteFormLayout.Configure(this, "MTK Chat · Davetle katıl", new Size(690, 610), new Size(650, 580));
        _name.AutoEllipsis = true;
        var layout = InviteFormLayout.Rows(48, 54, 26, 64, 110, 0, 48);
        layout.Controls.Add(InviteFormLayout.Label("Davet bağlantısıyla katıl", 20, bold: true), 0, 0);
        layout.Controls.Add(InviteFormLayout.Label("mtkaya.me bağlantısını yapıştır. Önce grubu gör, ardından katılmayı onayla.", 10, Theme.Muted), 0, 1);
        layout.Controls.Add(InviteFormLayout.Label("Grup davet bağlantısı", 9.5f, Theme.Muted), 0, 2);
        _link.Properties.MaxLength = 512;
        layout.Controls.Add(InviteFormLayout.Editor(_link, false, "Katılmak için grup davet bağlantısı", "https://mtkaya.me/chat/invite/#…"), 0, 3);
        var groupText = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = Color.Transparent, Margin = Padding.Empty };
        groupText.RowStyles.Add(new RowStyle(SizeType.Absolute, 32)); groupText.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        groupText.Controls.Add(_name, 0, 0); groupText.Controls.Add(_details, 0, 1); _groupPreview.Controls.Add(groupText);
        _groupPreview.Visible = false; layout.Controls.Add(_groupPreview, 0, 4); layout.Controls.Add(_status, 0, 5);
        var actions = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1, Margin = Padding.Empty };
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100)); actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        actions.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 160)); actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 160));
        _close.Dock = _previewButton.Dock = _join.Dock = DockStyle.Fill; _join.Enabled = false; _previewButton.Enabled = false;
        _close.Click += (_, _) => Close(); _previewButton.Click += async (_, _) => await PreviewAsync();
        _join.Click += async (_, _) => await JoinAsync();
        actions.Controls.Add(_close, 0, 0); actions.Controls.Add(_previewButton, 2, 0); actions.Controls.Add(_join, 3, 0);
        layout.Controls.Add(actions, 0, 6); Controls.Add(layout); CancelButton = _close; AcceptButton = _previewButton;
        _link.EditValueChanged += (_, _) => { if (!_busy) { ClearPreview(); _status.Text = ""; UpdateActions(); } };
        FormClosing += (_, e) => { if (_joining) e.Cancel = true; };
        Shown += (_, _) => _link.Focus();
        ResumeLayout(performLayout: true);
    }

    internal async Task PreviewAsync()
    {
        if (_busy || IsDisposed) return;
        ClearPreview();
        if (!GroupInviteLink.TryParse(_link.Text, out var token))
        { _status.ForeColor = Theme.Danger; _status.Text = "Geçerli bir mtkaya.me grup davet bağlantısı yapıştır."; UpdateActions(); return; }
        SetBusy(true); _status.ForeColor = Theme.Muted; _status.Text = "Davet kontrol ediliyor…";
        try
        {
            var preview = await _api.PreviewGroupInviteAsync(token, _lifetime.Token);
            if (IsDisposed) return;
            if (preview.ConversationId == Guid.Empty || !GroupInviteLink.ValidGroupTitle(preview.Title) || !GroupInviteLink.Active(preview.ExpiresAt, preview.NeverExpires))
                throw new InvalidOperationException("Bu davet bağlantısı geçersiz veya süresi dolmuş.");
            _preview = preview; _verifiedToken = token;
            _name.Text = preview.Title; _details.Text = GroupInviteLink.ExpiryText(preview.ExpiresAt, preview.NeverExpires) + "\n" +
                (preview.AlreadyMember ? "Zaten bu grubun üyesisin." : "Katıldıktan sonraki mesajlar sana da şifrelenerek gönderilir.");
            _groupPreview.Visible = true; _join.Text = preview.AlreadyMember ? "Grubu aç" : "Gruba katıl";
            _status.Text = "";
        }
        catch (Exception ex) { if (!IsDisposed) { _status.ForeColor = Theme.Danger; _status.Text = ex.Message; } }
        finally { if (!IsDisposed) SetBusy(false); }
    }

    internal async Task JoinAsync()
    {
        if (_busy || IsDisposed || _preview is not { } preview ||
            !GroupInviteLink.TryParse(_link.Text, out var token) || token != _verifiedToken) return;
        if (!GroupInviteLink.Active(preview.ExpiresAt, preview.NeverExpires))
        { ClearPreview(); _status.ForeColor = Theme.Danger; _status.Text = "Davetin süresi doldu. Yeni bağlantı iste."; UpdateActions(); return; }
        _joining = true; SetBusy(true); _status.ForeColor = Theme.Muted; _status.Text = "Gruba katılınıyor…";
        try
        {
            var room = await _api.JoinGroupInviteAsync(token, _lifetime.Token);
            if (IsDisposed) return;
            if (room.Id != preview.ConversationId || room.Kind == "direct" || !GroupInviteLink.ValidGroupTitle(room.Title) ||
                !room.Participants.Any(user => user.Id == _actor))
                throw new InvalidOperationException("Grup üyeliği sunucudan doğrulanamadı.");
            JoinedConversation = room; _joining = false; _busy = false; DialogResult = DialogResult.OK; Close();
        }
        catch (Exception ex) { if (!IsDisposed) { _status.ForeColor = Theme.Danger; _status.Text = ex.Message; } }
        finally { _joining = false; if (!IsDisposed) SetBusy(false); }
    }

    private void ClearPreview()
    { _preview = null; _verifiedToken = null; _name.Text = _details.Text = ""; _groupPreview.Visible = false; _join.Text = "Gruba katıl"; }
    private void SetBusy(bool busy) { _busy = busy; _link.Enabled = !busy; _close.Enabled = !_joining; UpdateActions(); }
    private void UpdateActions()
    {
        _previewButton.Enabled = !_busy && GroupInviteLink.TryParse(_link.Text, out _);
        _join.Enabled = !_busy && _preview is { } preview && GroupInviteLink.Active(preview.ExpiresAt, preview.NeverExpires) &&
            GroupInviteLink.TryParse(_link.Text, out var token) && token == _verifiedToken;
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal string LinkForQa { get => _link.Text; set => _link.Text = value; }
    internal string StatusForQa => _status.Text;
    internal bool PreviewEnabledForQa => _previewButton.Enabled;
    internal bool JoinEnabledForQa => _join.Enabled;
    internal bool PreviewVisibleForQa => _groupPreview.Visible;
    internal string DetailsForQa => _details.Text;
    internal bool BusyForQa => _busy;
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_lifetimeDisposed) { _lifetimeDisposed = true; _lifetime.Cancel(); _lifetime.Dispose(); }
        base.Dispose(disposing);
    }
}
