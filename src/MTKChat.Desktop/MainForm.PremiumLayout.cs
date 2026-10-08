using DevExpress.XtraEditors;
using MTKChat.Contracts;

namespace MTKChat.Desktop;

internal sealed partial class MainForm
{
    private readonly TextEdit _premiumSearch = new();
    private readonly MemoEdit _premiumComposer = new();
    private RowStyle? _composerHeightStyle;
    private RowStyle? _imageDraftRowStyle;
    private readonly ModernButton _premiumAdminButton = Theme.Button("", ButtonKind.Ghost);
    private readonly ModernButton _premiumExpiryButton = Theme.Button("Kalıcı  ▾", ButtonKind.Ghost);
    private readonly TableLayoutPanel _premiumRoot = new();
    private readonly FlowLayoutPanel _presenceList = new();
    private readonly Label _presenceCount = new();
    private bool _presenceRefreshing;
    private string? _presenceFingerprint;
    private int _presenceTicks;
    private ModernMessageViewport? _messageViewport;
    private Control? _composerBar;
    private string _conversationFilter = "all";
    private readonly Dictionary<string, ModernButton> _filterButtons = new();

    private void BuildPremiumLayout()
    {
        _premiumRoot.Dock = DockStyle.Fill;
        _premiumRoot.ColumnCount = 3;
        _premiumRoot.RowCount = 1;
        _premiumRoot.Margin = Padding.Empty;
        _premiumRoot.Padding = Padding.Empty;
        _premiumRoot.BackColor = Color.Transparent;
        _premiumRoot.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 400));
        _premiumRoot.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _premiumRoot.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 0));
        _premiumRoot.Controls.Add(BuildPremiumSidebar(), 0, 0);
        _premiumRoot.Controls.Add(BuildPremiumChat(), 1, 0);
        _premiumRoot.Controls.Add(BuildDetailsHost(), 2, 0);
        Controls.Add(_premiumRoot);
        Resize += (_, _) => UpdatePresenceVisibility();
        DpiChanged += (_, _) => UpdatePresenceVisibility();
        UpdatePresenceVisibility();
    }

    private Control BuildPremiumSidebar() => BuildReferenceSidebar();

    private Control BuildPremiumChat() => BuildReferenceChat();

    private Control BuildPremiumMessages()
    {
        _messageList.FlowDirection = FlowDirection.TopDown;
        _messageList.WrapContents = false;
        _messageList.AutoScroll = true;
        _messageList.BackColor = Color.Transparent;
        _messageList.Padding = new Padding(26, 24, 20, 16);
        _messageList.Margin = Padding.Empty;
        _messageList.ControlAdded += (_, _) => _messageLayoutRevision++;
        _messageList.ControlRemoved += (_, _) => _messageLayoutRevision++;
        _messageList.SizeChanged += (_, _) => ResizeBubbles();
        _messageViewport = new ModernMessageViewport(_messageList) { Dock = DockStyle.Fill, Margin = Padding.Empty };
        _messageViewport.ContentWidthChanged += (_, _) => ResizeBubbles();
        return _messageViewport;
    }

    private void SetConversationControlsEnabled(bool enabled)
    {
        // The entire bar is gated, not just text entry: an empty sidebar has no send/attachment target.
        if (_composerBar is not null) { _composerBar.Enabled = enabled; _composerBar.Visible = enabled; }
        _callButton.Enabled = enabled;
        _callButton.Visible = enabled;
        _participantsButton.Enabled = enabled;
    }

    internal void PopulateMessageScrollSnapshot()
    {
        PopulateSnapshot();
        var conversation = _selectedConversation!;
        var sender = conversation.Participants.First(user => user.IsAgent);
        for (var i = 0; i < 24; i++)
        {
            var message = new StoredMessage(Guid.NewGuid(), Guid.NewGuid(), conversation.Id,
                i % 3 == 0 ? _session!.User.Id : sender.Id, "text", DateTimeOffset.Now,
                null, false, [], null);
            _messageList.Controls.Add(BuildMessageBubble(message,
                $"Mesaj {i + 1:00} · Kaydırma görünümü\nTekerlek, sayfa tuşları ve kaydırma çubuğu aynı mesaj konumunu korur.", null));
        }
        ResizeBubbles();
        _messageList.PerformLayout();
    }

    internal IReadOnlyList<string> VerifyMessageScrolling() =>
        _messageViewport?.VerifyScrolling() ?? throw new InvalidOperationException("Message viewport was not built.");

    private Control BuildPremiumPresence()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            BackColor = Theme.Sidebar,
            Padding = new Padding(18, 24, 18, 18),
            Margin = Padding.Empty
        };
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 76));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        var header = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Sidebar };
        header.Controls.Add(new Label
        {
            Text = "Katılımcılar",
            Bounds = new Rectangle(0, 1, 224, 30),
            ForeColor = Theme.Text,
            Font = Theme.Font(12f, FontStyle.Bold)
        });
        _presenceCount.Bounds = new Rectangle(1, 34, 224, 22);
        _presenceCount.ForeColor = Theme.Muted;
        _presenceCount.Font = Theme.Font(8.5f);
        _presenceCount.Text = "Durum yükleniyor";
        header.Controls.Add(_presenceCount);
        header.Controls.Add(new Panel { Dock = DockStyle.Bottom, Height = 1, BackColor = Theme.Divider });

        _presenceList.Dock = DockStyle.Fill;
        _presenceList.FlowDirection = FlowDirection.TopDown;
        _presenceList.WrapContents = false;
        _presenceList.AutoScroll = true;
        _presenceList.BackColor = Theme.Sidebar;
        _presenceList.Padding = new Padding(0, 13, 0, 0);
        _presenceList.Resize += (_, _) => ResizePresenceItems();

        var explanation = new Label
        {
            Text = "",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Theme.Muted,
            Font = Theme.Font(7.8f)
        };
        panel.Controls.Add(header, 0, 0);
        panel.Controls.Add(_presenceList, 0, 1);
        panel.Controls.Add(explanation, 0, 2);
        return panel;
    }

    private void UpdatePresenceVisibility()
    {
        if (_premiumRoot.ColumnStyles.Count < 3) return;
        // Reserve a real icon rail without squeezing the measured filters or
        // borrowing any width from the message composer's action slots.
        var scale = DeviceDpi / 96f;
        var logicalWidth = ClientSize.Width / scale;
        var sidebarWidth = (logicalWidth >= 1450 ? 440 : logicalWidth >= 1280 ? 390 : 340) * scale;
        if (Math.Abs(_premiumRoot.ColumnStyles[0].Width - sidebarWidth) > .1f)
        { _premiumRoot.ColumnStyles[0].Width = sidebarWidth; QueueDetailsLayout(); }
        ArrangeDetails();
    }

    private async Task RefreshPresenceAsync()
    {
        if (_presenceRefreshing || _selectedConversation is null || _session is null) return;
        _presenceRefreshing = true;
        var conversationId = _selectedConversation.Id;
        var version = _conversationVersion;
        try
        {
            var people = await _api.GetPresenceAsync(conversationId);
            if (_selectedConversation?.Id != conversationId || version != _conversationVersion || IsDisposed) return;
            var fingerprint = string.Join('|', people.Select(item =>
                $"{item.User.Id:N}:{item.User.DisplayName}:{item.User.Role}:{item.User.PhotoVersion}:{item.GroupRole}:{item.LastSeenAt?.ToUnixTimeSeconds() / 60}:{item.IsOnline}:{item.IsInConversation}:{item.IsViewingConversation}"));
            if (fingerprint == _presenceFingerprint) return;
            _presenceFingerprint = fingerprint;
            var members = people.Where(p => p.IsInConversation).Select(p => p.User).ToArray();
            var roles = people.Where(p => p.IsInConversation).ToDictionary(p => p.User.Id, p => p.GroupRole);
            var rolesChanged = roles.Any(r => _selectedConversation.GroupRoles?.GetValueOrDefault(r.Key) != r.Value);
            if (!_selectedConversation.Participants.OrderBy(u => u.Id).SequenceEqual(members.OrderBy(u => u.Id)) || rolesChanged)
            {
                _selectedConversation = _selectedConversation with { Participants = members, GroupRoles = roles };
                RefreshEncryptionRecipientStatus();
                _renderFingerprint = null;
            }
            if (_selectedConversation.Kind == "direct" && people.FirstOrDefault(p => p.IsInConversation && p.User.Id != _session.User.Id) is { } peer)
            {
                _conversationMeta.Text = PresenceText(peer);
                _conversationMeta.ForeColor = peer.IsOnline ? Theme.Success : Theme.Muted;
            }
            var self = people.FirstOrDefault(p => p.User.Id == _session.User.Id)?.User;
            if (self is not null)
            {
                _session = _session with { User = self };
                RefreshRailAccount(self);
            }
            RenderPresence(people);
        }
        catch (Exception exception)
        {
            if (_selectedConversation?.Id != conversationId || version != _conversationVersion || IsDisposed) return;
            RecordNetworkFailure(exception);
            _presenceCount.Text = "Durum alınamadı";
        }
        finally
        {
            _presenceRefreshing = false;
        }
    }

    private void RenderPresence(IReadOnlyList<PresenceView> people)
    {
        if (DeferPresenceRender(people)) return;
        var previousOffset = Math.Max(0, -_presenceList.AutoScrollPosition.Y);
        var previous = _presenceList.Controls.Cast<Control>().ToArray();
        _presenceList.SuspendLayout();
        try
        {
            _presenceList.Controls.Clear();
            foreach (var control in previous) control.Dispose();
            var humans = people.Count(item => !item.User.IsAgent);
            var online = people.Count(item => !item.User.IsAgent && item.IsOnline);
            _presenceCount.Text = $"{online} çevrimiçi · {humans} kullanıcı";
            AddPresenceGroup("ŞU AN BU SOHBETTE", people.Where(item => item.IsViewingConversation));
            AddPresenceGroup("SOHBET ÜYELERİ", people.Where(item => item.IsInConversation && !item.IsViewingConversation));
            AddPresenceGroup("DİĞER KULLANICILAR", people.Where(item => !item.IsInConversation));
        }
        finally
        {
            _presenceList.ResumeLayout();
        }
        ResizePresenceItems();
        _presenceList.PerformLayout();
        var maximumOffset = _presenceList.VerticalScroll.Visible
            ? Math.Max(0, _presenceList.VerticalScroll.Maximum - _presenceList.VerticalScroll.LargeChange + 1) : 0;
        _presenceList.AutoScrollPosition = new Point(0, Math.Clamp(previousOffset, 0, maximumOffset));
        RememberRenderedPresence(people);
    }

    private void AddPresenceGroup(string title, IEnumerable<PresenceView> entries)
    {
        var group = entries.ToArray();
        if (group.Length == 0) return;
        _presenceList.Controls.Add(new Label
        {
            Text = title,
            Width = Math.Max(180, _presenceList.ClientSize.Width - 8),
            Height = 37,
            Padding = new Padding(3, 10, 0, 0),
            ForeColor = Theme.Muted,
            Font = Theme.Font(8f)
        });
        foreach (var item in group)
        {
            var row = new RoundedPanel
            {
                Width = Math.Max(180, _presenceList.ClientSize.Width - 8),
                Height = 67,
                Margin = new Padding(0, 0, 0, 7),
                FillColor = Theme.Surface,
                BorderColor = Theme.Divider,
                CornerRadius = 12,
                Padding = new Padding(9)
            };
            var avatar = new AvatarView
            {
                Initials = Initials(item.User.DisplayName),
                AvatarColor = item.User.IsAgent ? Theme.Bot : item.IsOnline ? Theme.Success : Theme.SurfaceHover,
                Bounds = new Rectangle(9, 13, 38, 38)
            };
            var name = new Label
            {
                Text = UserPresentation.Heading(item.User, item.IsInConversation ? item.GroupRole : null),
                Bounds = new Rectangle(57, 11, row.Width - 68, 23),
                ForeColor = UserPresentation.RoleColor(item.User, item.IsInConversation ? item.GroupRole : null),
                Font = Theme.Font(9f, FontStyle.Bold),
                UseMnemonic = false,
                AutoEllipsis = true
            };
            var status = new Label
            {
                Text = PresenceText(item),
                Bounds = new Rectangle(57, 35, row.Width - 68, 19),
                ForeColor = item.User.IsAgent ? Theme.Violet : item.IsOnline ? Theme.Success : Theme.Muted,
                Font = Theme.Font(8f),
                AutoEllipsis = true,
                UseMnemonic = false
            };
            row.Controls.Add(status);
            row.Controls.Add(name);
            row.Controls.Add(avatar);
            if (_presenceContinuityProbe) AddPresenceProbeMenu(row);
            else AddPersonActions(row, item);
            TrackPresenceMenu(row.ContextMenuStrip);
            _ = _avatars.ApplyAsync(avatar, item.User);
            row.Tag = (name, status);
            _presenceList.Controls.Add(row);
        }
    }

    private void ResizePresenceItems()
    {
        foreach (Control control in _presenceList.Controls)
        {
            control.Width = Math.Max(180, _presenceList.ClientSize.Width - 8);
            if (control.Tag is ValueTuple<Label, Label> labels)
            {
                labels.Item1.Width = control.Width - 68;
                labels.Item2.Width = control.Width - 68;
            }
        }
    }
}
