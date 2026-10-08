using MTKChat.Contracts;

namespace MTKChat.Server.Services;

public enum PinMessageResult { Success, Forbidden, NotFound, InvalidDuration, CapacityReached }

public sealed partial class ChatState
{
    private const int MaximumConversationPins = 3;
    private readonly Dictionary<Guid, PinnedMessageView> _pins = new();

    public IReadOnlyList<PinnedMessageView>? GetPinnedMessages(Guid viewerId, Guid conversationId)
    {
        lock (_gate)
        {
            // Even site administrators need current membership. A pinned preview
            // must never become an alternative path to another room's history.
            if (!PinMemberUnsafe(viewerId, conversationId, out _)) return null;
            var now = _time.GetUtcNow();
            return _pins.Values.Where(pin => pin.ConversationId == conversationId &&
                    SharedPinActiveUnsafe(pin, now) && CanViewPinnedMessageUnsafe(viewerId, pin.MessageId, now))
                .OrderByDescending(pin => pin.PinnedAt).ThenBy(pin => pin.MessageId).ToArray();
        }
    }

    public PinMessageResult PinMessage(Guid actorId, Guid conversationId, PinMessageRequest? request,
        out PinnedMessageView? result)
    {
        lock (_gate)
        {
            result = null;
            if (!CanManagePinsUnsafe(actorId, conversationId)) return PinMessageResult.Forbidden;
            if (request is null || request.MessageId == Guid.Empty || request.DurationHours is not (24 or 168 or 720))
                return PinMessageResult.InvalidDuration;
            var now = _time.GetUtcNow();
            if (!_messages.TryGetValue(request.MessageId, out var message) || message.ConversationId != conversationId ||
                !CanViewPinnedMessageUnsafe(actorId, request.MessageId, now)) return PinMessageResult.NotFound;
            var active = _pins.Values.Where(pin => pin.ConversationId == conversationId && SharedPinActiveUnsafe(pin, now)).ToArray();
            if (active.Length >= MaximumConversationPins && !active.Any(pin => pin.MessageId == request.MessageId))
                return PinMessageResult.CapacityReached;
            if (now > DateTimeOffset.MaxValue.AddHours(-request.DurationHours)) return PinMessageResult.InvalidDuration;

            // Prune only globally unavailable rows: hiding/clearing is personal
            // and must not remove another member's shared pin. Save failures roll
            // back both pruning and the new/renewed pin atomically.
            var previous = _pins.ToArray();
            foreach (var stale in _pins.Values.Where(pin => !SharedPinActiveUnsafe(pin, now)).ToArray()) _pins.Remove(stale.MessageId);
            var created = new PinnedMessageView(message.Id, conversationId, actorId, now, now.AddHours(request.DurationHours));
            _pins[message.Id] = created;
            try { PersistUnsafe(); }
            catch { RestorePinDictionaryUnsafe(previous); throw; }
            result = created;
            return PinMessageResult.Success;
        }
    }

    public PinMessageResult UnpinMessage(Guid actorId, Guid conversationId, Guid messageId)
    {
        lock (_gate)
        {
            if (!CanManagePinsUnsafe(actorId, conversationId)) return PinMessageResult.Forbidden;
            var now = _time.GetUtcNow();
            // Knowing a message id is not sufficient to reveal/remove a pin for
            // history the caller never received, or personally removed earlier.
            if (!_pins.TryGetValue(messageId, out var pin) || pin.ConversationId != conversationId ||
                !SharedPinActiveUnsafe(pin, now) || !CanViewPinnedMessageUnsafe(actorId, messageId, now))
                return PinMessageResult.NotFound;
            _pins.Remove(messageId);
            try { PersistUnsafe(); }
            catch { _pins[messageId] = pin; throw; }
            return PinMessageResult.Success;
        }
    }

    private bool PinMemberUnsafe(Guid actorId, Guid conversationId, out ConversationState? room)
    {
        room = _conversations.GetValueOrDefault(conversationId);
        return room is not null && _users.TryGetValue(actorId, out var actor) && !actor.IsAgent &&
            !_bannedUsers.Contains(actorId) && !_chatBannedUsers.Contains((conversationId, actorId)) &&
            room.MemberIds.Contains(actorId);
    }

    private bool CanManagePinsUnsafe(Guid actorId, Guid conversationId)
    {
        if (!PinMemberUnsafe(actorId, conversationId, out var room)) return false;
        var now = _time.GetUtcNow();
        if (_mutedUsers.TryGetValue(actorId, out var globalMute) && (globalMute is null || globalMute > now) ||
            _chatMutedUsers.TryGetValue((conversationId, actorId), out var chatMute) && (chatMute is null || chatMute > now)) return false;
        if (room!.Kind == "direct")
            return !room.MemberIds.Any(other => other != actorId &&
                (IsBlockedUnsafe(actorId, other) || IsBlockedUnsafe(other, actorId)));
        return _users[actorId].Role == "admin" || _groupRoles.GetValueOrDefault((conversationId, actorId)) is "admin" or "mod";
    }

    private bool CanViewPinnedMessageUnsafe(Guid viewerId, Guid messageId, DateTimeOffset now) =>
        _messages.TryGetValue(messageId, out var message) && !message.DeletedForEveryone &&
        (message.ExpiresAt is null || message.ExpiresAt > now) &&
        _conversations.TryGetValue(message.ConversationId, out var room) && room.MessageIds.Contains(messageId) &&
        !_hiddenConversations.Contains((message.ConversationId, viewerId)) &&
        !_hiddenMessages.Contains((viewerId, messageId)) && !IsBlockedUnsafe(viewerId, message.SenderId) &&
        message.Payloads.Any(payload => payload.RecipientId == viewerId);

    private bool SharedPinActiveUnsafe(PinnedMessageView pin, DateTimeOffset now) => pin.ExpiresAt > now &&
        _conversations.TryGetValue(pin.ConversationId, out var room) && room.MessageIds.Contains(pin.MessageId) &&
        _messages.TryGetValue(pin.MessageId, out var message) && message.ConversationId == pin.ConversationId &&
        !message.DeletedForEveryone && (message.ExpiresAt is null || message.ExpiresAt > now);

    private void RestorePinDictionaryUnsafe(KeyValuePair<Guid, PinnedMessageView>[] previous)
    {
        _pins.Clear();
        foreach (var item in previous) _pins[item.Key] = item.Value;
    }

    private void RestorePinsUnsafe(PinnedMessageView[]? rows)
    {
        _pins.Clear();
        var now = _time.GetUtcNow();
        foreach (var pin in rows ?? [])
        {
            // Older snapshots omit Pins entirely. Corrupt/duplicate entries do
            // not consume capacity or grant access; visibility is checked again
            // per current member after site-account synchronization.
            if (pin is null || pin.MessageId == Guid.Empty || pin.ConversationId == Guid.Empty || pin.PinnedBy == Guid.Empty ||
                pin.PinnedAt == default || pin.PinnedAt > now && pin.PinnedAt - now > TimeSpan.FromMinutes(5) ||
                (pin.ExpiresAt - pin.PinnedAt).TotalHours is not (24 or 168 or 720) ||
                _users.TryGetValue(pin.PinnedBy, out var author) && author.IsAgent ||
                !SharedPinActiveUnsafe(pin, now) ||
                _pins.Values.Count(existing => existing.ConversationId == pin.ConversationId) >= MaximumConversationPins) continue;
            _pins.TryAdd(pin.MessageId, pin);
        }
    }
}
