using MTKChat.Contracts;

namespace MTKChat.Server.Services;

public sealed partial class ChatState
{
    // Everyone-deletion erases encrypted payloads. Preserve only the original
    // audience ids so a tombstone cannot disclose activity to an unaddressed or
    // newly joined member. This table contains no message text, ciphertext or keys.
    private sealed record DeletedMessageAudienceData(Guid MessageId, Guid[] UserIds);
    private readonly Dictionary<Guid, HashSet<Guid>> _deletedMessageAudiences = new();

    private bool CanSeeDeletionUnsafe(Guid userId, StoredMessage message) =>
        message.DeletedForEveryone && (message.SenderId == userId ||
            _deletedMessageAudiences.TryGetValue(message.Id, out var audience) && audience.Contains(userId));

    private DateTimeOffset? LastDeletedMessageAtUnsafe(ConversationState room, Guid userId, DateTimeOffset now) =>
        room.MessageIds.Select(id => _messages.GetValueOrDefault(id))
            .Where(message => message is not null && CanSeeDeletionUnsafe(userId, message) &&
                !_hiddenMessages.Contains((userId, message.Id)) && !IsBlockedUnsafe(userId, message.SenderId) &&
                (message.ExpiresAt is null || message.ExpiresAt > now))
            .Select(message => (DateTimeOffset?)message!.CreatedAt)
            .Max();

    private void RestoreDeletedMessageAudiencesUnsafe(DeletedMessageAudienceData[]? rows)
    {
        _deletedMessageAudiences.Clear();
        foreach (var row in rows ?? [])
        {
            if (row is null || row.UserIds is null || !_messages.TryGetValue(row.MessageId, out var message) ||
                !message.DeletedForEveryone || !_conversations.TryGetValue(message.ConversationId, out var room) ||
                !room.MessageIds.Contains(message.Id)) continue;
            // Duplicate snapshot rows cannot enlarge a previously restored audience.
            _deletedMessageAudiences.TryAdd(message.Id, row.UserIds.Where(id => id != Guid.Empty).ToHashSet());
        }
        // Legacy snapshots lack recipient evidence after payload erasure. Do not
        // infer it from current membership or timestamps; only the sender's own
        // marker remains safe, and CanSeeDeletionUnsafe handles that fallback.
    }
}
