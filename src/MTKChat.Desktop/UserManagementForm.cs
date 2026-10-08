using DevExpress.XtraEditors;
using DevExpress.XtraEditors.Controls;
using DevExpress.XtraGrid;
using DevExpress.XtraGrid.Views.Grid;
using MTKChat.Contracts;

namespace MTKChat.Desktop;

internal sealed class UserManagementForm : ModernForm
{
    private readonly ChatApiClient _api;
    private readonly ChatUser _currentUser;
    private readonly ConversationSummary? _conversation;
    private readonly GridControl _grid = new();
    private readonly GridView _view = new();
    private readonly TextEdit _search = new();
    private readonly Label _count = new();
    private HashSet<Guid> _blocked = new();
    private IReadOnlyList<AdminUserView> _rows = Array.Empty<AdminUserView>();
    private IReadOnlyDictionary<Guid, ChatModerationView> _chatStatuses = new Dictionary<Guid, ChatModerationView>();

    public UserManagementForm(ChatApiClient api, ChatUser currentUser, ConversationSummary? conversation = null, bool snapshotMode = false)
    {
        if (currentUser.Role != "admin" || currentUser.IsAgent) throw new UnauthorizedAccessException("Site yöneticisi yetkisi gerekli.");
        SuspendLayout();
        _api = api;
        _currentUser = currentUser;
        _conversation = conversation;
        Text = currentUser.Role == "admin" ? "MTK Chat · Yönetim" : "MTK Chat · Kişiler";
        Icon = Theme.AppIcon();
        // Fixed design dimensions share the same 96DPI basis as invite/group
        // dialogs; the font alone must not scale while action slots remain tiny.
        AutoScaleMode = AutoScaleMode.Dpi; AutoScaleDimensions = new SizeF(96, 96);
        BackColor = Theme.Canvas;
        ForeColor = Theme.Text;
        Size = new Size(1080, 690);
        MinimumSize = new Size(900, 560);
        StartPosition = FormStartPosition.CenterParent;
        Font = Theme.Font(10f);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            BackColor = Theme.Canvas,
            Padding = new Padding(28, 24, 28, 24)
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 108));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, currentUser.Role == "admin" ? conversation is null ? 124 : 228 : 101));
        layout.Controls.Add(BuildHeader(), 0, 0);
        layout.Controls.Add(BuildGrid(), 0, 1);
        layout.Controls.Add(BuildActions(), 0, 2);
        Controls.Add(layout);
        if (!snapshotMode)
        {
            var accessTimer = new System.Windows.Forms.Timer { Interval = 3000 };
            var checking = false;
            accessTimer.Tick += async (_, _) =>
            {
                if (checking) return;
                checking = true;
                try { await _api.CheckAdminAccessAsync(); }
                catch { if (!IsDisposed) Close(); }
                finally { checking = false; }
            };
            Shown += async (_, _) => { accessTimer.Start(); await ReloadAsync(); };
            FormClosed += (_, _) => accessTimer.Dispose();
        }
        ResumeLayout(performLayout: true);
    }

    private Control BuildHeader()
    {
        var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = Theme.Canvas };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 300));
        header.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var heading = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Canvas };
        heading.Controls.Add(new Label
        {
            Text = _currentUser.Role == "admin" ? "Yönetim paneli" : "Kişiler ve engeller",
            Bounds = new Rectangle(0, 12, 500, 44),
            ForeColor = Theme.Text,
            Font = Theme.Font(22f, FontStyle.Bold)
        });
        _count.Bounds = new Rectangle(2, 61, 500, 24);
        _count.ForeColor = Theme.Muted;
        _count.Font = Theme.Font(9f);
        heading.Controls.Add(_count);

        _search.Dock = DockStyle.Fill;
        _search.Margin = Padding.Empty;
        _search.Properties.AutoHeight = false;
        _search.Properties.BorderStyle = BorderStyles.NoBorder;
        _search.Properties.NullValuePrompt = "İsim veya e-posta ara";
        _search.Properties.Appearance.BackColor = Theme.SurfaceRaised;
        _search.Properties.Appearance.ForeColor = Theme.Text;
        _search.Properties.Appearance.Font = Theme.Font(10f);
        _search.Properties.Appearance.Options.UseBackColor = true;
        _search.Properties.Appearance.Options.UseForeColor = true;
        _search.Properties.Appearance.Options.UseFont = true;
        _search.Properties.AppearanceFocused.BackColor = Theme.SurfaceRaised;
        _search.Properties.AppearanceFocused.ForeColor = Theme.Text;
        _search.Properties.AppearanceFocused.Options.UseBackColor = true;
        _search.Properties.AppearanceFocused.Options.UseForeColor = true;
        _search.TextChanged += (_, _) => RenderRows();
        var searchHost = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Canvas, Padding = new Padding(0, 19, 0, 0) };
        var searchFrame = new RoundedPanel
        {
            Dock = DockStyle.Top,
            Height = 44,
            FillColor = Theme.SurfaceRaised,
            BorderColor = Theme.Divider,
            CornerRadius = 12,
            Padding = new Padding(14, 5, 14, 5)
        };
        searchFrame.Controls.Add(_search);
        _search.Enter += (_, _) => searchFrame.BorderColor = Theme.Accent;
        _search.Leave += (_, _) => searchFrame.BorderColor = Theme.Divider;
        searchHost.Controls.Add(searchFrame);

        header.Controls.Add(heading, 0, 0);
        header.Controls.Add(searchHost, 1, 0);
        return header;
    }

    private Control BuildGrid()
    {
        _grid.Dock = DockStyle.Fill;
        _grid.MainView = _view;
        _grid.ViewCollection.Add(_view);
        _grid.BackColor = Theme.Surface;
        _view.BorderStyle = BorderStyles.NoBorder;
        _grid.LookAndFeel.UseDefaultLookAndFeel = false;
        // Flat look-and-feel introduced light frame/header edges and the inactive focused
        // row fell back to Windows gray. Keep DevExpress chrome dark and explicitly theme
        // every selection state, without changing which user the moderation actions select.
        _grid.LookAndFeel.SetSkinStyle("Office 2019 Black");
        _view.OptionsBehavior.Editable = false;
        _view.OptionsSelection.EnableAppearanceFocusedCell = false;
        _view.OptionsView.ShowGroupPanel = false;
        _view.OptionsView.ShowIndicator = false;
        _view.OptionsView.ColumnAutoWidth = true;
        _view.OptionsView.ShowHorizontalLines = DevExpress.Utils.DefaultBoolean.False;
        _view.OptionsView.ShowVerticalLines = DevExpress.Utils.DefaultBoolean.False;
        _view.FocusRectStyle = DrawFocusRectStyle.None;
        _view.RowHeight = 46;
        _view.ColumnPanelRowHeight = 42;
        _view.Appearance.Empty.BackColor = Theme.Surface;
        _view.Appearance.Empty.Options.UseBackColor = true;
        _view.Appearance.Row.BackColor = Theme.Surface;
        _view.Appearance.Row.ForeColor = Theme.Text;
        _view.Appearance.Row.Font = Theme.Font(10f);
        _view.Appearance.Row.Options.UseBackColor = true;
        _view.Appearance.Row.Options.UseForeColor = true;
        _view.Appearance.Row.Options.UseFont = true;
        var selection = Color.FromArgb(43, 34, 70);
        _view.Appearance.FocusedRow.BackColor = selection;
        _view.Appearance.FocusedRow.BackColor2 = selection;
        _view.Appearance.FocusedRow.ForeColor = Theme.Text;
        _view.Appearance.FocusedRow.Options.UseBackColor = true;
        _view.Appearance.FocusedRow.Options.UseForeColor = true;
        _view.Appearance.HideSelectionRow.BackColor = selection;
        _view.Appearance.HideSelectionRow.BackColor2 = selection;
        _view.Appearance.HideSelectionRow.ForeColor = Theme.Text;
        _view.Appearance.HideSelectionRow.Options.UseBackColor = true;
        _view.Appearance.HideSelectionRow.Options.UseForeColor = true;
        _view.Appearance.SelectedRow.BackColor = selection;
        _view.Appearance.SelectedRow.BackColor2 = selection;
        _view.Appearance.SelectedRow.ForeColor = Theme.Text;
        _view.Appearance.SelectedRow.Options.UseBackColor = true;
        _view.Appearance.SelectedRow.Options.UseForeColor = true;
        _view.Appearance.HeaderPanel.BackColor = Theme.SurfaceRaised;
        _view.Appearance.HeaderPanel.BorderColor = Theme.Divider;
        _view.Appearance.HeaderPanel.ForeColor = Theme.Muted;
        _view.Appearance.HeaderPanel.Font = Theme.Font(9f);
        _view.Appearance.HeaderPanel.Options.UseBackColor = true;
        _view.Appearance.HeaderPanel.Options.UseForeColor = true;
        _view.Appearance.HeaderPanel.Options.UseFont = true;
        _view.Appearance.HeaderPanel.Options.UseBorderColor = true;
        _view.RowCellStyle += (_, e) =>
        {
            if (_view.GetRow(e.RowHandle) is not AdminRow row) return;
            if (e.Column.FieldName == nameof(AdminRow.Role))
            {
                e.Appearance.ForeColor = row.Role == "Admin" ? Theme.Warning : Theme.Text;
                e.Appearance.Options.UseForeColor = true;
            }
            else if (e.Column.FieldName == nameof(AdminRow.Status))
            {
                e.Appearance.ForeColor = row.Status switch
                {
                    "Banlı" => Theme.Danger,
                    "Susturuldu" => Theme.Warning,
                    _ => Theme.Success
                };
                e.Appearance.Options.UseForeColor = true;
            }
        };
        var panel = new RoundedPanel
        {
            Dock = DockStyle.Fill,
            FillColor = Theme.Surface,
            BorderColor = Theme.Divider,
            CornerRadius = 18,
            Padding = new Padding(1)
        };
        panel.Controls.Add(_grid);
        return panel;
    }

    private Control BuildActions()
    {
        var host = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = _conversation is not null && _currentUser.Role == "admin" ? 2 : 1,
            BackColor = Theme.Canvas,
            Padding = new Padding(0, 12, 0, 0)
        };
        host.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        host.RowStyles.Add(new RowStyle(SizeType.Absolute, 91));
        var global = CreateActionGroup(_currentUser.Role == "admin" ? "GENEL YÖNETİM" : "KİŞİLER");
        AddAction(global, "Engelle / kaldır", async id =>
        {
            await _api.SetBlockedAsync(id, !_blocked.Contains(id));
            await ReloadAsync();
        });
        if (_currentUser.Role == "admin")
        {
            AddAction(global, "Admin yap", async id => { await _api.SetUserRoleAsync(id, "admin"); await ReloadAsync(); });
            AddAction(global, "Yetkiyi al", async id => { await _api.SetUserRoleAsync(id, "user"); await ReloadAsync(); });
            AddAction(global, "1 saat sustur", async id => { await _api.ModerateUserAsync(id, "mute", 60); await ReloadAsync(); });
            AddAction(global, "Susturmayı kaldır", async id => { await _api.ModerateUserAsync(id, "unmute"); await ReloadAsync(); });
            AddAction(global, "Banla", async id =>
            {
                if (MessageBox.Show(this, "Kullanıcı banlansın mı?", "Banla", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
                await _api.ModerateUserAsync(id, "ban");
                await ReloadAsync();
            }, danger: true);
            AddAction(global, "Banı kaldır", async id => { await _api.ModerateUserAsync(id, "unban"); await ReloadAsync(); });
            if (_conversation is not null)
            {
                host.RowStyles.Add(new RowStyle(SizeType.Absolute, 91));
                var chat = CreateActionGroup($"YALNIZCA {_conversation.Title.ToUpperInvariant()}");
                AddAction(chat, "Chat · 1 saat sustur", async id =>
                {
                    await _api.ModerateChatUserAsync(_conversation.Id, id, "mute", 60);
                    await ReloadAsync();
                });
                AddAction(chat, "Chat · susturmayı kaldır", async id =>
                {
                    await _api.ModerateChatUserAsync(_conversation.Id, id, "unmute");
                    await ReloadAsync();
                });
                AddAction(chat, "Chat · yasakla", async id =>
                {
                    await _api.ModerateChatUserAsync(_conversation.Id, id, "ban");
                    await ReloadAsync();
                }, danger: true);
                AddAction(chat, "Chat · yasağı kaldır", async id =>
                {
                    await _api.ModerateChatUserAsync(_conversation.Id, id, "unban");
                    await ReloadAsync();
                });
                host.Controls.Add(chat.Parent!, 0, 1);
            }
        }
        host.Controls.Add(global.Parent!, 0, 0);
        return host;
    }

    private FlowLayoutPanel CreateActionGroup(string heading)
    {
        var group = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Canvas };
        group.Controls.Add(new Label
        {
            Text = heading,
            Dock = DockStyle.Top,
            Height = 23,
            ForeColor = Theme.Muted,
            Font = Theme.Font(8.5f)
        });
        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            // At compact widths the horizontal scrollbar consumes client height.
            // Reserve its 17px logical slot as well as the whole 36px button so
            // scrolling to Banla/Chat yasakla cannot crop their lower outlines.
            Height = 62,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoScroll = true,
            BackColor = Theme.Canvas
        };
        group.Controls.Add(actions);
        return actions;
    }

    private void AddAction(FlowLayoutPanel panel, string text, Func<Guid, Task> action, bool danger = false)
    {
        // Reuse the shared rounded button instead of a rectangular DevExpress skin tile.
        // The same existing click handler and focused user ID remain the only action path.
        var button = new ModernButton
        {
            Text = text,
            Kind = ButtonKind.Secondary,
            ForeColor = danger ? Theme.Danger : Theme.Text,
            Font = Theme.Font(9f),
            Cursor = Cursors.Hand
        };
        button.Size = new Size(Math.Max(110, TextRenderer.MeasureText(text, button.Font).Width + 24), 36);
        button.CornerRadius = 9;
        button.Margin = new Padding(0, 0, 6, 0);
        button.Click += async (_, _) =>
        {
            if (SelectedUserId() is not { } userId) return;
            try { await action(userId); }
            catch (Exception exception) { MessageBox.Show(this, exception.Message, "MTK Chat", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        };
        panel.Controls.Add(button);
    }

    private Guid? SelectedUserId() => _view.GetFocusedRowCellValue(nameof(AdminRow.Id)) is Guid id ? id : null;

    private async Task ReloadAsync()
    {
        try
        {
            _blocked = (await _api.GetBlockedUsersAsync()).ToHashSet();
            _rows = _currentUser.Role == "admin"
                ? await _api.GetAdminUsersAsync()
                : (await _api.GetUsersAsync()).Where(user => !user.IsAgent)
                    .Select(user => new AdminUserView(user, false, null)).ToArray();
            _chatStatuses = _currentUser.Role == "admin" && _conversation is not null
                ? (await _api.GetChatModerationAsync(_conversation.Id)).ToDictionary(item => item.UserId)
                : new Dictionary<Guid, ChatModerationView>();
            RenderRows();
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "Kullanıcılar yüklenemedi", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void RenderRows()
    {
        var query = _search.Text.Trim();
        var visible = _rows.Where(item => query.Length == 0 ||
            item.User.DisplayName.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
            item.User.Email.Contains(query, StringComparison.CurrentCultureIgnoreCase)).ToArray();
        _grid.DataSource = visible.Select(item => new AdminRow(
            item.User.Id,
            item.User.DisplayName,
            item.User.Email,
            item.User.Role == "admin" ? "Admin" : "Üye",
            item.IsBanned ? "Banlı" : item.MutedUntil is not null && item.MutedUntil > DateTimeOffset.UtcNow
                ? "Susturuldu" : "Aktif",
            _blocked.Contains(item.User.Id) ? "Engelli" : "—",
            ChatStatusText(item.User.Id))).ToArray();
        _view.PopulateColumns();
        if (_view.Columns[nameof(AdminRow.Id)] is { } idColumn) idColumn.Visible = false;
        SetCaption(nameof(AdminRow.Name), "KULLANICI");
        SetCaption(nameof(AdminRow.Email), "E-POSTA");
        SetCaption(nameof(AdminRow.Role), "ROL");
        SetCaption(nameof(AdminRow.Status), "DURUM");
        SetCaption(nameof(AdminRow.Blocked), "ENGEL");
        SetCaption(nameof(AdminRow.ChatStatus), "BU SOHBET");
        if (_view.Columns[nameof(AdminRow.ChatStatus)] is { } chatColumn)
            chatColumn.Visible = _conversation is not null && _currentUser.Role == "admin";
        _count.Text = _conversation is null
            ? $"{visible.Length} kullanıcı · bir kullanıcı seçerek işlem yap"
            : $"{visible.Length} kullanıcı · {_conversation.Title} için moderasyon";
    }

    private string ChatStatusText(Guid userId)
    {
        if (!_chatStatuses.TryGetValue(userId, out var status)) return "—";
        if (status.IsBanned) return "Yasaklı";
        if (!status.IsMuted) return "Aktif";
        return "Susturuldu";
    }

    private void SetCaption(string field, string caption)
    {
        if (_view.Columns[field] is { } column) column.Caption = caption;
    }

    internal void PopulateSnapshot()
    {
        _rows = new[]
        {
            new AdminUserView(new ChatUser(Guid.NewGuid(), "Ayşe Demir", "ayse@mtkaya.me", false, null, "user"), false, null),
            new AdminUserView(new ChatUser(Guid.NewGuid(), "Mehmet Kaya", "mehmet@mtkaya.me", false, null, "admin"), false, null),
            new AdminUserView(new ChatUser(Guid.NewGuid(), "Deniz Arslan", "deniz@mtkaya.me", false, null, "user"), false, DateTimeOffset.UtcNow.AddHours(1))
        };
        if (_conversation is not null)
            _chatStatuses = _rows.ToDictionary(item => item.User.Id,
                item => new ChatModerationView(item.User.Id, item.MutedUntil is not null, item.MutedUntil, false));
        RenderRows();
    }

    private sealed record AdminRow(Guid Id, string Name, string Email, string Role, string Status, string Blocked, string ChatStatus);
}
