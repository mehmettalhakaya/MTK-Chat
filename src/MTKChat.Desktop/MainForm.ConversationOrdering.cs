using System.Drawing.Drawing2D;
using MTKChat.Contracts;

namespace MTKChat.Desktop;

internal sealed partial class MainForm
{
    // Compatibility for older servers only: retain dates, never deleted plaintext.
    // Current servers own the persisted, access-checked activity watermark.
    private readonly Dictionary<Guid, ConversationActivity> _conversationActivity = new();
    private sealed record ConversationActivity(DateTimeOffset At, DateTimeOffset? ExpiresAt);

    private DateTimeOffset? ConversationActivityAt(ConversationSummary room)
    {
        EnsurePreviewOwner();
        if (room.ActivityMetadataAvailable) return room.LastActivityAt;
        if (_conversationPreviews.TryGetValue(room.Id, out var preview) && preview.LastAt is { } previewAt &&
            (preview.SummaryAt == room.LastMessageAt || previewAt == room.LastMessageAt) &&
            (!_conversationActivity.TryGetValue(room.Id, out var observed) || previewAt >= observed.At))
            _conversationActivity[room.Id] = new(previewAt, preview.ExpiresAt);
        var date = room.LastMessageAt;
        if (!_conversationActivity.TryGetValue(room.Id, out var known)) return date;
        if (known.ExpiresAt <= DateTimeOffset.UtcNow)
            return date == known.At ? null : date;
        return date is null || known.At > date ? known.At : date;
    }

    private void ObserveConversationActivity(ConversationSummary room, IEnumerable<StoredMessage>? history = null)
    {
        if (room.ActivityMetadataAvailable) { _conversationActivity.Remove(room.Id); return; }
        if (room.LastMessageAt is { } at &&
            (!_conversationActivity.TryGetValue(room.Id, out var known) || at > known.At))
            _conversationActivity[room.Id] = new(at, null);
        var last = history?.Where(message => message.SenderId == _session?.User.Id ||
                message.Payloads.Any(payload => payload.RecipientId == _session?.User.Id) ||
                message.DeletedForEveryone && _conversationActivity.TryGetValue(room.Id, out var eligible) && eligible.At == message.CreatedAt)
            .OrderByDescending(message => message.CreatedAt).FirstOrDefault();
        if (last is not null && (!_conversationActivity.TryGetValue(room.Id, out var prior) || last.CreatedAt >= prior.At))
            _conversationActivity[room.Id] = new(last.CreatedAt, last.ExpiresAt);
    }

    private ConversationSummary[] OrderedConversations(IEnumerable<ConversationSummary> rooms) => rooms
        .OrderByDescending(room => IsPinnedConversation(room.Id))
        .ThenByDescending(ConversationActivityAt)
        .ThenBy(room => room.Id).ToArray();

    private void ApplyConversationOrder()
    {
        // Move existing controls only. Reordering must not select a conversation,
        // reload history, stop audio, clear a draft or dispose a popup owner.
        var cards = _conversationList.Controls.OfType<RoundedPanel>()
            .Where(card => card.Tag is ConversationSummary).ToDictionary(card => ((ConversationSummary)card.Tag!).Id);
        var rooms = OrderedConversations(cards.Values.Select(card => (ConversationSummary)card.Tag!));
        _conversationList.SuspendLayout();
        try
        {
            for (var index = 0; index < rooms.Length; index++)
                if (_conversationList.Controls.GetChildIndex(cards[rooms[index].Id]) != index)
                    _conversationList.Controls.SetChildIndex(cards[rooms[index].Id], index);
        }
        finally { _conversationList.ResumeLayout(); }
    }

    private sealed class ConversationPinBadge : Control
    {
        internal ConversationPinBadge()
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
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var scale = Math.Min(Width, Height) / 20f;
            PointF P(float x, float y) => new(x * scale, y * scale);
            using var ink = new Pen(Theme.Muted, 1.5f * scale)
                { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
            e.Graphics.DrawPolygon(ink, [P(9, 3), P(16, 10), P(13, 11), P(11, 15), P(5, 9), P(9, 7)]);
            e.Graphics.DrawLine(ink, P(8, 12), P(3, 17));
        }
    }
}
