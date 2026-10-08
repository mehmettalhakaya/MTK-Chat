using System.Globalization;
using System.Text;
using MTKChat.Contracts;

namespace MTKChat.Desktop;

// Receipt metadata is supplied by the authenticated history response. This view never
// opens/decrypts a message, acknowledges it, or changes the original recipient set.
internal sealed record MessageInfoView(Guid MessageId, Guid ConversationId, DateTimeOffset SentAt,
    string Kind, MessageDelivery? Delivery, IReadOnlyList<ChatUser> Participants,
    IReadOnlyDictionary<Guid, string>? GroupRoles = null);

internal sealed class MessageInfoPanel : UserControl
{
    private readonly AvatarCache _avatars;
    private readonly Label _title;
    private readonly ModernButton _close;
    private readonly Label _kind;
    private readonly Label _sent;
    private readonly Panel _divider;
    private readonly FlowLayoutPanel _list;
    private readonly ModernConversationViewport _viewport;
    private string? _fingerprint;
    private bool _arranging;

    internal event EventHandler? CloseRequested;
    internal Guid? RenderedMessageId { get; private set; }
    internal (int Read, int Delivered, int Pending) Counts { get; private set; }
    internal IReadOnlyList<Guid> RecipientIds { get; private set; } = Array.Empty<Guid>();
    internal int ScrollOffset => _viewport.Offset;
    internal int MaximumScrollOffset => _viewport.MaximumOffset;

    internal MessageInfoPanel(AvatarCache avatars)
    {
        _avatars = avatars;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Theme.Sidebar;
        ForeColor = Theme.Text;
        Name = "MessageInfoPanel";
        AccessibleName = "Mesaj bilgisi";
        AccessibleRole = AccessibleRole.Pane;
        Margin = Padding.Empty;
        MinimumSize = new Size(280, 0);
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw, true);

        _title = Label("Mesaj bilgisi", 15, Theme.Text, FontStyle.Bold);
        _title.Name = "MessageInfoTitle";
        _close = Theme.GlyphButton("\uE711", "Mesaj bilgisini kapat");
        _close.Name = "MessageInfoClose";
        _close.AccessibleName = "Mesaj bilgisini kapat";
        _close.Click += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty);
        _kind = Label("", 10, Theme.Text);
        _kind.Name = "MessageInfoKind";
        _sent = Label("", 9, Theme.Muted);
        _sent.Name = "MessageInfoSent";
        _divider = new Panel { BackColor = Theme.Divider, Margin = Padding.Empty };
        _list = new FlowLayoutPanel
        {
            Name = "MessageInfoRecipients", FlowDirection = FlowDirection.TopDown,
            WrapContents = false, AutoScroll = true, BackColor = Theme.Sidebar,
            Padding = Padding.Empty, Margin = Padding.Empty
        };
        // Opaque tint is intentional: receipt rows share one fixed, crisp surface and
        // do not stamp/repeat the conversation wallpaper while scrolling or resizing.
        _viewport = new ModernConversationViewport(_list)
        {
            Name = "MessageInfoViewport", Tint = Theme.Sidebar, Dock = DockStyle.None,
            AccessibleName = "Mesajın teslim ve okunma bilgilerini kaydır"
        };
        _list.Resize += (_, _) => ResizeRows();
        Controls.AddRange(new Control[] { _title, _close, _kind, _sent, _divider, _viewport });
        Arrange();
    }

    internal void Render(MessageInfoView view)
    {
        if (IsDisposed) return;
        var fingerprint = Fingerprint(view);
        // Background polling must not reset a user's scroll position, replay GIF
        // avatars, or recreate receipt controls when nothing visible has changed.
        if (_fingerprint == fingerprint) return;
        var sameMessage = RenderedMessageId == view.MessageId;
        var previousOffset = sameMessage ? _viewport.Offset : 0;
        var people = view.Participants.GroupBy(user => user.Id).ToDictionary(group => group.Key, group => group.First());
        // Only original receipt IDs are recipients. Current members can include people
        // who joined later, or omit a departed recipient; neither changes delivery.
        var receipts = (view.Delivery?.Recipients ?? Array.Empty<RecipientReceipt>())
            .GroupBy(receipt => receipt.UserId)
            .Select(group => new RecipientReceipt(group.Key,
                group.Select(receipt => receipt.DeliveredAt).Max(),
                group.Select(receipt => receipt.ReadAt).Max()))
            .ToArray();
        ChatUser Person(RecipientReceipt receipt) => people.GetValueOrDefault(receipt.UserId) ??
            new ChatUser(receipt.UserId, "Kullanıcı", "", false, null);
        var ordered = receipts.OrderBy(receipt => Person(receipt).DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(receipt => receipt.UserId).ToArray();
        var read = ordered.Where(receipt => receipt.ReadAt.HasValue).ToArray();
        var delivered = ordered.Where(receipt => !receipt.ReadAt.HasValue && receipt.DeliveredAt.HasValue).ToArray();
        var pending = ordered.Where(receipt => !receipt.ReadAt.HasValue && !receipt.DeliveredAt.HasValue).ToArray();
        RenderedMessageId = view.MessageId;
        Counts = (read.Length, delivered.Length, pending.Length);
        RecipientIds = ordered.Select(receipt => receipt.UserId).ToArray();
        _kind.Text = KindText(view.Kind);
        _sent.Text = "Gönderildi · " + TimeText(view.SentAt);

        _list.SuspendLayout();
        try
        {
            var previous = _list.Controls.Cast<Control>().ToArray();
            _list.Controls.Clear();
            foreach (var control in previous) control.Dispose();
            if (receipts.Length == 0)
            {
                _list.Controls.Add(new InfoNote("Henüz teslim bilgisi yok",
                    "Alıcı bilgisi geldiğinde burada görünecek.", "MessageInfoUnavailable"));
            }
            else
            {
                AddSection("Okundu", "MessageInfoRead", Theme.Cyan, read,
                    "Henüz paylaşılan okunma bilgisi yok.", Person, view.GroupRoles, true);
                AddSection("Teslim edildi", "MessageInfoDelivered", Theme.Muted, delivered,
                    "Bu bölümde bekleyen alıcı yok.", Person, view.GroupRoles, false);
                AddSection("Bekleniyor", "MessageInfoPending", Theme.Muted, pending,
                    "Tüm alıcılar için teslim bilgisi var.", Person, view.GroupRoles, false);
                _list.Controls.Add(new InfoNote("", "Okundu bilgisi paylaşılmıyorsa mesajın okunup okunmadığı bilinmez.",
                    "MessageInfoPrivacy"));
            }
            ResizeRows();
        }
        finally { _list.ResumeLayout(true); }
        _fingerprint = fingerprint;
        _viewport.PerformLayout();
        _viewport.SynchronizeNow();
        _list.AutoScrollPosition = new Point(0, Math.Clamp(previousOffset, 0, _viewport.MaximumOffset));
        _viewport.SynchronizeNow();
    }

    private void AddSection(string heading, string name, Color color, RecipientReceipt[] receipts,
        string empty, Func<RecipientReceipt, ChatUser> person, IReadOnlyDictionary<Guid, string>? roles, bool read)
    {
        _list.Controls.Add(new SectionHeading(heading, receipts.Length, color) { Name = name });
        if (receipts.Length == 0)
        {
            _list.Controls.Add(new InfoNote("", empty, name + "Empty"));
            return;
        }
        foreach (var receipt in receipts)
        {
            var user = person(receipt);
            var card = new RecipientCard(user, roles?.GetValueOrDefault(user.Id), receipt, read)
            { Name = "ReceiptRecipient_" + user.Id.ToString("N") };
            _list.Controls.Add(card);
            // AvatarCache checks both binding/version and disposal after awaiting a
            // download. A response for a closed/replaced panel cannot paint a new row.
            _ = _avatars.ApplyAsync(card.Avatar, user);
        }
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        Arrange();
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        MinimumSize = new Size(Scale(280), 0);
        Arrange();
    }

    private void Arrange()
    {
        if (_arranging || _viewport is null) return;
        _arranging = true;
        try
        {
            var inset = Scale(20);
            var closeSize = Scale(36);
            _close.SetBounds(Math.Max(inset, Width - inset - closeSize), Scale(17), closeSize, closeSize);
            _title.SetBounds(inset, Scale(17), Math.Max(0, _close.Left - inset - Scale(10)), closeSize);
            _kind.SetBounds(inset, Scale(72), Math.Max(0, Width - inset * 2), Scale(23));
            _sent.SetBounds(inset, Scale(97), Math.Max(0, Width - inset * 2), Scale(22));
            _divider.SetBounds(inset, Scale(137), Math.Max(0, Width - inset * 2), Scale(1));
            var top = Scale(151);
            _viewport.SetBounds(inset, top, Math.Max(0, Width - inset - Scale(7)), Math.Max(0, Height - top - Scale(8)));
            ResizeRows();
        }
        finally { _arranging = false; }
    }

    private void ResizeRows()
    {
        if (_list is null || _list.IsDisposed) return;
        var width = Math.Max(1, _list.ClientSize.Width - _list.Padding.Horizontal);
        foreach (Control child in _list.Controls)
        {
            if (child.Width != width) child.Width = width;
            child.Margin = new Padding(0, 0, 0, child is SectionHeading ? 0 : Scale(6));
            child.PerformLayout();
        }
    }

    // Called with the panel visible by snapshot QA. Checks the actual child controls
    // rather than a mocked layout; all recipient IDs/counts remain separately exposed.
    internal IReadOnlyList<string> VerifyLayout()
    {
        Arrange();
        _list.PerformLayout();
        _viewport.SynchronizeNow();
        var checks = new List<string>();
        void Require(bool valid, string text)
        {
            if (!valid) throw new InvalidOperationException("Message info panel regression: " + text);
            checks.Add(text);
        }
        Require(_title.Right <= _close.Left && _close.Right <= ClientSize.Width, "Receipt title and close button do not overlap");
        Require(_viewport.Bottom <= ClientSize.Height && _viewport.Right <= ClientSize.Width,
            "Receipt viewport fits the panel");
        Require(_list.ClientSize.Width == _viewport.ViewportWidth,
            "Receipt rows exclude the clipped native scrollbar");
        Require(!_list.HorizontalScroll.Visible && _list.ClientSize.Height == _viewport.ClientSize.Height,
            "Receipt panel has no native horizontal rail or lost vertical space");
        Require(_list.Controls.Cast<Control>().All(control => control.Right <= _viewport.ViewportWidth),
            "Receipt rows remain left of the custom right scrollbar");
        Require(_list.Controls.OfType<RecipientCard>().All(card => card.TextFits()),
            "Receipt names and timestamp lines fit without intersecting avatars");
        Require(_list.Controls.OfType<RecipientCard>().Count() == RecipientIds.Count &&
            Counts.Read + Counts.Delivered + Counts.Pending == RecipientIds.Count,
            "Each original receipt ID occupies exactly one status section");
        return checks;
    }

    internal IReadOnlyList<string> VerifyScrolling() => _viewport.VerifyScrolling();

    private int Scale(int value) => Math.Max(1, (int)Math.Round(value * DeviceDpi / 96d));
    private static Label Label(string text, float size, Color color, FontStyle style = FontStyle.Regular) => new()
    {
        Text = text, Font = Theme.Font(size, style), ForeColor = color, BackColor = Theme.Sidebar,
        AutoSize = false, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft, UseMnemonic = false,
        Margin = Padding.Empty
    };
    private static string KindText(string kind) => kind switch
    {
        "image" => "Görsel mesajı", "voice" or "audio" => "Sesli mesaj", "file" => "Dosya mesajı", _ => "Metin mesajı"
    };
    private static string TimeText(DateTimeOffset time) => time.ToLocalTime().ToString("dd MMM yyyy · HH:mm", CultureInfo.GetCultureInfo("tr-TR"));
    private static string Fingerprint(MessageInfoView view)
    {
        var builder = new StringBuilder();
        void Part(string? part) => builder.Append(part?.Length ?? -1).Append(':').Append(part).Append('|');
        Part(view.MessageId.ToString("N")); Part(view.ConversationId.ToString("N"));
        Part(view.SentAt.ToString("O")); Part(view.Kind); Part(view.Delivery?.Status);
        foreach (var receipt in (view.Delivery?.Recipients ?? Array.Empty<RecipientReceipt>()).OrderBy(receipt => receipt.UserId))
        { Part(receipt.UserId.ToString("N")); Part(receipt.DeliveredAt?.ToString("O")); Part(receipt.ReadAt?.ToString("O")); }
        foreach (var user in view.Participants.OrderBy(user => user.Id))
        {
            Part(user.Id.ToString("N")); Part(user.DisplayName); Part(user.Role); Part(user.IsAgent.ToString());
            Part(user.PhotoVersion); Part(view.GroupRoles?.GetValueOrDefault(user.Id));
        }
        return builder.ToString();
    }

    private sealed class SectionHeading : Control
    {
        private readonly string _heading;
        private readonly int _count;
        private readonly Color _color;
        internal SectionHeading(string heading, int count, Color color)
        {
            _heading = heading; _count = count; _color = color;
            BackColor = Theme.Sidebar; Font = Theme.Font(10, FontStyle.Bold);
            AccessibleName = heading + ": " + count;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        }
        protected override void OnLayout(LayoutEventArgs e) { base.OnLayout(e); Height = (int)Math.Round(42 * DeviceDpi / 96d); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var inset = Math.Max(1, DeviceDpi * 4 / 96);
            TextRenderer.DrawText(e.Graphics, _heading, Font, new Rectangle(inset, 0, Math.Max(0, Width - 40), Height),
                _color, TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(e.Graphics, _count.ToString(CultureInfo.InvariantCulture), Font,
                new Rectangle(Math.Max(0, Width - 36), 0, 30, Height), Theme.Muted,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }
    }

    private sealed class InfoNote : Control
    {
        private readonly string _heading;
        private readonly string _detail;
        private readonly Font _headingFont = Theme.Font(10, FontStyle.Bold);
        internal InfoNote(string heading, string detail, string name)
        {
            _heading = heading; _detail = detail; Name = name;
            Font = Theme.Font(9); BackColor = Theme.Sidebar; ForeColor = Theme.Muted;
            AccessibleName = string.Join(" ", new[] { heading, detail }.Where(text => text.Length > 0));
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        }
        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            var pad = Math.Max(4, DeviceDpi * 4 / 96);
            var headingHeight = _heading.Length == 0 ? 0 : TextRenderer.MeasureText(_heading, _headingFont).Height + pad;
            var size = TextRenderer.MeasureText(_detail, Font, new Size(Math.Max(1, Width - pad * 2), int.MaxValue),
                TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
            Height = pad * 3 + headingHeight + size.Height;
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            var pad = Math.Max(4, DeviceDpi * 4 / 96);
            var top = pad;
            if (_heading.Length > 0)
            {
                var height = TextRenderer.MeasureText(_heading, _headingFont).Height;
                TextRenderer.DrawText(e.Graphics, _heading, _headingFont,
                    new Rectangle(pad, top, Math.Max(0, Width - pad * 2), height), Theme.Text,
                    TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                top += height + pad;
            }
            TextRenderer.DrawText(e.Graphics, _detail, Font,
                new Rectangle(pad, top, Math.Max(0, Width - pad * 2), Math.Max(0, Height - top)), Theme.Muted,
                TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
        }
        protected override void Dispose(bool disposing)
        { if (disposing) _headingFont.Dispose(); base.Dispose(disposing); }
    }

    private sealed class RecipientCard : Panel
    {
        internal AvatarView Avatar { get; }
        private readonly Label _name;
        private readonly Label _first;
        private readonly Label? _second;
        internal RecipientCard(ChatUser user, string? groupRole, RecipientReceipt receipt, bool read)
        {
            BackColor = Theme.Sidebar;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Avatar = new AvatarView { Initials = UserPresentation.Initials(user.DisplayName), AvatarColor = Theme.SurfaceHover };
            _name = Label(UserPresentation.Heading(user, groupRole), 9.5f, UserPresentation.RoleColor(user, groupRole), FontStyle.Bold);
            _first = Label(receipt.ReadAt is { } readAt ? "Okundu · " + TimeText(readAt) :
                receipt.DeliveredAt is { } deliveredAt ? "Teslim · " + TimeText(deliveredAt) : "Teslim bilgisi henüz yok", 8.5f,
                read ? Theme.Cyan : Theme.Muted);
            if (read && receipt.DeliveredAt is { } delivered)
                _second = Label("Teslim · " + TimeText(delivered), 8.5f, Theme.Muted);
            Controls.AddRange(_second is null ? new Control[] { Avatar, _name, _first } : new Control[] { Avatar, _name, _first, _second });
            AccessibleName = _name.Text;
            AccessibleDescription = _first.Text + (_second is null ? "" : "; " + _second.Text);
        }
        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (Avatar is null) return;
            int Scale(int value) => Math.Max(1, (int)Math.Round(value * DeviceDpi / 96d));
            var edge = Scale(38);
            Height = Scale(_second is null ? 67 : 84);
            Avatar.SetBounds(Scale(4), Scale(11), edge, edge);
            var left = Avatar.Right + Scale(12);
            var width = Math.Max(0, Width - left - Scale(4));
            _name.SetBounds(left, Scale(6), width, Scale(23));
            _first.SetBounds(left, Scale(31), width, Scale(20));
            _second?.SetBounds(left, Scale(53), width, Scale(20));
        }
        internal bool TextFits() => _name.Left > Avatar.Right && _name.Right <= ClientSize.Width &&
            _first.Top >= _name.Bottom && _first.Bottom <= ClientSize.Height &&
            (_second is null || (_second.Top >= _first.Bottom && _second.Bottom <= ClientSize.Height));
    }
}
