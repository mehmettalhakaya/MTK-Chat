using DevExpress.Utils;
using DevExpress.XtraEditors;
using DevExpress.XtraEditors.Controls;

namespace MTKChat.Desktop;

internal sealed partial class MainForm
{
    private enum SidebarPage { Chats, Settings, Profile, Privacy, Blocked, Statuses }
    private SidebarPage _sidebarPage;
    private SidebarPage _sidebarReturnPage;
    private Control? _sidebarChats;
    private Panel? _sidebarPageHost;
    private TableLayoutPanel? _sidebarDrawer;
    private Panel? _sidebarDrawerBody;
    private Label? _sidebarDrawerTitle;
    private ModernButton? _sidebarBack;
    private ToolTip? _sidebarBackTip;
    private Control? _sidebarSettings;
    private ProfileEditor? _sidebarProfileEditor;
    private Guid? _sidebarEditorUser;
    private ModernButton? _railChat;
    private ModernButton? _railSettings;
    private RailProfileButton? _railProfile;
    private RowStyle? _railAdminRow;
    private bool _openingAdministration;

    private Control BuildReferenceSidebar()
    {
        var shell = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1,
            BackColor = Theme.Sidebar, Margin = Padding.Empty, Padding = Padding.Empty };
        shell.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 56));
        shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var rail = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6,
            BackColor = Theme.Rail, Margin = Padding.Empty, Padding = new Padding(6, 12, 6, 12) };
        // An implicit AutoSize column follows Button's preferred 75px width,
        // even inside a 56px rail. Constrain the real slot, not only the icon.
        rail.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        rail.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        rail.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        rail.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _railAdminRow = new RowStyle(SizeType.Absolute, 0);
        rail.RowStyles.Add(_railAdminRow);
        rail.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        rail.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        _railChat = NavigationButton(ModernButtonIcon.Chat, "Sohbetler");
        _railStatus = NavigationButton(ModernButtonIcon.Status, "Durumlar");
        _railSettings = NavigationButton(ModernButtonIcon.Settings, "Ayarlar");
        _railProfile = new RailProfileButton(_accountAvatar)
        { Dock = DockStyle.Fill, Margin = new Padding(0, 2, 0, 2) };
        _premiumAdminButton.Text = "";
        _premiumAdminButton.VectorIcon = ModernButtonIcon.Shield;
        _premiumAdminButton.AccessibleName = "Yönetim paneli";
        _premiumAdminButton.Dock = DockStyle.Fill;
        _premiumAdminButton.Margin = new Padding(0, 2, 0, 2);
        _premiumAdminButton.CornerRadius = 12;
        _premiumAdminButton.Kind = ButtonKind.Ghost;
        _premiumAdminButton.ForeColor = Theme.Warning;
        _premiumAdminButton.Visible = false;
        var adminTip = new ToolTip { InitialDelay = 250, ReshowDelay = 100 };
        adminTip.SetToolTip(_premiumAdminButton, "Yönetim paneli");
        _premiumAdminButton.Disposed += (_, _) => adminTip.Dispose();
        _premiumAdminButton.Click += async (_, _) => await OpenAdministrationAsync();
        _railChat.Click += (_, _) => CloseSidebarDrawer();
        _railStatus.Click += async (_, _) => await RunSidebarActionAsync(() => ToggleSidebarAsync(SidebarPage.Statuses));
        _railSettings.Click += async (_, _) => await ToggleSidebarAsync(SidebarPage.Settings);
        _railProfile.Click += async (_, _) => await ToggleSidebarAsync(SidebarPage.Profile);
        rail.Controls.Add(_railChat, 0, 0); rail.Controls.Add(_railStatus, 0, 1); rail.Controls.Add(_premiumAdminButton, 0, 3);
        rail.Controls.Add(_railSettings, 0, 4); rail.Controls.Add(_railProfile, 0, 5);
        rail.DpiChangedAfterParent += (_, _) => SetRailAdminVisibility(IsSiteAdmin(_session?.User));
        _sidebarPageHost = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Sidebar, Margin = Padding.Empty };
        _sidebarChats = BuildReferenceSidebarContent();
        _sidebarPageHost.Controls.Add(_sidebarChats);
        _sidebarDrawer = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1,
            BackColor = Theme.Sidebar, Margin = Padding.Empty, Visible = false };
        _sidebarDrawer.RowStyles.Add(new RowStyle(SizeType.Absolute, 76));
        _sidebarDrawer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _sidebarDrawer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var heading = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1,
            Margin = Padding.Empty, Padding = new Padding(14, 16, 12, 12), BackColor = Theme.Sidebar };
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 42));
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _sidebarBack = NavigationButton(ModernButtonIcon.Back, "Geri", tip => _sidebarBackTip = tip);
        _sidebarBack.Click += async (_, _) => await RunSidebarActionAsync(BackSidebarAsync);
        _sidebarDrawerTitle = new Label { Dock = DockStyle.Fill, ForeColor = Theme.Text,
            Font = Theme.Font(17, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(10, 0, 0, 0), AutoEllipsis = true, Margin = Padding.Empty };
        heading.Controls.Add(_sidebarBack, 0, 0); heading.Controls.Add(_sidebarDrawerTitle, 1, 0);
        _sidebarDrawerBody = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Sidebar, Margin = Padding.Empty };
        _sidebarDrawer.Controls.Add(heading, 0, 0); _sidebarDrawer.Controls.Add(_sidebarDrawerBody, 0, 1);
        _sidebarPageHost.Controls.Add(_sidebarDrawer);
        shell.Controls.Add(rail, 0, 0); shell.Controls.Add(_sidebarPageHost, 1, 0);
        StyleSidebarNavigation();
        return shell;
    }

    private static ModernButton NavigationButton(ModernButtonIcon icon, string label, Action<ToolTip>? configureTip = null)
    {
        var button = Theme.Button("", ButtonKind.Ghost);
        button.VectorIcon = icon; button.AccessibleName = label;
        button.Dock = DockStyle.Fill; button.Margin = new Padding(0, 2, 0, 2); button.CornerRadius = 12;
        var tip = new ToolTip { InitialDelay = 250, ReshowDelay = 100 };
        configureTip?.Invoke(tip);
        tip.SetToolTip(button, label); button.Disposed += (_, _) => tip.Dispose();
        return button;
    }

    private Task ToggleSidebarAsync(SidebarPage page)
    {
        if (_sidebarPage == page) { CloseSidebarDrawer(); return Task.CompletedTask; }
        return OpenSidebarAsync(page);
    }

    private async Task OpenSidebarAsync(SidebarPage page, bool returnToSettings = false)
    {
        if (_session is null || IsDisposed || Disposing || SidebarWriteBusy) return;
        if (page == SidebarPage.Chats) { CloseSidebarDrawer(); return; }
        if (_sidebarDrawer is null || _sidebarDrawerBody is null || _sidebarChats is null) return;
        if (_sidebarEditorUser != _session.User.Id)
        {
            _sidebarProfileEditor?.Dispose(); _sidebarProfileEditor = null;
            _sidebarStatuses?.Dispose(); _sidebarStatuses = null;
            _sidebarBlocked?.Dispose(); _sidebarBlocked = null;
            _sidebarEditorUser = _session.User.Id;
        }
        _sidebarSettings ??= BuildSidebarSettings();
        _sidebarProfileEditor ??= new ProfileEditor(_api, _session.User, _avatars, _snapshotMode) { Dock = DockStyle.Fill };
        _sidebarProfileEditor.RefreshUser(_session.User);
        if (_sidebarProfileEditor.Parent is null)
        {
            _sidebarProfileEditor.PhotoSaved += ApplySidebarProfileUpdate;
            var editor = _sidebarProfileEditor;
            editor.BusyChanged += _ =>
            {
                if (ReferenceEquals(_sidebarProfileEditor, editor) && !IsDisposed && !Disposing)
                    StyleSidebarNavigation();
            };
            _sidebarDrawerBody.Controls.Add(_sidebarProfileEditor);
        }
        if (_sidebarSettings.Parent is null) _sidebarDrawerBody.Controls.Add(_sidebarSettings);
        if (page is SidebarPage.Statuses or SidebarPage.Blocked) EnsureSocialPanels();
        _sidebarStatuses?.CancelPending(); _sidebarBlocked?.CancelPending();
        _sidebarPage = page;
        // A settings child is not the conversation list. Remember the explicit
        // entry point so Back returns one level; rail toggles still collapse.
        _sidebarReturnPage = returnToSettings && (page is SidebarPage.Profile or SidebarPage.Privacy or SidebarPage.Blocked)
            ? SidebarPage.Settings : SidebarPage.Chats;
        _sidebarBack!.AccessibleName = _sidebarReturnPage == SidebarPage.Settings ? "Ayarlara dön" : "Sohbetlere dön";
        _sidebarBackTip?.SetToolTip(_sidebarBack, _sidebarBack.AccessibleName);
        var privacy = page == SidebarPage.Privacy;
        _sidebarProfileEditor.SetMode(privacy);
        _sidebarSettings.Visible = page == SidebarPage.Settings;
        _sidebarProfileEditor.Visible = page is SidebarPage.Profile or SidebarPage.Privacy;
        if (_sidebarStatuses is not null) _sidebarStatuses.Visible = page == SidebarPage.Statuses;
        if (_sidebarBlocked is not null) _sidebarBlocked.Visible = page == SidebarPage.Blocked;
        _sidebarDrawerTitle!.Text = page switch { SidebarPage.Settings => "Ayarlar", SidebarPage.Privacy => "Gizlilik",
            SidebarPage.Blocked => "Engellenenler", SidebarPage.Statuses => "Durumlar", _ => "Profilim" };
        _sidebarChats.Visible = false; _sidebarDrawer.Visible = true; _sidebarDrawer.BringToFront();
        StyleSidebarNavigation();
        // Showing a profile never depends on an unrelated privacy GET. Only its
        // own settings panel loads privacy, with cancellation when disposed.
        if (privacy && !_snapshotMode) await _sidebarProfileEditor.LoadPrivacyAsync(refresh: true);
        if (page == SidebarPage.Blocked && !_snapshotMode) await _sidebarBlocked!.LoadAsync();
        if (page == SidebarPage.Statuses && !_snapshotMode) await _sidebarStatuses!.LoadAsync();
    }

    private Task BackSidebarAsync()
    {
        if (SidebarWriteBusy) return Task.CompletedTask;
        if (_sidebarReturnPage == SidebarPage.Settings) return OpenSidebarAsync(SidebarPage.Settings);
        CloseSidebarDrawer();
        return Task.CompletedTask;
    }

    private void CloseSidebarDrawer()
    {
        if (SidebarWriteBusy || _sidebarDrawer is null || _sidebarChats is null) return;
        _sidebarStatuses?.CancelPending(); _sidebarBlocked?.CancelPending();
        _sidebarPage = _sidebarReturnPage = SidebarPage.Chats;
        _sidebarDrawer.Visible = false; _sidebarChats.Visible = true;
        _sidebarChats.BringToFront(); StyleSidebarNavigation();
    }

    private void StyleSidebarNavigation()
    {
        if (_railChat is not null) _railChat.Kind = _sidebarPage == SidebarPage.Chats ? ButtonKind.Secondary : ButtonKind.Ghost;
        if (_railSettings is not null) _railSettings.Kind = _sidebarPage is SidebarPage.Settings or SidebarPage.Privacy or SidebarPage.Blocked ? ButtonKind.Secondary : ButtonKind.Ghost;
        if (_railStatus is not null) _railStatus.Kind = _sidebarPage == SidebarPage.Statuses ? ButtonKind.Secondary : ButtonKind.Ghost;
        if (_railProfile is not null) _railProfile.Active = _sidebarPage == SidebarPage.Profile;
        // Save is a single authenticated write. Its navigation guard must also
        // be visible, rather than leaving apparently clickable inactive actions.
        var available = !SidebarWriteBusy;
        foreach (var button in new Control?[] { _railChat, _railStatus, _railSettings, _railProfile, _sidebarBack })
            if (button is not null) button.Enabled = available;
        if (_sidebarSettings is not null) _sidebarSettings.Enabled = available;
    }

    private static bool IsSiteAdmin(MTKChat.Contracts.ChatUser? user) => user is { IsAgent: false, Role: "admin" };

    private void SetRailAdminVisibility(bool visible)
    {
        _premiumAdminButton.Visible = visible;
        if (_railAdminRow is null) return;
        // A hidden management action must not leave a dead slot above Settings.
        var height = visible ? 48 * (_premiumAdminButton.Parent?.DeviceDpi ?? DeviceDpi) / 96f : 0;
        if (Math.Abs(_railAdminRow.Height - height) > .1f) _railAdminRow.Height = height;
    }

    private void RefreshRailAccount(MTKChat.Contracts.ChatUser user)
    {
        if (IsDisposed || Disposing || _session?.User.Id != user.Id) return;
        SetRailAdminVisibility(IsSiteAdmin(user));
        _accountAvatar.AccessibleDescription = user.DisplayName + " · " + UserPresentation.Role(user);
        // Login, presence updates and photo save/removal share the same real
        // avatar. Its version guard avoids downloads and flashing on each poll.
        _ = _avatars.ApplyAsync(_accountAvatar, user);
    }

    private async Task<MTKChat.Contracts.ChatUser?> ValidateRailAdminAsync()
    {
        if (!IsSiteAdmin(_session?.User)) { SetRailAdminVisibility(false); return null; }
        var owner = _session!.User.Id;
        await _api.CheckAdminAccessAsync();
        var current = await _api.GetProfileAsync();
        // Group roles do not grant site administration; recheck server role and
        // account ownership after the awaits, before showing any management UI.
        if (IsDisposed || Disposing || _session?.User.Id != owner) return null;
        if (!IsSiteAdmin(_session.User)) { SetRailAdminVisibility(false); return null; }
        if (current.Id != owner) { SetRailAdminVisibility(false); return null; }
        ApplySidebarProfileUpdate(current);
        return IsSiteAdmin(current) ? current : null;
    }

    private async Task OpenAdministrationAsync()
    {
        if (_openingAdministration || IsDisposed || Disposing || !IsSiteAdmin(_session?.User)) return;
        var owner = _session!.User.Id;
        _openingAdministration = true;
        _premiumAdminButton.Enabled = false;
        try
        {
            if (await ValidateRailAdminAsync() is not { } current) return;
            using var management = new UserManagementForm(_api, current, _selectedConversation);
            management.ShowDialog(this);
        }
        catch (Exception ex)
        {
            if (!IsDisposed && !Disposing && _session?.User.Id == owner)
            { SetRailAdminVisibility(false); ShowError(ex.Message); }
        }
        finally
        {
            _openingAdministration = false;
            if (!_premiumAdminButton.IsDisposed) _premiumAdminButton.Enabled = true;
        }
    }

    private Control BuildSidebarSettings()
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1,
            Padding = new Padding(18, 12, 18, 18), Margin = Padding.Empty, BackColor = Theme.Sidebar };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var (title, detail, action) in new (string, string, Func<Task>)[]
        {
            ("Profilim", "Fotoğraf ve hesap bilgileri", () => OpenSidebarAsync(SidebarPage.Profile, returnToSettings: true)),
            ("Gizlilik", "Son görülme ve okundu bilgisi", () => OpenSidebarAsync(SidebarPage.Privacy, returnToSettings: true)),
            ("Engellenenler", "Engellediğin kişileri gör ve engeli kaldır", () => OpenSidebarAsync(SidebarPage.Blocked, returnToSettings: true)),
            ("Yıldızlı mesajlar", "Açık sohbetin kaydedilen mesajları", () => _selectedConversation is { } room ? OpenStarredMessagesAsync(room) : Task.CompletedTask)
        })
        {
            var card = new RoundedPanel { Dock = DockStyle.Top, Height = 86, FillColor = Theme.Surface,
                BorderColor = Theme.Divider, CornerRadius = 14, Margin = new Padding(0, 0, 0, 10),
                Padding = new Padding(8), Cursor = Cursors.Hand, AccessibleName = title };
            // A real DevExpress button gives each row one Click/keyboard target.
            // Recursive Panel/Label handlers had no tab-stop, focus state or
            // contained exception boundary and could not express nested Back.
            var button = new SimpleButton { Dock = DockStyle.Fill, Margin = Padding.Empty,
                ButtonStyle = BorderStyles.NoBorder, AllowHtmlDraw = DefaultBoolean.True,
                AllowFocus = true, Cursor = Cursors.Hand, AccessibleName = title,
                AccessibleDescription = detail, Padding = new Padding(6, 2, 6, 2),
                Text = $"<b>{title}</b><br><color=#{Theme.Muted.R:X2}{Theme.Muted.G:X2}{Theme.Muted.B:X2}>{detail}</color>" };
            foreach (var appearance in new[] { button.Appearance, button.AppearanceHovered, button.AppearancePressed })
            {
                appearance.Font = Theme.Font(10.25f);
                appearance.ForeColor = Theme.Text;
                appearance.Options.UseFont = appearance.Options.UseForeColor = appearance.Options.UseBackColor = true;
                appearance.TextOptions.HAlignment = HorzAlignment.Near;
                appearance.TextOptions.VAlignment = VertAlignment.Center;
                appearance.TextOptions.WordWrap = WordWrap.Wrap;
            }
            button.Appearance.BackColor = Theme.Surface;
            button.AppearanceHovered.BackColor = Theme.SurfaceHover;
            button.AppearancePressed.BackColor = Theme.SurfaceRaised;
            button.Click += async (_, _) => await RunSidebarActionAsync(action);
            card.Controls.Add(button);
            panel.Controls.Add(card);
        }
        return panel;
    }

    private async Task RunSidebarActionAsync(Func<Task> action)
    {
        if (IsDisposed || Disposing || _session is null || SidebarWriteBusy) return;
        try { await action(); }
        catch (Exception ex) { if (!IsDisposed && !Disposing) ShowError(ex.Message); }
    }

    private void ApplySidebarProfileUpdate(MTKChat.Contracts.ChatUser user)
    {
        if (IsDisposed || Disposing || _session?.User.Id != user.Id) return;
        _session = _session with { User = user };
        RefreshRailAccount(user);
        _renderFingerprint = _presenceFingerprint = null;
    }

    internal void PopulateSidebarSnapshot(bool privacy = false, bool settings = false)
    {
        PopulateReferenceSnapshot();
        OpenSidebarAsync(settings ? SidebarPage.Settings : privacy ? SidebarPage.Privacy : SidebarPage.Profile).GetAwaiter().GetResult();
    }

    internal void PopulateRailPhotoSnapshot(bool admin = true)
    {
        PopulateSnapshot(admin);
        // Local illustrative avatar only, not a real user's photo or remote GET.
        using var sample = new Bitmap(80, 80);
        using (var graphics = Graphics.FromImage(sample))
        {
            graphics.Clear(Color.FromArgb(105, 80, 200));
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var ink = new SolidBrush(Color.FromArgb(234, 228, 255));
            graphics.FillEllipse(ink, 27, 14, 26, 26);
            graphics.FillEllipse(ink, 16, 45, 48, 42);
        }
        _accountAvatar.SetPhoto(sample);
    }
}
