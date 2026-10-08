using MTKChat.Contracts;

namespace MTKChat.Server.Services;

public sealed partial class ChatState
{
    // One metadata-only high-water mark per participant. Joining a group does
    // not grant historical metadata, and deleting a message never stores its
    // plaintext/ciphertext in a second history merely to preserve this date.
    private sealed record ConversationActivityData(Guid ConversationId, Guid UserId,
        Guid SenderId, DateTimeOffset At, DateTimeOffset? ExpiresAt);
    private readonly Dictionary<(Guid ConversationId, Guid UserId), ConversationActivityData> _conversationActivity = new();

    private IReadOnlyList<((Guid ConversationId, Guid UserId) Key, ConversationActivityData? Previous)>
        RecordConversationActivityUnsafe(StoredMessage message)
    {
        var changes = new List<((Guid ConversationId, Guid UserId), ConversationActivityData?)>();
        if (message.ExpiresAt <= _time.GetUtcNow()) return changes;
        foreach (var userId in message.Payloads.Select(payload => payload.RecipientId).Append(message.SenderId).Distinct())
        {
            if (IsBlockedUnsafe(userId, message.SenderId)) continue;
            var key = (message.ConversationId, userId);
            var previous = _conversationActivity.GetValueOrDefault(key);
            if (previous is not null && previous.At >= message.CreatedAt &&
                !IsBlockedUnsafe(userId, previous.SenderId) && !(previous.ExpiresAt <= _time.GetUtcNow())) continue;
            changes.Add((key, previous));
            _conversationActivity[key] = new(message.ConversationId, userId, message.SenderId,
                message.CreatedAt, message.ExpiresAt);
        }
        return changes;
    }

    private DateTimeOffset? ConversationActivityAtUnsafe(Guid roomId, Guid userId,
        DateTimeOffset? visibleMessageAt, DateTimeOffset now)
    {
        // Old snapshots have no watermark. Their currently visible timestamp is
        // a safe fallback, never a reason to reveal hidden/pre-join messages.
        if (!_conversationActivity.TryGetValue((roomId, userId), out var activity) ||
            IsBlockedUnsafe(userId, activity.SenderId) || activity.ExpiresAt <= now)
            return visibleMessageAt;
        return visibleMessageAt > activity.At ? visibleMessageAt : activity.At;
    }

    private void RestoreConversationActivityUnsafe(ConversationActivityData[]? rows)
    {
        _conversationActivity.Clear();
        foreach (var row in rows ?? [])
        {
            if (row is null || row.SenderId == Guid.Empty || row.UserId == Guid.Empty || row.At == default ||
                !_conversations.TryGetValue(row.ConversationId, out var room) || !room.MemberIds.Contains(row.UserId)) continue;
            var key = (row.ConversationId, row.UserId);
            if (!_conversationActivity.TryGetValue(key, out var previous) || row.At > previous.At)
                _conversationActivity[key] = row;
        }
    }
}
