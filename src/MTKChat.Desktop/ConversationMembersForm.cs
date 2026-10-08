using DevExpress.XtraEditors.Controls;
using DevExpress.XtraGrid;
using DevExpress.XtraGrid.Views.Grid;
using MTKChat.Contracts;

namespace MTKChat.Desktop;

// This is a read-only member view, not the global or group administration panel.
internal sealed class ConversationMembersForm : ModernForm
{
    private sealed record Member(PresenceView Person)
    {
        public string Name => Person.User.DisplayName;
        public string Role => UserPresentation.Role(Person.User, Person.GroupRole);
        public string Status => UserPresentation.PresenceText(Person);
    }
    private readonly ChatApiClient _api;
    private readonly ConversationSummary _conversation;
    private readonly GridControl _grid = new();
    private readonly Label _status = new();
    private readonly ModernButton _refresh = Theme.Button("Yenile", ButtonKind.Secondary);

    internal ConversationMembersForm(ChatApiClient api, ConversationSummary conversation, bool snapshot = false)
    {
        _api = api; _conversation = conversation;
        Text = conversation.Kind == "direct" ? "MTK Chat · Sohbet bilgileri" : "MTK Chat · Grup üyeleri";
        Icon = Theme.AppIcon(); Size = new Size(700, 580); MinimumSize = new Size(580, 480);
        BackColor = Theme.Canvas; ForeColor = Theme.Text; Font = Theme.Font(10);
        StartPosition = FormStartPosition.CenterParent;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24), ColumnCount = 1, RowCount = 3 };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 66));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        var header = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Canvas };
        var title = new Label { Text = conversation.Title, Dock = DockStyle.Fill, AutoEllipsis = true,
            Font = Theme.Font(22, FontStyle.Bold), ForeColor = Theme.Text };
        _refresh.Dock = DockStyle.Right; _refresh.Width = 88;
        _refresh.Click += async (_, _) => await LoadAsync();
        header.Controls.Add(title); header.Controls.Add(_refresh); layout.Controls.Add(header, 0, 0);
        _grid.Dock = DockStyle.Fill;
        _grid.LookAndFeel.UseDefaultLookAndFeel = false;
        _grid.LookAndFeel.Style = DevExpress.LookAndFeel.LookAndFeelStyle.Flat;
        var view = new GridView(_grid) { BorderStyle = BorderStyles.NoBorder, RowHeight = 42 };
        _grid.MainView = view;
        view.OptionsBehavior.Editable = false; view.OptionsView.ShowGroupPanel = false;
        view.OptionsView.ShowIndicator = false; view.OptionsSelection.EnableAppearanceFocusedCell = false;
        foreach (var appearance in new[] { view.Appearance.Row, view.Appearance.Empty, view.Appearance.HeaderPanel,
            view.Appearance.FocusedRow, view.Appearance.SelectedRow, view.Appearance.HideSelectionRow })
        {
            appearance.BackColor = appearance == view.Appearance.FocusedRow || appearance == view.Appearance.SelectedRow
                ? Theme.SurfaceHover : Theme.Surface;
            appearance.ForeColor = Theme.Text;
            appearance.Options.UseBackColor = appearance.Options.UseForeColor = true;
        }
        view.Appearance.Row.Font = Theme.Font(10); view.Appearance.Row.Options.UseFont = true;
        view.Appearance.HeaderPanel.ForeColor = Theme.Muted;
        view.Appearance.HeaderPanel.BorderColor = Theme.Divider; view.Appearance.HeaderPanel.Options.UseBorderColor = true;
        view.Appearance.HorzLine.BackColor = view.Appearance.VertLine.BackColor = Theme.Divider;
        view.Appearance.HorzLine.Options.UseBackColor = view.Appearance.VertLine.Options.UseBackColor = true;
        view.Columns.AddVisible(nameof(Member.Name), "Kullanıcı");
        view.Columns.AddVisible(nameof(Member.Role), "Rol");
        view.Columns.AddVisible(nameof(Member.Status), "Durum");
        view.RowCellStyle += (_, e) =>
        {
            if (view.GetRow(e.RowHandle) is Member member && e.Column.FieldName == nameof(Member.Role))
                e.Appearance.ForeColor = UserPresentation.RoleColor(member.Person.User, member.Person.GroupRole);
        };
        layout.Controls.Add(_grid, 0, 1);
        _status.Dock = DockStyle.Fill; _status.TextAlign = ContentAlignment.MiddleLeft;
        _status.ForeColor = Theme.Muted; _status.AutoEllipsis = true;
        layout.Controls.Add(_status, 0, 2); Controls.Add(layout);
        if (snapshot) Render(conversation.Participants.Select(u => new PresenceView(u, !u.IsAgent, true, !u.IsAgent,
            null, conversation.GroupRoles?.GetValueOrDefault(u.Id) ?? "user")).ToArray());
        else Shown += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        _refresh.Enabled = false;
        try { Render((await _api.GetPresenceStatusAsync(_conversation.Id)).Where(p => p.IsInConversation).ToArray()); }
        catch (Exception ex) { if (!IsDisposed) _status.Text = ex.Message; }
        finally { if (!IsDisposed) _refresh.Enabled = true; }
    }

    private void Render(IReadOnlyList<PresenceView> members)
    {
        if (IsDisposed) return;
        _grid.DataSource = members.OrderBy(p => p.User.IsAgent).ThenBy(p => p.User.DisplayName).Select(p => new Member(p)).ToArray();
        _status.Text = $"{members.Count(p => !p.User.IsAgent)} üye · {members.Count(p => p.User.IsAgent)} bot";
    }
}
