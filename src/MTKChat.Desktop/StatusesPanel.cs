using MTKChat.Contracts;

namespace MTKChat.Desktop;

// Feed polling downloads metadata only. Encrypted media is fetched and decrypted
// on an explicit view, never for every list row or every conversation refresh.
internal sealed class StatusesPanel : UserControl
{
    private readonly ChatApiClient _api;
    private ChatUser _owner;
    private readonly AvatarCache _avatars;
    private readonly Func<Task> _create;
    private readonly Func<StatusSummary, Task> _view;
    private readonly ModernButton _new, _refresh;
    private readonly Label _feedback = new();
    private readonly FlowLayoutPanel _list = new() { FlowDirection = FlowDirection.TopDown, WrapContents = false,
        AutoScroll = true, BackColor = Theme.Sidebar, Margin = Padding.Empty };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 30000 };
    private CancellationTokenSource? _read;
    private int _revision;
    private bool _busy;
    internal event Action<bool>? BusyChanged;
    internal bool IsBusy => _busy;
    internal int FeedCountForQa => _list.Controls.Cast<Control>().Count(c => c.Tag is StatusSummary);
    internal string FeedbackForQa => _feedback.Text;
    internal void RefreshOwner(ChatUser owner) { if (owner.Id == _owner.Id) _owner = owner; }

    internal StatusesPanel(ChatApiClient api, ChatUser owner, AvatarCache avatars, Func<Task> create, Func<StatusSummary, Task> view)
    {
        _api = api; _owner = owner; _avatars = avatars; _create = create; _view = view;
        SuspendLayout(); AutoScaleMode = AutoScaleMode.Dpi; AutoScaleDimensions = new SizeF(96, 96);
        BackColor = Theme.Sidebar; Margin = Padding.Empty;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4,
            Padding = new Padding(18, 0, 18, 12), Margin = Padding.Empty, BackColor = Theme.Sidebar };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(new Label { Text = "Seçili kişilerle paylaş · 24 saat", Dock = DockStyle.Fill,
            ForeColor = Theme.Muted, Font = Theme.Font(9.5f), Margin = Padding.Empty }, 0, 0);
        var actions = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        actions.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 65)); actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 35));
        _new = Theme.Button("＋ Yeni durum", ButtonKind.Primary); _refresh = Theme.Button("Yenile");
        _new.Dock = _refresh.Dock = DockStyle.Fill; _new.Margin = new Padding(0, 0, 8, 8); _refresh.Margin = new Padding(0, 0, 0, 8);
        _new.Click += async (_, _) => await RunAsync(_create); _refresh.Click += async (_, _) => await LoadAsync();
        actions.Controls.Add(_new, 0, 0); actions.Controls.Add(_refresh, 1, 0); layout.Controls.Add(actions, 0, 1);
        _feedback.Dock = DockStyle.Fill; _feedback.ForeColor = Theme.Muted; _feedback.Font = Theme.Font(9);
        _feedback.AutoEllipsis = false; _feedback.Margin = Padding.Empty; layout.Controls.Add(_feedback, 0, 2);
        var viewport = new ModernConversationViewport(_list) { Dock = DockStyle.Fill, Tint = Theme.Sidebar, Margin = Padding.Empty };
        layout.Controls.Add(viewport, 0, 3); _list.Resize += (_, _) => SizeCards();
        Controls.Add(layout); _timer.Tick += async (_, _) => { if (Visible && !_busy) await LoadAsync(); };
        VisibleChanged += (_, _) => { if (Visible) _timer.Start(); else CancelPending(); };
        ResumeLayout(true);
    }
    internal async Task LoadAsync()
    {
        if (IsDisposed || Disposing || _busy) return;
        CancelPending(); var revision = ++_revision; var cts = _read = new CancellationTokenSource(); var token = cts.Token;
        _refresh.Enabled = false; _feedback.ForeColor = Theme.Muted; _feedback.Text = "Durumlar yükleniyor...";
        try
        {
            var feedTask = _api.GetStatusesAsync(token); var usersTask = _api.GetUsersAsync(token);
            await Task.WhenAll(feedTask, usersTask);
            if (IsDisposed || revision != _revision || token.IsCancellationRequested) return;
            var users = (await usersTask).Append(_owner).DistinctBy(u => u.Id).ToDictionary(u => u.Id);
            Render((await feedTask).Where(s => s.ExpiresAt > DateTimeOffset.UtcNow && s.Id != Guid.Empty &&
                    users.TryGetValue(s.SenderId, out var person) && !person.IsAgent).OrderByDescending(s => s.SenderId == _owner.Id)
                .ThenByDescending(s => s.CreatedAt).Take(200).ToArray(), users);
            _feedback.Text = "";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { if (!IsDisposed && revision == _revision) { _feedback.ForeColor = Theme.Warning; _feedback.Text = "Durumlar alınamadı. Yenile ile tekrar deneyin. " + ex.Message; } }
        finally { if (!IsDisposed && revision == _revision) _refresh.Enabled = !_busy; }
        if (!IsDisposed && Visible && revision == _revision) _timer.Start();
    }
    private void Render(StatusSummary[] feed, IReadOnlyDictionary<Guid, ChatUser> users)
    {
        _list.SuspendLayout();
        try
        {
            foreach (Control old in _list.Controls.Cast<Control>().ToArray()) old.Dispose();
            if (feed.Length == 0)
            {
                _list.Controls.Add(new Label { Text = "Henüz durum yok\n\nBir durum paylaş veya arkadaşlarının paylaşmasını bekle.",
                    Height = S(180), ForeColor = Theme.Muted, Font = Theme.Font(11), TextAlign = ContentAlignment.MiddleCenter,
                    Margin = Padding.Empty, BackColor = Theme.Sidebar });
            }
            foreach (var status in feed)
            {
                var user = users.GetValueOrDefault(status.SenderId);
                // If the authenticated directory no longer contains a sender, do
                // not invent their identity or offer a now-unresolvable viewer.
                if (user is null || user.IsAgent || status.Id == Guid.Empty) continue;
                var card = new RoundedPanel { Tag = status, Height = S(124), FillColor = Theme.Surface,
                    BorderColor = Theme.Divider, CornerRadius = 14, Margin = new Padding(0, 0, 0, S(8)), Padding = new Padding(S(10)) };
                var top = new TableLayoutPanel { Dock = DockStyle.Top, Height = S(60), ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
                top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, S(52))); top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                var avatar = new AvatarView { Anchor = AnchorStyles.None, Size = new Size(S(40), S(40)), Margin = Padding.Empty };
                _ = _avatars.ApplyAsync(avatar, user);
                var labels = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, Margin = Padding.Empty };
                labels.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                labels.RowStyles.Add(new RowStyle(SizeType.Percent, 50)); labels.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
                labels.Controls.Add(new Label { Text = user.Id == _owner.Id ? "Durumum" : user.DisplayName, Dock = DockStyle.Fill,
                    ForeColor = Theme.Text, Font = Theme.Font(10.5f, FontStyle.Bold), AutoEllipsis = true, UseMnemonic = false, Margin = Padding.Empty }, 0, 0);
                labels.Controls.Add(new Label { Text = status.CreatedAt.ToLocalTime().ToString("dd.MM.yyyy HH:mm") + (status.Kind == "text" ? " · Metin" : " · Görsel"),
                    Dock = DockStyle.Fill, ForeColor = Theme.Muted, Font = Theme.Font(8.5f), Margin = Padding.Empty, AutoEllipsis = true }, 0, 1);
                top.Controls.Add(avatar, 0, 0); top.Controls.Add(labels, 1, 0);
                var actions = new TableLayoutPanel { Dock = DockStyle.Bottom, Height = S(38), RowCount = 1, ColumnCount = user.Id == _owner.Id ? 2 : 1, Margin = Padding.Empty };
                actions.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, user.Id == _owner.Id ? 65 : 100));
                var show = Theme.Button("Göster"); show.Dock = DockStyle.Fill; show.Margin = Padding.Empty;
                show.Click += async (_, _) => await RunAsync(() => _view(status)); actions.Controls.Add(show, 0, 0);
                if (user.Id == _owner.Id)
                {
                    actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 35));
                    var remove = Theme.Button("Sil", ButtonKind.Danger); remove.Dock = DockStyle.Fill; remove.Margin = new Padding(S(6), 0, 0, 0);
                    remove.Click += async (_, _) => await DeleteAsync(status); actions.Controls.Add(remove, 1, 0);
                }
                card.Controls.Add(top); card.Controls.Add(actions); _list.Controls.Add(card);
            }
            SizeCards();
        }
        finally { _list.ResumeLayout(true); }
    }
    private async Task DeleteAsync(StatusSummary status)
    {
        if (status.SenderId != _owner.Id || _busy) return;
        if (MessageBox.Show(this, "Bu durum seçtiğin kişilerden de silinsin mi?", "Durumu sil", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        await RunAsync(async () => await _api.DeleteStatusAsync(status.Id));
    }
    private async Task RunAsync(Func<Task> action)
    {
        if (_busy || IsDisposed) return;
        CancelPending(); SetBusy(true);
        try { await action(); }
        catch (Exception ex) { if (!IsDisposed) { _feedback.ForeColor = Theme.Warning; _feedback.Text = ex.Message; } }
        finally { SetBusy(false); }
        if (!IsDisposed && Visible) await LoadAsync();
    }
    private void SetBusy(bool value)
    {
        _busy = value; if (IsDisposed || Disposing) return;
        _new.Enabled = _refresh.Enabled = _list.Enabled = !value; BusyChanged?.Invoke(value);
    }
    private int S(int logical) => Math.Max(1, (int)Math.Round(logical * DeviceDpi / 96f));
    private void SizeCards() { foreach (Control card in _list.Controls) card.Width = Math.Max(S(120), _list.ClientSize.Width - S(4)); }
    internal void CancelPending()
    {
        _timer.Stop(); _revision++; _read?.Cancel(); _read?.Dispose(); _read = null;
        if (!IsDisposed && !Disposing) _refresh.Enabled = !_busy;
    }
    protected override void Dispose(bool disposing) { if (disposing) { CancelPending(); _timer.Dispose(); } base.Dispose(disposing); }
}

internal sealed class StatusViewerForm : ModernForm
{
    private readonly PictureBox? _picture;
    private bool _imageDisposed;
    internal StatusViewerForm(ChatUser sender, StoredStatus status, byte[] content)
    {
        AutoScaleMode = AutoScaleMode.Dpi; AutoScaleDimensions = new SizeF(96, 96);
        Text = "MTK Chat · Durum"; Icon = Theme.AppIcon(); BackColor = Theme.Canvas;
        Size = new Size(650, 680); MinimumSize = new Size(440, 450); StartPosition = FormStartPosition.CenterParent;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(24), Margin = Padding.Empty };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
        layout.Controls.Add(new Label { Text = sender.DisplayName + "\n" + status.CreatedAt.ToLocalTime().ToString("dd.MM.yyyy HH:mm"),
            Dock = DockStyle.Fill, ForeColor = Theme.Text, Font = Theme.Font(13, FontStyle.Bold), UseMnemonic = false, Margin = Padding.Empty }, 0, 0);
        var card = new RoundedPanel { Dock = DockStyle.Fill, FillColor = Theme.Surface, BorderColor = Theme.Divider,
            CornerRadius = 18, Padding = new Padding(20), Margin = new Padding(0, 0, 0, 10) };
        if (status.Kind == "text")
        {
            var text = new System.Text.UTF8Encoding(false, true).GetString(content);
            if (text.Length > 2000 || string.IsNullOrWhiteSpace(text)) throw new InvalidDataException("Durum metni geçersiz.");
            var memo = new DevExpress.XtraEditors.MemoEdit { Dock = DockStyle.Fill, Text = text };
            memo.Properties.ReadOnly = true; memo.Properties.BorderStyle = DevExpress.XtraEditors.Controls.BorderStyles.NoBorder;
            memo.Properties.ScrollBars = ScrollBars.None;
            memo.Properties.Appearance.BackColor = Theme.Surface; memo.Properties.Appearance.ForeColor = Theme.Text;
            memo.Properties.Appearance.Font = Theme.Font(18); memo.Properties.Appearance.Options.UseFont = true;
            memo.Properties.Appearance.Options.UseForeColor = memo.Properties.Appearance.Options.UseBackColor = true; card.Controls.Add(memo);
        }
        else
        {
            using var stream = new MemoryStream(content); using var decoded = Image.FromStream(stream, false, true);
            if ((long)decoded.Width * decoded.Height > 24_000_000) throw new InvalidDataException("Durum görseli çok büyük.");
            _picture = new PictureBox { Dock = DockStyle.Fill, Image = new Bitmap(decoded), SizeMode = PictureBoxSizeMode.Zoom, BackColor = Theme.Surface };
            card.Controls.Add(_picture);
        }
        layout.Controls.Add(card, 0, 1);
        var close = Theme.Button("Kapat"); close.Dock = DockStyle.Fill; close.Click += (_, _) => Close(); layout.Controls.Add(close, 0, 2);
        Controls.Add(layout);
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_imageDisposed && _picture is not null)
        { _imageDisposed = true; var old = _picture.Image; _picture.Image = null; old?.Dispose(); }
        base.Dispose(disposing);
    }
}
