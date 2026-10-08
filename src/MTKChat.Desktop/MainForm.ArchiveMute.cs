using System.Drawing.Drawing2D;
using MTKChat.Contracts;

namespace MTKChat.Desktop;

internal sealed partial class MainForm
{
    private bool IsArchivedConversation(Guid roomId) => ChatPreferences()?.IsArchived(roomId) == true;
    private bool IsMutedConversation(Guid roomId, DateTimeOffset? now = null) => ChatPreferences()?.IsMuted(roomId, now) == true;

    // Central notification gate: a personal archive/mute never changes delivery,
    // read receipts, encryption, membership or whether history is still received.
    private bool ShouldNotifyConversation(Guid roomId, DateTimeOffset? now = null) =>
        !IsArchivedConversation(roomId) && !IsMutedConversation(roomId, now);

    private void SetArchivedConversation(Guid roomId, bool archived)
    {
        EnsurePreferencesWritable();
        ChatPreferences()?.SetArchived(roomId, archived);
        FilterConversations(_premiumSearch.Text);
    }

    private void SetConversationMute(Guid roomId, TimeSpan? duration)
    {
        EnsurePreferencesWritable();
        var now = DateTimeOffset.UtcNow;
        ChatPreferences()?.SetMute(roomId, duration is { } finite ? now + finite : null, now);
        RefreshPersonalConversationIndicators();
    }

    private void ClearConversationMute(Guid roomId)
    {
        EnsurePreferencesWritable();
        ChatPreferences()?.ClearMute(roomId);
        RefreshPersonalConversationIndicators();
    }

    private void AddArchiveMuteActions(ContextMenuStrip menu, Func<ConversationSummary> current)
    {
        var archive = menu.Items.AddAction("Sohbeti arşivle", ModernMenuIcon.Archive, (_, _) =>
        {
            try { SetArchivedConversation(current().Id, !IsArchivedConversation(current().Id)); }
            catch (Exception ex) { ShowError(ex.Message); }
        });
        var mute = new ModernMenuItem { Text = "Bildirimleri sessize al", Icon = ModernMenuIcon.Mute };
        foreach (var (title, duration) in new (string, TimeSpan?)[]
                 { ("8 saat", TimeSpan.FromHours(8)), ("1 hafta", TimeSpan.FromDays(7)), ("Süresiz", null) })
        {
            var captured = duration;
            mute.DropDownItems.AddAction(title, ModernMenuIcon.Clock, (_, _) =>
            {
                try { SetConversationMute(current().Id, captured); }
                catch (Exception ex) { ShowError(ex.Message); }
            });
        }
        mute.DropDownItems.Add(new ToolStripSeparator());
        var unmute = mute.DropDownItems.AddAction("Sessize almayı kaldır", ModernMenuIcon.Unmute, (_, _) =>
        {
            try { ClearConversationMute(current().Id); }
            catch (Exception ex) { ShowError(ex.Message); }
        });
        menu.Items.Add(mute);
        menu.Opening += (_, _) =>
        {
            var room = current();
            archive.Text = IsArchivedConversation(room.Id) ? "Arşivden çıkar" : "Sohbeti arşivle";
            archive.AccessibleDescription = "Yalnızca bu hesap ve bilgisayardaki sohbet listesini değiştirir; mesajları silmez.";
            mute.Text = IsMutedConversation(room.Id) ? "Sessize alma süresini değiştir" : "Bildirimleri sessize al";
            unmute.Enabled = IsMutedConversation(room.Id);
        };
    }

    private void UpdateArchiveFilterSummary(IEnumerable<ConversationSummary> conversations)
    {
        if (!_filterButtons.TryGetValue("archived", out var archived)) return;
        var rooms = conversations.Where(room => IsArchivedConversation(room.Id)).ToArray();
        var unread = rooms.Aggregate(0L, (total, room) => total + Math.Max(0, room.UnreadCount));
        var title = rooms.Length == 0 ? "Arşiv" : $"Arşiv ({rooms.Length})";
        if (archived.Text != title)
        {
            archived.Text = title;
            archived.Parent?.PerformLayout();
        }
        archived.AccessibleDescription = $"{rooms.Length} arşivlenmiş sohbet · {unread} okunmamış mesaj";
    }

    private void ConfigureConversationPersonalIndicators(RoundedPanel card, ConversationSummary conversation)
    {
        var badge = new ConversationMuteBadge
        {
            Name = "ConversationMuteIndicator", Visible = IsMutedConversation(conversation.Id),
            AccessibleName = "Bildirimleri sessize alınmış sohbet", Cursor = Cursors.Hand
        };
        card.Controls.Add(badge);
        badge.BringToFront();
        void Arrange()
        {
            var scale = card.DeviceDpi / 96f;
            int Pixels(int logical) => Math.Max(1, (int)Math.Round(logical * scale));
            // An overlay at the avatar's lower-right avoids competing with the
            // unread count, full title and last-message clock/preview slots.
            var edge = Pixels(20);
            var avatarTop = Math.Max(0, (card.Height - Pixels(48)) / 2);
            badge.SetBounds(Pixels(40), avatarTop + Pixels(31), edge, edge);
        }
        card.Layout += (_, _) => Arrange();
        card.DpiChangedAfterParent += (_, _) => Arrange();
        Arrange();
        ApplyConversationPersonalIndicator(card, conversation);
    }

    private void ApplyConversationPersonalIndicator(Control card, ConversationSummary conversation)
    {
        var pin = card.Controls.Find("ConversationPinIndicator", false).FirstOrDefault();
        var pinned = IsPinnedConversation(conversation.Id);
        if (pin is not null && !Equals(pin.Tag, pinned))
        {
            pin.Tag = pinned;
            pin.Visible = pinned;
            card.PerformLayout();
        }
        var muted = IsMutedConversation(conversation.Id);
        var until = ChatPreferences()?.MuteUntil(conversation.Id);
        var badge = card.Controls.Find("ConversationMuteIndicator", false).FirstOrDefault();
        if (badge is not null)
        {
            badge.Visible = muted;
            badge.AccessibleDescription = !muted ? "Bildirimler açık" : until is null ? "Süresiz sessizde" :
                "Sessizde: " + until.Value.ToLocalTime().ToString("dd.MM.yyyy HH:mm");
        }
        card.AccessibleDescription = (pinned ? "Sabitlenmiş · " : "") + (IsArchivedConversation(conversation.Id) ? "Arşivlenmiş · " : "") +
            (muted ? "Bildirimleri sessizde · " : "") + $"{Math.Max(0, conversation.UnreadCount)} okunmamış mesaj";
    }

    private void RefreshPersonalConversationIndicators()
    {
        // Run on the existing background refresh cadence, not another UI timer.
        // Expiring a mute changes only this tiny icon, not the history/control tree.
        foreach (Control card in _conversationList.Controls)
            if (card.Tag is ConversationSummary conversation) ApplyConversationPersonalIndicator(card, conversation);
    }

    private sealed class ConversationMuteBadge : Control
    {
        internal ConversationMuteBadge()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor |
                ControlStyles.ResizeRedraw, true);
            BackColor = Color.Transparent;
            TabStop = false;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (Width < 4 || Height < 4) return;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var edge = Math.Min(Width, Height);
            var scale = edge / 20f;
            PointF P(float x, float y) => new(x * scale, y * scale);
            using var surface = new SolidBrush(Theme.SurfaceRaised);
            e.Graphics.FillEllipse(surface, .5f, .5f, edge - 1, edge - 1);
            using var ink = new Pen(Theme.Muted, 1.4f * scale)
                { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
            e.Graphics.DrawPolygon(ink, [P(5, 8), P(8, 8), P(11, 5), P(11, 15), P(8, 12), P(5, 12)]);
            e.Graphics.DrawLine(ink, P(13, 8), P(16, 11));
            e.Graphics.DrawLine(ink, P(16, 8), P(13, 11));
        }
    }
}
