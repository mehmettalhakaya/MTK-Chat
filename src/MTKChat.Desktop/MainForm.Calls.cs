using MTKChat.Contracts;
using DevExpress.XtraEditors;
using DevExpress.XtraEditors.Controls;

namespace MTKChat.Desktop;

internal sealed partial class MainForm
{
    private CallForm? _callForm;
    private bool _checkingCalls, _startingCall;
    private readonly HashSet<(Guid User, Guid Call)> _seenIncomingCalls = [];
    private readonly ModernButton _callButton = Theme.GlyphButton("\uE717", "Sesli arama başlat");

    private async Task StartCallAsync(Guid? target = null)
    {
        if (_callForm is { IsDisposed: false }) { _callForm.Activate(); return; }
        if (_session is null || _selectedConversation is null || _startingCall) return;
        var conversation = _selectedConversation;
        var session = _session;
        // Reserve the start flow before any await/dialog; a second click or
        // incoming-call refresh must not race the same outgoing invitation.
        _startingCall = true;
        _callButton.Enabled = false;
        try
        {
            if (_voiceRecorder is not null) await StopVoiceRecordingAsync();
            var users = conversation.Participants.Where(p => !p.IsAgent && p.Id != session.User.Id).ToArray();
            Guid[] invitees;
            if (target is { } id)
            {
                if (!users.Any(u => u.Id == id)) return;
                invitees = [id];
            }
            else if (conversation.Kind == "direct" && users.Length == 1) invitees = [users[0].Id];
            else
            {
                using var picker = new CallInviteForm(users, _avatars);
                if (picker.ShowDialog(this) != DialogResult.OK) return;
                invitees = picker.Selected;
            }
            if (IsDisposed || _session != session || _selectedConversation?.Id != conversation.Id) return;
            var call = await _api.StartCallAsync(new StartCallRequest(conversation.Id, invitees));
            if (IsDisposed || _session != session)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                try { await _api.LeaveCallAsync(call.Id, timeout.Token); } catch { /* The unanswered call also expires. */ }
                return;
            }
            ShowCall(call, false);
        }
        catch (Exception ex) { if (!IsDisposed && _session == session) ShowError(ex.Message); }
        finally { _startingCall = false; if (!_callButton.IsDisposed) _callButton.Enabled = true; }
    }
    private void ShowCall(CallView call, bool incoming)
    {
        if (IsDisposed || _session is null || _callForm is { IsDisposed: false }) return;
        if (incoming) _seenIncomingCalls.Add((_session.User.Id, call.Id));
        var form = new CallForm(_api, _identity, _session.User.Id, call, incoming, prepare: async () =>
        {
            if (_voiceRecorder is not null) await StopVoiceRecordingAsync();
        }, avatars: _avatars);
        _callForm = form;
        form.FormClosed += (_, _) => { if (ReferenceEquals(_callForm, form)) _callForm = null; };
        form.Show(this);
    }
    private async Task RefreshCallsAsync()
    {
        if (_session is null || _checkingCalls || _startingCall || _callForm is { IsDisposed: false }) return;
        _checkingCalls = true;
        var session = _session;
        try
        {
            var calls = await _api.GetCallsAsync();
            if (IsDisposed || _session != session) return;
            _seenIncomingCalls.RemoveWhere(entry => entry.User != session.User.Id || !calls.Any(call => call.Id == entry.Call));
            if (!IsDisposed && _session == session && !_startingCall && _callForm is not { IsDisposed: false } &&
                calls.FirstOrDefault(c => c.CallerId != session.User.Id && !_seenIncomingCalls.Contains((session.User.Id, c.Id))) is { } call)
                ShowCall(call, true);
        }
        catch (Exception exception) { RecordNetworkFailure(exception); }
        finally { _checkingCalls = false; }
    }
    private void AddPersonActions(Control card, PresenceView person)
    {
        if (person.User.IsAgent || person.User.Id == _session?.User.Id) return;
        var menu = Theme.ContextMenu();
        menu.Items.Add("Mesaj gönder", null, async (_, _) => await OpenDirectAsync(person.User.Id));
        if (person.IsInConversation) menu.Items.Add("Sesli ara", null, async (_, _) => await StartCallAsync(person.User.Id));
        if (_selectedConversation is { Kind: not "direct" } group && person.IsInConversation &&
            (_session?.User.Role == "admin" || GroupRole(_session!.User.Id) == "admin") && person.User.Role != "admin")
        {
            var roles = new ToolStripMenuItem("Grup rolü");
            foreach (var (label, role) in new[] { ("Grup yöneticisi", "admin"), ("Mod", "mod"), ("User", "user") })
            {
                var item = new ToolStripMenuItem(label) { Checked = person.GroupRole == role,
                    ForeColor = UserPresentation.RoleColor(person.User, role) };
                roles.DropDownItems.Add(item);
                item.Click += async (_, _) => await ChangeGroupRoleAsync(group.Id, person.User.Id, role);
            }
            menu.Items.Add(roles);
        }
        var blockAction = menu.Items.Add("Engelle", null, async (_, _) =>
        {
            try
            {
                var blocked = (await _api.GetBlockedUsersAsync()).Contains(person.User.Id);
                await _api.SetBlockedAsync(person.User.Id, !blocked);
                if (IsDisposed) return;
                    InvalidateConversationVisibility(preserveActivity: false);
                await RefreshMessagesAsync(silent: true);
                await LoadConversationsAsync();
            }
            catch (Exception ex) { ShowError(ex.Message); }
        });
        menu.Items.Insert(menu.Items.IndexOf(blockAction), new ToolStripSeparator());
        blockAction.ForeColor = Theme.Danger;
        menu.Opening += async (_, _) =>
        {
            blockAction.Enabled = false;
            try
            {
                var blocked = await _api.GetBlockedUsersAsync();
                if (!menu.IsDisposed)
                {
                    var isBlocked = blocked.Contains(person.User.Id);
                    blockAction.Text = isBlocked ? "Engeli kaldır" : "Engelle";
                    blockAction.ForeColor = isBlocked ? Theme.Text : Theme.Danger;
                    blockAction.Enabled = true;
                }
            }
            catch { /* Do not offer a toggle based on an unknown block state. */ }
        };
        card.ContextMenuStrip = menu;
        foreach (Control child in card.Controls) child.ContextMenuStrip = menu;
        card.Cursor = Cursors.Hand;
        AttachClick(card, () => { menu.Show(card, new Point(10, card.Height - 2)); return Task.CompletedTask; });
        card.Disposed += (_, _) => menu.Dispose();
    }
}

internal sealed class CallInviteForm : ModernForm
{
    private readonly ChatUser[] _users;
    private readonly HashSet<Guid> _selected = [];
    private readonly PickerPeopleViewport _people;
    private readonly TextEdit _search = new();
    private readonly Label _summary = new();
    private readonly ModernButton _start = Theme.Button("Aramayı başlat", ButtonKind.Primary);
    internal Guid[] Selected => _users.Where(u => _selected.Contains(u.Id)).Select(u => u.Id).ToArray();
    internal CallInviteForm(IEnumerable<ChatUser> users, AvatarCache? avatars = null)
    {
        SuspendLayout();
        _users = users.Where(u => !u.IsAgent).DistinctBy(u => u.Id).OrderBy(u => u.DisplayName).ToArray();
        Text = "MTK Chat · Kişileri ara"; Icon = Theme.AppIcon();
        AutoScaleMode = AutoScaleMode.Dpi; AutoScaleDimensions = new SizeF(96, 96);
        Size = new Size(490, 610); MinimumSize = new Size(420, 480); MaximizeBox = MinimizeBox = false;
        BackColor = Theme.Canvas; StartPosition = FormStartPosition.CenterParent;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24), RowCount = 5, ColumnCount = 1, BackColor = Theme.Canvas };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 66)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
        layout.Controls.Add(new Label { Text = "Kimi arayalım?", Dock = DockStyle.Fill, ForeColor = Theme.Text, Font = Theme.Font(21, FontStyle.Bold) }, 0, 0);
        var searchSurface = new RoundedPanel { Dock = DockStyle.Fill, FillColor = Theme.Surface, BorderColor = Theme.Divider,
            CornerRadius = 12, Padding = new Padding(12, 2, 12, 2), Margin = new Padding(0, 0, 0, 10) };
        _search.Dock = DockStyle.Fill; _search.Properties.BorderStyle = BorderStyles.NoBorder; _search.Properties.AutoHeight = false;
        _search.Properties.NullValuePrompt = "Kişilerde ara"; _search.AccessibleName = "Aranacak kişileri bul";
        _search.Properties.Appearance.BackColor = Theme.Surface; _search.Properties.Appearance.ForeColor = Theme.Text;
        _search.Properties.Appearance.Font = Theme.Font(10); searchSurface.Controls.Add(_search); layout.Controls.Add(searchSurface, 0, 1);
        _people = new PickerPeopleViewport(true, avatars, id => _selected.Contains(id), SelectPerson) { Dock = DockStyle.Fill, Margin = Padding.Empty };
        layout.Controls.Add(_people, 0, 2); _search.TextChanged += (_, _) => FilterPeople();
        _summary.Dock = DockStyle.Fill; _summary.Font = Theme.Font(9); _summary.ForeColor = Theme.Muted;
        _summary.TextAlign = ContentAlignment.MiddleLeft; layout.Controls.Add(_summary, 0, 3);
        _start.Dock = DockStyle.Fill; _start.Click += (_, _) =>
        {
            if (_selected.Count is < 1 or > 5) return;
            DialogResult = DialogResult.OK; Close();
        };
        layout.Controls.Add(_start, 0, 4); Controls.Add(layout); AcceptButton = _start;
        if (_users.Length == 1) _selected.Add(_users[0].Id);
        FilterPeople(); UpdateSummary();
        Shown += (_, _) =>
        {
            var area = Screen.FromControl(this).WorkingArea;
            Size = new Size(Math.Min(Width, area.Width), Math.Min(Height, area.Height));
            Location = new Point(Math.Clamp(Left, area.Left, Math.Max(area.Left, area.Right - Width)), Math.Clamp(Top, area.Top, Math.Max(area.Top, area.Bottom - Height)));
        };
        ResumeLayout(true);
    }
    private void FilterPeople() => _people.SetUsers(_users.Where(u => u.DisplayName.Contains(_search.Text.Trim(), StringComparison.CurrentCultureIgnoreCase)).ToArray());
    internal void SelectPerson(Guid id, bool selected)
    {
        if (!_users.Any(u => u.Id == id)) return;
        if (selected && !_selected.Contains(id) && _selected.Count >= 5)
        { _summary.Text = "Bir aramaya en fazla 5 kişi davet edebilirsin."; _summary.ForeColor = Theme.Warning; _people.RefreshSelection(); return; }
        if (selected) _selected.Add(id); else _selected.Remove(id);
        _people.RefreshSelection(); UpdateSummary();
    }
    private void UpdateSummary()
    { _summary.ForeColor = Theme.Muted; _summary.Text = _users.Length == 0 ? "Aranabilecek kişi yok." : $"{_selected.Count}/5 kişi seçildi"; _start.Enabled = _selected.Count is >= 1 and <= 5; }
}
