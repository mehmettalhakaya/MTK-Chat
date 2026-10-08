using MTKChat.Contracts;

namespace MTKChat.Server.Services;

public sealed partial class ChatState
{
    private readonly Dictionary<(Guid MessageId, Guid UserId), RecipientReceipt> _receipts = new();

    private bool CanReceiveUnsafe(Guid userId, StoredMessage message) =>
        message.SenderId != userId && !message.DeletedForEveryone &&
        (message.ExpiresAt is null || message.ExpiresAt > DateTimeOffset.UtcNow) &&
        _users.ContainsKey(userId) && !IsBanned(userId) && IsMember(userId, message.ConversationId) &&
        !_hiddenMessages.Contains((userId, message.Id)) && !IsBlockedUnsafe(userId, message.SenderId) &&
        message.Payloads.Any(p => p.RecipientId == userId);

    // Download real encrypted envelopes even when their conversation is not currently selected.
    // A bounded batch avoids repeatedly downloading already acknowledged images/voice recordings.
    public IReadOnlyList<StoredMessage> GetDeliveryInbox(Guid userId)
    {
        lock (_gate)
        {
            var result = new List<StoredMessage>();
            long bytes = 0;
            foreach (var message in _messages.Values.OrderBy(m => m.CreatedAt).ThenBy(m => m.Id))
            {
                if (!CanReceiveUnsafe(userId, message) || _receipts.ContainsKey((message.Id, userId))) continue;
                var payloads = message.Payloads.Where(p => p.RecipientId == userId).ToArray();
                var size = payloads.Sum(p => (long)p.Ciphertext.Length);
                if (result.Count > 0 && bytes + size > 12 * 1024 * 1024) break;
                result.Add(message with { Payloads = payloads, Delivery = null });
                bytes += size;
                if (result.Count == 32) break;
            }
            return result;
        }
    }

    // The caller cannot choose a recipient or timestamp. Repeat ACKs preserve the first times.
    // Deleted/expired/blocked messages may race a downloaded batch: skip them rather than
    // rejecting the whole batch and starving other recipients' valid acknowledgements.
    public int AcknowledgeMessages(Guid userId, IReadOnlyList<Guid> ids, bool read)
    {
        lock (_gate)
        {
            var changed = 0;
            var now = DateTimeOffset.UtcNow;
            foreach (var id in ids.Distinct())
            {
                if (!_messages.TryGetValue(id, out var message) || !CanReceiveUnsafe(userId, message)) continue;
                var key = (id, userId);
                var old = _receipts.GetValueOrDefault(key);
                var privateRead = read && !GetPrivacy(userId).SendReadReceipts;
                // Turning read receipts back on must not retroactively disclose private reads,
                // including when an older client retries them after restarting.
                if (old is not null && (!read || old.ReadAt is not null || _privateReads.Contains(key))) continue;
                _receipts[key] = new RecipientReceipt(userId, old?.DeliveredAt ?? now,
                    read && !privateRead ? now : old?.ReadAt);
                if (privateRead) _privateReads.Add(key);
                changed++;
            }
            if (changed > 0) PersistUnsafe();
            return changed;
        }
    }

    private MessageDelivery DeliveryUnsafe(StoredMessage message)
    {
        // Use the ORIGINAL envelope recipients, not today's group membership. An added member
        // must not change an old message's status. Bots do not consume image/voice messages.
        var recipients = message.Payloads.Select(p => p.RecipientId).Distinct()
            .Where(id => id != message.SenderId &&
                (message.Kind == "text" || _users.GetValueOrDefault(id)?.IsAgent != true))
            .Select(id =>
            {
                var receipt = _receipts.GetValueOrDefault((message.Id, id)) ?? new RecipientReceipt(id, null, null);
                return GetPrivacy(id).SendReadReceipts ? receipt : receipt with { ReadAt = null };
            }).ToArray();
        var status = recipients.Length > 0 && recipients.All(r => r.ReadAt is not null) ? "read" :
            recipients.Length > 0 && recipients.All(r => r.DeliveredAt is not null) ? "delivered" : "sent";
        return new MessageDelivery(status, recipients);
    }

    private void RemoveReceiptsUnsafe(HashSet<Guid> messageIds)
    {
        foreach (var key in _receipts.Keys.Where(k => messageIds.Contains(k.MessageId)).ToArray()) _receipts.Remove(key);
        _privateReads.RemoveWhere(k => messageIds.Contains(k.MessageId));
    }
}
