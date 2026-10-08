using DevExpress.XtraEditors;
using DevExpress.XtraEditors.Controls;
using DevExpress.XtraGrid;
using DevExpress.XtraGrid.Views.Grid;
using MTKChat.Contracts;

namespace MTKChat.Desktop;

internal sealed class GroupManagementForm : ModernForm
{
    private sealed record Choice(GroupManagementView Group) { public override string ToString() => Group.Title; }
    private sealed record MemberRow(GroupMemberView Member)
    {
        public string Name => Member.User.DisplayName;
        public string Role => UserPresentation.Role(Member.User, Member.GroupRole);
        public string Status => Member.Moderation.IsBanned ? "Grupta yasaklı" : Member.Moderation.IsMuted ? "Susturuldu" : "Aktif";
    }
    private readonly ChatApiClient _api;
    private readonly ChatUser _me;
    private readonly Guid? _initialRoom;
    private readonly ComboBoxEdit _groups = new();
    private readonly ModernButton _rename = Theme.Button("Adı değiştir", ButtonKind.Secondary);
    private readonly ModernButton _remove = Theme.Button("Gruptan çıkar", ButtonKind.Secondary);
    private readonly GridControl _grid = new();
    private readonly GridView _view;
    private readonly Label _status = new();
    private readonly List<ModernButton> _roleButtons = new();
    private readonly List<ModernButton> _moderationButtons = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Func<string, bool>? _confirmRemovalForQa;
    private bool _busy;
    private bool _loadingGroups;
    private bool _lifetimeDisposed;
    private GroupManagementView? Current => (_groups.SelectedItem as Choice)?.Group;

    internal GroupManagementForm(ChatApiClient api, ChatUser me, Guid? initialRoom, bool snapshot = false,
        Func<string, bool>? confirmRemovalForQa = null)
    {
        SuspendLayout();
        _api = api; _me = me; _initialRoom = initialRoom; _confirmRemovalForQa = confirmRemovalForQa;
        Text = "MTK Chat · Grup yönetimi"; Icon = Theme.AppIcon();
        Size = new Size(820, 640); MinimumSize = new Size(720, 590);
        BackColor = Theme.Canvas; ForeColor = Theme.Text; Font = Theme.Font(10);
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi; AutoScaleDimensions = new SizeF(96, 96);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5,
            Padding = new Padding(28), BackColor = Theme.Canvas };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 55));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 116));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        layout.Controls.Add(new Label { Text = "Grup yönetimi", Dock = DockStyle.Fill, Font = Theme.Font(23, FontStyle.Bold), ForeColor = Theme.Text }, 0, 0);
        _groups.Dock = DockStyle.Fill; _groups.Margin = new Padding(0, 0, 0, 12);
        _groups.Properties.TextEditStyle = TextEditStyles.DisableTextEditor;
        _groups.Properties.AutoHeight = false;
        _groups.Properties.Appearance.BackColor = Theme.Surface; _groups.Properties.Appearance.ForeColor = Theme.Text;
        _groups.Properties.Appearance.Font = Theme.Font(11);
        _groups.Properties.Appearance.Options.UseBackColor = true;
        _groups.Properties.Appearance.Options.UseForeColor = true;
        _groups.Properties.BorderStyle = BorderStyles.NoBorder;
        _groups.Properties.AppearanceDropDown.BackColor = Theme.Surface;
        _groups.Properties.AppearanceDropDown.ForeColor = Theme.Text;
        _groups.Properties.AppearanceDropDown.Font = Theme.Font(11);
        _groups.Properties.AppearanceDropDown.Options.UseBackColor = true;
        _groups.Properties.AppearanceDropDown.Options.UseForeColor = true;
        _groups.Properties.AppearanceDropDown.Options.UseFont = true;
        _groups.Properties.AppearanceItemHighlight.BackColor = Theme.SurfaceHover;
        _groups.Properties.AppearanceItemHighlight.ForeColor = Theme.Text;
        _groups.Properties.AppearanceItemHighlight.Options.UseBackColor = true;
        _groups.Properties.AppearanceItemHighlight.Options.UseForeColor = true;
        _groups.Properties.AppearanceItemSelected.BackColor = Theme.SurfaceHover;
        _groups.Properties.AppearanceItemSelected.ForeColor = Theme.Text;
        _groups.Properties.AppearanceItemSelected.Options.UseBackColor = true;
        _groups.Properties.AppearanceItemSelected.Options.UseForeColor = true;
        _groups.SelectedIndexChanged += (_, _) => { if (!_loadingGroups) Render(); };
        var groupHeader = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        // Match the containing DPI-scaled row instead of retaining the button's
        // design height through an implicit AutoSize row at 125% scaling.
        groupHeader.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        groupHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        groupHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 136));
        _rename.Dock = DockStyle.Fill; _rename.Margin = new Padding(10, 0, 0, 12);
        _rename.Click += async (_, _) => await RenameAsync();
        groupHeader.Controls.Add(_groups, 0, 0); groupHeader.Controls.Add(_rename, 1, 0);
        layout.Controls.Add(groupHeader, 0, 1);
        _view = new GridView(_grid); _grid.MainView = _view; _grid.Dock = DockStyle.Fill;
        // Disable the skin's light selected/header painting before applying the app palette.
        _grid.LookAndFeel.UseDefaultLookAndFeel = false;
        _grid.LookAndFeel.Style = DevExpress.LookAndFeel.LookAndFeelStyle.Flat;
        _view.BorderStyle = BorderStyles.NoBorder;
        _view.OptionsSelection.EnableAppearanceFocusedCell = false;
        _view.OptionsBehavior.Editable = false; _view.OptionsView.ShowGroupPanel = false;
        _view.OptionsView.ShowIndicator = false; _view.OptionsView.RowAutoHeight = true;
        _view.Appearance.Row.BackColor = Theme.Surface; _view.Appearance.Row.ForeColor = Theme.Text;
        _view.Appearance.Row.Font = Theme.Font(11); _view.RowHeight = 40;
        _view.Appearance.Row.Options.UseBackColor = true;
        _view.Appearance.Row.Options.UseForeColor = true;
        _view.Appearance.Row.Options.UseFont = true;
        _view.Appearance.Empty.BackColor = Theme.Surface;
        _view.Appearance.Empty.Options.UseBackColor = true;
        _view.Appearance.HeaderPanel.BackColor = Theme.Sidebar; _view.Appearance.HeaderPanel.ForeColor = Theme.Muted;
        _view.Appearance.HeaderPanel.Options.UseBackColor = true;
        _view.Appearance.HeaderPanel.Options.UseForeColor = true;
        _view.Appearance.HeaderPanel.BorderColor = Theme.Divider;
        _view.Appearance.HeaderPanel.Options.UseBorderColor = true;
        _view.Appearance.HorzLine.BackColor = Theme.Divider;
        _view.Appearance.HorzLine.Options.UseBackColor = true;
        _view.Appearance.VertLine.BackColor = Theme.Divider;
        _view.Appearance.VertLine.Options.UseBackColor = true;
        foreach (var appearance in new[] { _view.Appearance.FocusedRow, _view.Appearance.SelectedRow, _view.Appearance.HideSelectionRow })
        {
            appearance.BackColor = Theme.SurfaceHover; appearance.ForeColor = Theme.Text;
            appearance.Options.UseBackColor = true; appearance.Options.UseForeColor = true;
        }
        _view.Columns.AddVisible(nameof(MemberRow.Name), "Kullanıcı");
        _view.Columns.AddVisible(nameof(MemberRow.Role), "Grup rolü");
        _view.Columns.AddVisible(nameof(MemberRow.Status), "Durum");
        _view.FocusedRowChanged += (_, _) => UpdateButtons();
        _view.RowCellStyle += (_, e) => { if (_view.GetRow(e.RowHandle) is MemberRow row && e.Column.FieldName == nameof(MemberRow.Role)) e.Appearance.ForeColor = UserPresentation.RoleColor(row.Member.User, row.Member.GroupRole); };
        layout.Controls.Add(_grid, 0, 2);
        // A fixed two-row grid keeps the destructive action aligned with the
        // other buttons. Flow wrapping previously added a third clipped row on
        // narrow/DPI-scaled windows and made removal look like floating text.
        var actions = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Theme.Canvas,
            Padding = new Padding(0, 10, 0, 0), Margin = Padding.Empty, ColumnCount = 4, RowCount = 2 };
        for (var column = 0; column < 4; column++) actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        actions.RowStyles.Add(new RowStyle(SizeType.Percent, 50)); actions.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        var actionIndex = 0;
        void Add(string text, string operation, bool role)
        {
            var button = Theme.Button(text, ButtonKind.Secondary); button.Dock = DockStyle.Fill;
            button.Margin = new Padding(0, 0, 8, 8); button.Enabled = false;
            button.Click += async (_, _) => await ApplyAsync(operation, role);
            (role ? _roleButtons : _moderationButtons).Add(button);
            actions.Controls.Add(button, actionIndex % 4, actionIndex / 4); actionIndex++;
        }
        Add("Yönetici yap", "admin", true); Add("Mod yap", "mod", true); Add("Üye yap", "user", true);
        Add("10 dk sustur", "mute", false); Add("Susturmayı kaldır", "unmute", false);
        Add("Grupta yasakla", "ban", false); Add("Yasağı kaldır", "unban", false);
        _remove.Dock = DockStyle.Fill; _remove.Margin = new Padding(0, 0, 8, 8);
        _remove.ForeColor = Theme.Danger; _remove.Enabled = false;
        _remove.Click += async (_, _) => await RemoveMemberAsync();
        actions.Controls.Add(_remove, 3, 1);
        layout.Controls.Add(actions, 0, 3);
        _status.Dock = DockStyle.Fill; _status.ForeColor = Theme.Muted; _status.Font = Theme.Font(9); _status.AutoEllipsis = true;
        layout.Controls.Add(_status, 0, 4); Controls.Add(layout);
        if (snapshot)
        {
            var member = new ChatUser(Guid.NewGuid(), "Ayşe Demir", "", false, null);
            SetGroups([new(Guid.NewGuid(), "Tasarım ekibi", "admin", [
                new(me, "admin", new(me.Id, false, null, false)),
                new(member, "mod", new(member.Id, false, null, false)),
                new(member with { Id = Guid.NewGuid(), DisplayName = "Mehmet Kaya" }, "user", new(Guid.NewGuid(), false, null, false))], me.Role == "admin")]);
        }
        else Shown += async (_, _) =>
        {
            SetBusy(true);
            try { await LoadAsync(); }
            finally { if (!IsDisposed) SetBusy(false); }
        };
        // A deliberate write keeps its captured group/target until the result is
        // known. Disposal, unlike an accidental close, cancels pending requests.
        FormClosing += (_, e) => { if (_busy) e.Cancel = true; };
        ResumeLayout(performLayout: true);
    }

    private async Task LoadAsync(Guid? preferredRoom = null)
    {
        try { SetGroups(await _api.GetManageableGroupsAsync(_lifetime.Token), preferredRoom); }
        catch (Exception ex) { if (!IsDisposed) { _status.ForeColor = Theme.Danger; _status.Text = ex.Message; } }
    }
    private void SetGroups(IReadOnlyList<GroupManagementView> groups, Guid? preferredRoom = null)
    {
        if (IsDisposed) return;
        var preferred = preferredRoom ?? Current?.Id ?? _initialRoom;
        _loadingGroups = true;
        try
        {
            _groups.Properties.Items.Clear();
            foreach (var group in groups) _groups.Properties.Items.Add(new Choice(group));
            _groups.SelectedIndex = groups.Count == 0 ? -1 : Math.Max(0, groups.ToList().FindIndex(g => g.Id == preferred));
        }
        finally { _loadingGroups = false; }
        Render();
    }
    private void Render()
    {
        _grid.DataSource = Current?.Members.Select(m => new MemberRow(m)).ToArray() ?? [];
        _status.ForeColor = Theme.Muted;
        _status.Text = Current is null ? "Yönetebileceğiniz bir grup bulunmuyor." :
            $"Yetkiniz: {(Current.IsSiteAdmin ? "Site admini" : Current.MyRole == "admin" ? "Grup yöneticisi" : "Mod")}";
        UpdateButtons();
    }
    private void UpdateButtons()
    {
        _rename.Enabled = !_busy && Current is { } current && (current.IsSiteAdmin || current.MyRole == "admin");
        var target = (_view.GetFocusedRow() as MemberRow)?.Member;
        var valid = !_busy && Current is not null && target is not null && target.User.Id != _me.Id &&
            target.User.Role != "admin" && !target.User.IsAgent;
        foreach (var button in _roleButtons) button.Enabled = valid && Current!.MyRole == "admin";
        var moderate = valid && (Current!.IsSiteAdmin || Current.MyRole == "admin" && target!.GroupRole != "admin" ||
            Current.MyRole == "mod" && target!.GroupRole == "user");
        foreach (var button in _moderationButtons) button.Enabled = moderate;
        _remove.Enabled = !_busy && CanRemoveMember(_me, Current, target);
    }

    internal static bool CanRemoveMember(ChatUser me, GroupManagementView? room, GroupMemberView? target) =>
        !me.IsAgent && room is not null && target is not null && target.User.Id != me.Id &&
        !target.User.IsAgent && target.User.Role != "admin" &&
        room.Members.Any(member => member.User.Id == target.User.Id) &&
        !room.Members.Any(member => member.User.Id == me.Id && member.Moderation.IsBanned) &&
        (room.IsSiteAdmin && me.Role == "admin" || room.MyRole == "admin" &&
            room.Members.Any(member => member.User.Id == me.Id) && target.GroupRole != "admin");

    private void SetBusy(bool busy)
    {
        _busy = busy; _groups.Enabled = _grid.Enabled = !busy; UpdateButtons();
    }

    internal async Task RemoveMemberAsync()
    {
        if (_busy || IsDisposed || Current is not { } room ||
            (_view.GetFocusedRow() as MemberRow)?.Member is not { } target || !CanRemoveMember(_me, room, target)) return;
        // Capture both identifiers before any dialog/await. Focus or a group-list
        // refresh must never redirect a destructive action to another participant.
        var groupId = room.Id; var targetId = target.User.Id;
        SetBusy(true);
        try
        {
            var prompt = $"{target.User.DisplayName} adlı kullanıcı {room.Title} grubundan çıkarılsın mı?";
            var confirmed = _confirmRemovalForQa?.Invoke(prompt);
            if (confirmed is null)
            {
                using var confirmation = new GroupMemberRemovalConfirmationForm(prompt);
                confirmed = confirmation.ShowDialog(this) == DialogResult.OK;
            }
            if (confirmed != true || IsDisposed) return;
            _status.ForeColor = Theme.Muted; _status.Text = "Üye gruptan çıkarılıyor…";
            await _api.RemoveGroupMemberAsync(groupId, targetId, _lifetime.Token);
            if (IsDisposed) return;
            // A successful write and a failed reload are distinct outcomes. Clear
            // the departed row immediately so a reload error cannot offer a retry
            // against a user who has already been removed.
            var groups = _groups.Properties.Items.Cast<Choice>().Select(choice => choice.Group.Id == groupId
                ? choice.Group with { Members = choice.Group.Members.Where(member => member.User.Id != targetId).ToArray() }
                : choice.Group).ToArray();
            SetGroups(groups, groupId);
            _status.ForeColor = Theme.Success; _status.Text = "Kullanıcı gruptan çıkarıldı. Hesabı ve diğer sohbetleri değişmedi.";
            try
            {
                SetGroups(await _api.GetManageableGroupsAsync(_lifetime.Token), groupId);
                if (!IsDisposed) { _status.ForeColor = Theme.Success; _status.Text = "Kullanıcı gruptan çıkarıldı. Hesabı ve diğer sohbetleri değişmedi."; }
            }
            catch (Exception ex)
            {
                if (!IsDisposed) { _status.ForeColor = Theme.Warning; _status.Text = "Kullanıcı çıkarıldı; üye listesi yenilenemedi. " + ex.Message; }
            }
        }
        catch (Exception ex)
        {
            if (!IsDisposed) { _status.ForeColor = Theme.Danger; _status.Text = ex.Message; }
        }
        finally { if (!IsDisposed) SetBusy(false); }
    }
    private async Task RenameAsync()
    {
        if (_busy || Current is not { } room || !(room.IsSiteAdmin || room.MyRole == "admin")) return;
        SetBusy(true);
        try
        {
            // Management metadata does not make a nonmember site admin a reader.
            var group = new ConversationSummary(room.Id, room.Title, room.Members.Select(member => member.User).ToArray(),
                null, null, 0, GroupRoles: room.Members.ToDictionary(member => member.User.Id, member => member.GroupRole));
            using var editor = new GroupTitleEditorForm(_api, group);
            if (editor.ShowDialog(this) == DialogResult.OK && !IsDisposed) await LoadAsync(room.Id);
        }
        finally { if (!IsDisposed) SetBusy(false); }
    }
    internal bool RenameEnabledForQa => _rename.Enabled;
    internal void SetGroupsForQa(IReadOnlyList<GroupManagementView> groups) => SetGroups(groups);
    internal bool RemoveEnabledForQa => _remove.Enabled;
    internal IReadOnlyList<ModernButton> ActionButtonsForQa => [_rename, .. _roleButtons, .. _moderationButtons, _remove];
    internal bool BusyForQa => _busy;
    internal string StatusForQa => _status.Text;
    internal Guid? CurrentRoomForQa => Current?.Id;
    internal Guid[] MembersForQa => Current?.Members.Select(member => member.User.Id).ToArray() ?? [];
    internal void SelectMemberForQa(Guid id)
    {
        for (var row = 0; row < _view.DataRowCount; row++)
            if (_view.GetRow(row) is MemberRow member && member.Member.User.Id == id) { _view.FocusedRowHandle = row; break; }
        UpdateButtons();
    }
    private async Task ApplyAsync(string operation, bool role)
    {
        if (_busy || Current is not { } room || _view.GetFocusedRow() is not MemberRow target) return;
        SetBusy(true);
        try
        {
            if (role) await _api.SetGroupRoleAsync(room.Id, target.Member.User.Id, operation, _lifetime.Token);
            else await _api.ModerateGroupAsync(room.Id, target.Member.User.Id, operation, operation == "mute" ? 10 : null, _lifetime.Token);
            if (!IsDisposed) await LoadAsync(room.Id);
        }
        catch (Exception ex) { if (!IsDisposed) { _status.ForeColor = Theme.Danger; _status.Text = ex.Message; } }
        finally { if (!IsDisposed) SetBusy(false); }
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_lifetimeDisposed) { _lifetimeDisposed = true; _lifetime.Cancel(); _lifetime.Dispose(); }
        base.Dispose(disposing);
    }
}

internal sealed class GroupMemberRemovalConfirmationForm : ModernForm
{
    internal GroupMemberRemovalConfirmationForm(string prompt)
    {
        SuspendLayout();
        InviteFormLayout.Configure(this, "MTK Chat · Üyeyi çıkar", new Size(540, 340), new Size(480, 320));
        var layout = InviteFormLayout.Rows(50, 0, 60, 50);
        layout.Controls.Add(InviteFormLayout.Label("Üyeyi gruptan çıkar", 19, bold: true), 0, 0);
        layout.Controls.Add(InviteFormLayout.Label(prompt, 10.5f), 0, 1);
        layout.Controls.Add(InviteFormLayout.Label("Bu işlem hesabı silmez veya yasaklamaz. Kullanıcı yeniden geçerli bir davetle katılabilir.", 9.5f, Theme.Muted), 0, 2);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft,
            Margin = Padding.Empty, BackColor = Theme.Sidebar };
        var remove = Theme.Button("Gruptan çıkar", ButtonKind.Secondary); remove.Width = 138; remove.ForeColor = Theme.Danger;
        var cancel = Theme.Button("Vazgeç", ButtonKind.Secondary); cancel.Width = 100;
        remove.Margin = cancel.Margin = new Padding(0, 4, 8, 4);
        remove.Click += (_, _) => { DialogResult = DialogResult.OK; Close(); };
        cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        actions.Controls.Add(remove); actions.Controls.Add(cancel); layout.Controls.Add(actions, 0, 3);
        Controls.Add(layout); CancelButton = cancel;
        // Escape or window close cancels. Enter does not silently accept removal.
        ResumeLayout(performLayout: true);
    }
}
