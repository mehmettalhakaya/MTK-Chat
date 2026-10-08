using System.Drawing.Drawing2D;
using MTKChat.Contracts;

namespace MTKChat.Desktop;

// The authenticated /api/blocks endpoint always resolves the current session's
// owner. This drawer never accepts another account's block-list ID or opens a DM.
internal sealed class BlockedUsersPanel : UserControl
{
    private readonly ChatApiClient _api;
    private readonly Guid _ownerId;
    private readonly AvatarCache _avatars;
    private readonly Label _summary;
    private readonly ModernButton _refresh;
    private readonly Label _feedback;
    private readonly FlowLayoutPanel _list;
    private readonly ModernConversationViewport _viewport;
    private CancellationTokenSource? _operation;
    private long _version;
    private bool _arranging;
    private bool _hasLoaded;
    private bool _busy;

    internal event Action<Guid>? UserUnblocked;
    internal event Action<bool>? BusyChanged;
    internal bool IsBusy => _busy;
    internal IReadOnlyList<Guid> BlockedIds => _list.Controls.OfType<BlockedUserCard>().Select(card => card.User.Id).ToArray();
    internal string FeedbackForQa => _feedback.Text;
    internal bool HasLoadedForQa => _hasLoaded;
    internal IReadOnlyList<ModernButton> UnblockButtonsForQa => _list.Controls.OfType<BlockedUserCard>().Select(card => card.Unblock).ToArray();

    internal BlockedUsersPanel(ChatApiClient api, Guid ownerId, AvatarCache avatars)
    {
        _api = api; _ownerId = ownerId; _avatars = avatars;
        SuspendLayout();
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96, 96);
        BackColor = Theme.Sidebar; Margin = Padding.Empty;
        Name = "BlockedUsersPanel";
        _summary = TextLabel("Engellediğiniz hesaplar", 10, Theme.Muted);
        _summary.Name = "BlockedUsersSummary";
        _refresh = Theme.Button("Yenile");
        _refresh.Name = "BlockedUsersRefresh";
        _refresh.AccessibleName = "Engellenen hesapları yenile";
        _refresh.Click += async (_, _) => await LoadAsync();
        _feedback = TextLabel("", 9, Theme.Muted);
        _feedback.Name = "BlockedUsersFeedback";
        _feedback.AutoEllipsis = false;
        _list = new FlowLayoutPanel
        {
            Name = "BlockedUsersList", FlowDirection = FlowDirection.TopDown,
            WrapContents = false, AutoScroll = true, BackColor = Theme.Sidebar,
            Padding = Padding.Empty, Margin = Padding.Empty
        };
        _viewport = new ModernConversationViewport(_list)
        {
            Name = "BlockedUsersViewport", Dock = DockStyle.None, Tint = Theme.Sidebar,
            AccessibleName = "Engellenen hesaplar listesi"
        };
        _list.Resize += (_, _) => ResizeRows();
        Controls.AddRange([_summary, _refresh, _feedback, _viewport]);
        ShowNote("Engel listesi", "Listeyi açtığınızda yalnızca sizin engellediğiniz hesaplar gösterilir.", "BlockedUsersInitial");
        ResumeLayout(true);
    }

    // Opening, explicit refresh and successful unblocking are the only data
    // boundaries. Conversation polling must not fetch/rebuild this drawer.
    internal async Task LoadAsync()
    {
        if (IsDisposed || Disposing || IsBusy) return;
        var (version, token) = BeginOperation();
        _feedback.ForeColor = Theme.Muted;
        _feedback.Text = "Engel listesi yükleniyor…";
        try
        {
            var blockedTask = _api.GetBlockedUsersAsync(token);
            var peopleTask = _api.GetUsersAsync(token);
            await Task.WhenAll(blockedTask, peopleTask);
            if (!Current(version, token)) return;
            var people = (await peopleTask).GroupBy(user => user.Id).ToDictionary(group => group.Key, group => group.First());
            // A deleted/hidden account may be absent from /api/users but its real
            // block ID must remain removable. Never invent an account or drop it.
            var users = (await blockedTask).Where(id => id != Guid.Empty && id != _ownerId).Distinct()
                .Select(id => people.GetValueOrDefault(id) ?? new ChatUser(id, "Kullanıcı", "", false, null))
                .OrderBy(user => user.DisplayName, StringComparer.CurrentCultureIgnoreCase).ThenBy(user => user.Id).ToArray();
            Render(users);
            _hasLoaded = true;
            _feedback.Text = "";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception)
        {
            if (!Current(version, token)) return;
            _feedback.ForeColor = Theme.Danger;
            _feedback.Text = "Engel listesi alınamadı. Yenile ile tekrar deneyin.";
            if (!_hasLoaded) ShowNote("Liste yüklenemedi", "Bağlantınızı kontrol edip Yenile düğmesine basın.", "BlockedUsersLoadError");
        }
        finally { EndOperation(version); }
    }

    internal async Task UnblockAsync(Guid targetId)
    {
        if (IsDisposed || Disposing || IsBusy || targetId == _ownerId || !BlockedIds.Contains(targetId)) return;
        var (version, token) = BeginOperation();
        _feedback.ForeColor = Theme.Muted;
        _feedback.Text = "Engel kaldırılıyor…";
        try
        {
            await _api.SetBlockedAsync(targetId, false, token);
            if (!Current(version, token)) return;
            var card = _list.Controls.OfType<BlockedUserCard>().SingleOrDefault(row => row.User.Id == targetId);
            if (card is not null) { _list.Controls.Remove(card); card.Dispose(); }
            UpdateSummary();
            if (BlockedIds.Count == 0) ShowEmpty();
            _feedback.ForeColor = Theme.Success;
            _feedback.Text = "Engel kaldırıldı.";
            _viewport.SynchronizeNow();
            // Publish only an acknowledged DELETE. Parent presence/menus can
            // refresh their local cache without creating or selecting a chat.
            UserUnblocked?.Invoke(targetId);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception)
        {
            if (!Current(version, token)) return;
            _feedback.ForeColor = Theme.Danger;
            _feedback.Text = "Engel kaldırılamadı. Lütfen tekrar deneyin.";
        }
        finally { EndOperation(version); }
    }

    internal void CancelPending()
    {
        _version++;
        var previous = _operation;
        _operation = null;
        previous?.Cancel(); previous?.Dispose();
        if (!IsDisposed && !Disposing) SetBusy(false);
    }

    private (long Version, CancellationToken Token) BeginOperation()
    {
        _operation?.Dispose();
        _operation = new CancellationTokenSource();
        var version = ++_version;
        SetBusy(true);
        return (version, _operation.Token);
    }
    private bool Current(long version, CancellationToken token) => !IsDisposed && !Disposing &&
        version == _version && !token.IsCancellationRequested;
    private void EndOperation(long version)
    {
        if (version != _version) return;
        _operation?.Dispose(); _operation = null;
        if (!IsDisposed && !Disposing) SetBusy(false);
    }
    private void SetBusy(bool busy)
    {
        _busy = busy;
        _refresh.Enabled = !busy;
        foreach (var button in UnblockButtonsForQa) button.Enabled = !busy;
        BusyChanged?.Invoke(busy);
    }

    private void Render(IReadOnlyList<ChatUser> users)
    {
        _list.SuspendLayout();
        try
        {
            ClearRows();
            foreach (var user in users)
            {
                var card = new BlockedUserCard(user);
                card.Unblock.Enabled = !IsBusy;
                card.Unblock.Click += async (_, _) => await UnblockAsync(user.Id);
                _list.Controls.Add(card);
                _ = _avatars.ApplyAsync(card.Avatar, user);
            }
            UpdateSummary();
            if (users.Count == 0) ShowEmpty();
            ResizeRows();
        }
        finally { _list.ResumeLayout(true); }
        _list.AutoScrollPosition = Point.Empty;
        _viewport.SynchronizeNow();
    }
    private void UpdateSummary() => _summary.Text = BlockedIds.Count == 0 ? "Engellenen hesap yok" : $"{BlockedIds.Count} engellenen hesap";
    private void ShowEmpty() => ShowNote("Engellediğiniz kişi yok", "Bir hesabı engellediğinizde burada görünür. İstediğiniz zaman engelini kaldırabilirsiniz.", "BlockedUsersEmpty");
    private void ShowNote(string title, string detail, string name)
    {
        ClearRows();
        _list.Controls.Add(new BlockedListNote(title, detail) { Name = name });
        ResizeRows();
    }
    private void ClearRows()
    {
        var rows = _list.Controls.Cast<Control>().ToArray();
        _list.Controls.Clear();
        foreach (var row in rows) row.Dispose();
    }

    protected override void OnVisibleChanged(EventArgs e)
    { base.OnVisibleChanged(e); if (!Visible && _operation is not null) CancelPending(); }
    protected override void OnLayout(LayoutEventArgs e) { base.OnLayout(e); Arrange(); }
    protected override void OnDpiChangedAfterParent(EventArgs e) { base.OnDpiChangedAfterParent(e); Arrange(); }
    private void Arrange()
    {
        if (_arranging || _viewport is null) return;
        _arranging = true;
        try
        {
            var inset = Scale(18);
            var refreshWidth = Scale(78);
            _refresh.SetBounds(Math.Max(inset, Width - inset - refreshWidth), Scale(5), refreshWidth, Scale(36));
            _summary.SetBounds(inset, Scale(5), Math.Max(0, _refresh.Left - inset - Scale(8)), Scale(36));
            _feedback.SetBounds(inset, Scale(51), Math.Max(0, Width - inset * 2), Scale(44));
            var top = Scale(101);
            _viewport.SetBounds(inset, top, Math.Max(0, Width - inset - Scale(6)), Math.Max(0, Height - top - Scale(10)));
            ResizeRows();
        }
        finally { _arranging = false; }
    }
    private void ResizeRows()
    {
        if (_list is null || _list.IsDisposed) return;
        foreach (Control row in _list.Controls)
        {
            row.Width = Math.Max(1, _list.ClientSize.Width);
            row.Margin = new Padding(0, 0, 0, Scale(10));
            row.PerformLayout();
        }
    }
    private int Scale(int value) => Math.Max(1, (int)Math.Round(value * DeviceDpi / 96d));

    internal IReadOnlyList<string> VerifyLayout()
    {
        Arrange(); _list.PerformLayout(); _viewport.SynchronizeNow();
        var checks = new List<string>();
        void Require(bool value, string name)
        { if (!value) throw new InvalidOperationException("Blocked users layout: " + name); checks.Add(name); }
        Require(_summary.Right <= _refresh.Left && ClientRectangle.Contains(_refresh.Bounds), "Count and refresh button fit without overlap");
        Require(ClientRectangle.Contains(_viewport.Bounds), "Blocked viewport is contained in the drawer");
        Require(!_list.HorizontalScroll.Visible && _list.ClientSize.Width == _viewport.ViewportWidth, "Blocked list has only its modern right scrollbar");
        Require(_list.Controls.Cast<Control>().All(row => row.Right <= _viewport.ViewportWidth), "Rows remain left of the modern scrollbar");
        foreach (var card in _list.Controls.OfType<BlockedUserCard>())
        {
            Require(card.TextFits(), "Blocked name and role do not intersect avatar or action");
            Require(card.ClientRectangle.Contains(card.Unblock.Bounds) && card.Unblock.Height >= Scale(34) && card.Unblock.Kind == ButtonKind.Secondary,
                "Unblock is a fully visible outlined button with a large click target");
        }
        return checks;
    }

    protected override void Dispose(bool disposing)
    { if (disposing) CancelPending(); base.Dispose(disposing); }

    private static Label TextLabel(string text, float size, Color color, FontStyle style = FontStyle.Regular) => new()
    {
        Text = text, Font = Theme.Font(size, style), ForeColor = color, BackColor = Color.Transparent,
        AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft, UseMnemonic = false, Margin = Padding.Empty
    };

    private sealed class BlockedUserCard : RoundedPanel
    {
        internal ChatUser User { get; }
        internal AvatarView Avatar { get; }
        internal ModernButton Unblock { get; }
        private readonly Label _name;
        private readonly Label _role;
        internal BlockedUserCard(ChatUser user)
        {
            User = user; Name = "BlockedUser_" + user.Id.ToString("N");
            FillColor = Color.FromArgb(22, 27, 52); BorderColor = Theme.Divider; CornerRadius = 14;
            AccessibleName = UserPresentation.Heading(user);
            Avatar = new AvatarView { Initials = UserPresentation.Initials(user.DisplayName), AvatarColor = Theme.SurfaceHover, BackColor = Color.Transparent };
            _name = TextLabel(user.DisplayName, 10, UserPresentation.RoleColor(user), FontStyle.Bold);
            _name.Name = "BlockedUserName";
            _role = TextLabel(UserPresentation.Role(user) + " · Engellendi", 8.5f, Theme.Muted);
            Unblock = Theme.Button("Engeli kaldır");
            Unblock.Name = "BlockedUserUnblock"; Unblock.AccessibleName = user.DisplayName + " hesabının engelini kaldır";
            Controls.AddRange([Avatar, _name, _role, Unblock]);
        }
        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (Avatar is null) return;
            int S(int value) => Math.Max(1, (int)Math.Round(value * DeviceDpi / 96d));
            Height = S(113);
            Avatar.SetBounds(S(12), S(12), S(40), S(40));
            var left = Avatar.Right + S(10);
            var width = Math.Max(0, Width - left - S(12));
            _name.SetBounds(left, S(10), width, S(25));
            _role.SetBounds(left, S(35), width, S(22));
            Unblock.SetBounds(S(12), S(66), Math.Max(1, Width - S(24)), S(36));
        }
        internal bool TextFits() => _name.Left > Avatar.Right && _name.Right <= ClientSize.Width &&
            _name.Bottom <= _role.Top && _role.Bottom < Unblock.Top;
    }

    private sealed class BlockedListNote : Control
    {
        private readonly string _title;
        private readonly string _detail;
        private readonly Font _heading = Theme.Font(11, FontStyle.Bold);
        internal BlockedListNote(string title, string detail)
        {
            _title = title; _detail = detail;
            BackColor = Theme.Sidebar; Font = Theme.Font(9);
            AccessibleName = title; AccessibleDescription = detail;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }
        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            int S(int value) => Math.Max(1, (int)Math.Round(value * DeviceDpi / 96d));
            var text = TextRenderer.MeasureText(_detail, Font, new Size(Math.Max(1, Width - S(16)), int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
            Height = S(142) + text.Height;
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            int S(int value) => Math.Max(1, (int)Math.Round(value * DeviceDpi / 96d));
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var edge = S(58); var icon = new Rectangle((Width - edge) / 2, S(16), edge, edge);
            using var surface = new SolidBrush(Theme.Surface);
            e.Graphics.FillEllipse(surface, icon);
            using var pen = new Pen(Theme.Violet, S(2));
            var ring = Rectangle.Inflate(icon, -S(16), -S(16));
            e.Graphics.DrawEllipse(pen, ring);
            e.Graphics.DrawLine(pen, ring.Left + S(4), ring.Bottom - S(4), ring.Right - S(4), ring.Top + S(4));
            TextRenderer.DrawText(e.Graphics, _title, _heading, new Rectangle(S(4), S(92), Width - S(8), S(28)), Theme.Text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(e.Graphics, _detail, Font, new Rectangle(S(8), S(132), Width - S(16), Height - S(132)), Theme.Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
        }
        protected override void Dispose(bool disposing) { if (disposing) _heading.Dispose(); base.Dispose(disposing); }
    }
}
