using MTKChat.Contracts;

namespace MTKChat.Desktop;

internal sealed partial class MainForm
{
    // Keep only the controls already owned by the selected conversation. Reusing a
    // verified row avoids decrypting, measuring and decoding old content on every
    // new message, and keeps an in-progress voice message playing.
    private readonly Dictionary<Guid, RenderedMessageRow> _renderedMessageRows = new();
    private readonly Dictionary<(DateTime Date, int Order), MessageDateDivider> _renderedDateDividers = new();
    private int _messageRowsCreated;
    private int _historyUiYieldCount;
    private int _conversationReconcilePasses;
    private int _bubbleResizePasses;
    private int _messageLayoutRevision;
    private int _bubbleLayoutRevision = -1;
    private int _bubbleLayoutWidth = -1;
    private int _bubbleLayoutHeight = -1;
    private bool _resizingBubbles;
    private Action<int>? _historyProgressObserverForQa;

    private sealed record MessageRenderKey(Guid ClientMessageId, Guid ConversationId, Guid SenderId,
        string Kind, DateTimeOffset CreatedAt, DateTimeOffset? ExpiresAt, bool DeletedForEveryone,
        EncryptedPayload? Payload, EncryptedAttachment? Attachment, DateTimeOffset? DeleteForEveryoneUntil,
        ChatUser Sender, string? GroupRole);

    private sealed record RenderedMessageRow(MessageRow Row, MessageRenderKey Key);

    private MessageRenderKey MessageKeyFor(StoredMessage message)
    {
        var sender = message.SenderId == _session!.User.Id ? _session.User :
            _selectedConversation!.Participants.FirstOrDefault(user => user.Id == message.SenderId) ??
            new ChatUser(message.SenderId, "Kullanıcı", "", false, null);
        return new MessageRenderKey(message.ClientMessageId, message.ConversationId, message.SenderId,
            message.Kind, message.CreatedAt, message.ExpiresAt, message.DeletedForEveryone,
            message.Payloads.FirstOrDefault(payload => payload.RecipientId == _session.User.Id),
            message.Attachment, message.DeleteForEveryoneUntil, sender, GroupRole(sender.Id));
    }

    private bool MessageRowsMatch(IReadOnlyDictionary<Guid, MessageRenderKey> keys) =>
        keys.Count == _renderedMessageRows.Count && keys.All(pair =>
            _renderedMessageRows.TryGetValue(pair.Key, out var rendered) &&
            !rendered.Row.IsDisposed && rendered.Row.Parent == _messageList && rendered.Key == pair.Value);

    private void ClearRenderedMessageCache()
    {
        // The control tree, not the cache, owns images, menus and players. Never
        // dispose a reused row merely because a canceled batch drops its cache.
        _renderedMessageRows.Clear();
        _renderedDateDividers.Clear();
    }

    private static bool ConversationVisualEquals(ConversationSummary left, ConversationSummary right) =>
        left.Id == right.Id && left.Title == right.Title && left.LastMessagePreview == right.LastMessagePreview &&
        left.LastMessageAt == right.LastMessageAt && left.UnreadCount == right.UnreadCount &&
        left.LastActivityAt == right.LastActivityAt && left.ActivityMetadataAvailable == right.ActivityMetadataAvailable &&
        left.LastDeletedMessageAt == right.LastDeletedMessageAt && left.DeletedMessageMetadataAvailable == right.DeletedMessageMetadataAvailable &&
        left.PhotoVersion == right.PhotoVersion && left.Kind == right.Kind &&
        left.Participants.SequenceEqual(right.Participants) &&
        (left.GroupRoles?.Count ?? 0) == (right.GroupRoles?.Count ?? 0) &&
        (left.GroupRoles is null || left.GroupRoles.All(pair => right.GroupRoles?.GetValueOrDefault(pair.Key) == pair.Value));

    private bool ConversationPopupOpen() => _conversationList.Controls.Cast<Control>()
        .Any(card => card.ContextMenuStrip is { Visible: true });

    private bool MessagePopupOpen() => _messageList.Controls.OfType<MessageRow>()
        .Any(row => row.Controls.Cast<Control>().Any(child => child.ContextMenuStrip is { Visible: true }));

    private bool ReconcileConversationCards(IReadOnlyList<ConversationSummary> conversations)
    {
        conversations = OrderedConversations(conversations);
        var prior = _conversationList.Controls.OfType<RoundedPanel>().ToArray();
        if (prior.Length == conversations.Count && prior.Select((card, index) =>
            card.Tag is ConversationSummary summary && ConversationVisualEquals(summary, conversations[index])).All(same => same))
            return false;

        _conversationReconcilePasses++;
        var old = prior.ToDictionary(card => ((ConversationSummary)card.Tag!).Id);
        var ids = conversations.Select(conversation => conversation.Id).ToHashSet();
        _conversationList.SuspendLayout();
        try
        {
            foreach (var obsolete in prior.Where(card => !ids.Contains(((ConversationSummary)card.Tag!).Id))) obsolete.Dispose();
            for (var index = 0; index < conversations.Count; index++)
            {
                var conversation = conversations[index];
                if (old.TryGetValue(conversation.Id, out var existing) && existing.Tag is ConversationSummary previous &&
                    ConversationVisualEquals(previous with { LastActivityAt = conversation.LastActivityAt,
                        ActivityMetadataAvailable = conversation.ActivityMetadataAvailable,
                        LastDeletedMessageAt = conversation.LastDeletedMessageAt,
                        DeletedMessageMetadataAvailable = conversation.DeletedMessageMetadataAvailable }, conversation))
                {
                    // A metadata-only movement does not invalidate an avatar/menu.
                    existing.Tag = conversation;
                    ApplyConversationPreviewLabel(conversation);
                }
                if (!old.TryGetValue(conversation.Id, out var card) ||
                    !ConversationVisualEquals((ConversationSummary)card.Tag!, conversation))
                {
                    card?.Dispose();
                    card = CreateConversationCard(conversation);
                    _conversationList.Controls.Add(card);
                }
                if (_conversationList.Controls.GetChildIndex(card) != index)
                    _conversationList.Controls.SetChildIndex(card, index);
            }
            ResizeConversationCards();
            FilterConversations(_premiumSearch.Text);
        }
        finally { _conversationList.ResumeLayout(); }
        return true;
    }
}
