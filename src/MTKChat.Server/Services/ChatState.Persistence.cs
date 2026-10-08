using System.Text.Json;
using MTKChat.Contracts;

namespace MTKChat.Server.Services;

public sealed partial class ChatState
{
    private sealed record ConversationData(Guid Id, string Title, Guid[] Members, Guid[] Messages, string Kind = "group");
    private sealed record UserMessage(Guid UserId, Guid MessageId);
    private sealed record UserDeadline(Guid UserId, DateTimeOffset? Until);
    private sealed record ChatUserDeadline(Guid ConversationId, Guid UserId, DateTimeOffset? Until);
    private sealed record ChatUserKey(Guid ConversationId, Guid UserId);
    private sealed record BlockData(Guid OwnerId, Guid[] TargetIds);
    private sealed record ReceiptData(Guid MessageId, RecipientReceipt Receipt);
    private sealed record PrivacyData(Guid UserId, PrivacySettings Settings);
    private sealed record GroupRoleData(Guid ConversationId, Guid UserId, string Role);
    private sealed record LastSeenData(Guid UserId, DateTimeOffset SeenAt);
    private sealed record Snapshot(
        int Version,
        ConversationData[] Conversations,
        StoredMessage[] Messages,
        DeviceKeyBundle[] Devices,
        UserMessage[] HiddenMessages,
        Guid[] BannedUsers,
        UserDeadline[] MutedUsers,
        ChatUserDeadline[] ChatMutedUsers,
        ChatUserKey[] ChatBannedUsers,
        BlockData[] Blocks,
        ReceiptData[]? Receipts = null,
        PrivacyData[]? Privacy = null,
        UserMessage[]? PrivateReads = null,
        GroupRoleData[]? GroupRoles = null,
        LastSeenData[]? LastSeen = null,
        ChatUserKey[]? HiddenConversations = null,
        ChatUserKey[]? LeftGroups = null,
        GroupInviteData[]? GroupInvites = null,
        ProfileNameData[]? ProfileNames = null,
        StatusData[]? Statuses = null,
        PinnedMessageView[]? Pins = null);

    private void PersistUnsafe()
    {
        if (_database is null) return;
        _database.SaveSnapshot(SerializeSnapshotUnsafe());
    }

    private string SerializeSnapshotUnsafe()
    {
        var snapshot = new Snapshot(1,
            _conversations.Values.Select(item => new ConversationData(item.Id, item.Title,
                item.MemberIds.ToArray(), item.MessageIds.ToArray(), item.Kind)).ToArray(),
            _messages.Values.ToArray(),
            _devicesByUser.Values.ToArray(),
            _hiddenMessages.Select(item => new UserMessage(item.UserId, item.MessageId)).ToArray(),
            _bannedUsers.ToArray(),
            _mutedUsers.Select(item => new UserDeadline(item.Key, item.Value)).ToArray(),
            _chatMutedUsers.Select(item => new ChatUserDeadline(item.Key.ConversationId, item.Key.UserId, item.Value)).ToArray(),
            _chatBannedUsers.Select(item => new ChatUserKey(item.ConversationId, item.UserId)).ToArray(),
            _blockedUsers.Select(item => new BlockData(item.Key, item.Value.ToArray())).ToArray(),
            _receipts.Select(item => new ReceiptData(item.Key.MessageId, item.Value)).ToArray(),
            _privacy.Select(item => new PrivacyData(item.Key, item.Value)).ToArray(),
            _privateReads.Select(item => new UserMessage(item.UserId, item.MessageId)).ToArray(),
            _groupRoles.Select(item => new GroupRoleData(item.Key.ConversationId, item.Key.UserId, item.Value)).ToArray(),
            _lastSeen.Select(item => new LastSeenData(item.Key, item.Value)).ToArray(),
            _hiddenConversations.Select(item => new ChatUserKey(item.ConversationId, item.UserId)).ToArray(),
            _leftGroups.Select(item => new ChatUserKey(item.ConversationId, item.UserId)).ToArray(),
            _groupInvites.Values.ToArray(),
            _profileNames.Values.ToArray(),
            _statuses.Values.ToArray(),
            _pins.Values.ToArray());
        return JsonSerializer.Serialize(snapshot);
    }

    private void RestoreSnapshot(string json)
    {
        var snapshot = JsonSerializer.Deserialize<Snapshot>(json)
            ?? throw new InvalidDataException("Chat veritabanı anlık görüntüsü boş.");
        if (snapshot.Version != 1) throw new InvalidDataException("Bilinmeyen chat veri sürümü.");
        // Older snapshots have no real-name field. Do not infer names from
        // usernames or accept malformed/bot profile data from a snapshot.
        _profileNames.Clear();
        foreach (var row in snapshot.ProfileNames ?? [])
            if (row.UserId != Guid.Empty && row.UserId != Guid.Parse("22222222-2222-2222-2222-222222222222") &&
                row.UserId != Guid.Parse("33333333-3333-3333-3333-333333333333") &&
                TryNormalizeProfileName(row.FirstName, out var first) && TryNormalizeProfileName(row.LastName, out var last))
                _profileNames.TryAdd(row.UserId, new ProfileNameData(row.UserId, first, last));
        foreach (var user in _users.Values.Where(user => !user.IsAgent).ToArray())
        {
            var names = _profileNames.GetValueOrDefault(user.Id);
            _users[user.Id] = user with { FirstName = names?.FirstName, LastName = names?.LastName };
        }
        _conversations.Clear();
        foreach (var row in snapshot.Conversations)
        {
            var conversation = new ConversationState(row.Id, row.Title, row.Members.ToHashSet(), row.Kind);
            conversation.MessageIds.AddRange(row.Messages);
            _conversations[row.Id] = conversation;
        }
        _messages.Clear();
        _clientMessageIds.Clear();
        foreach (var message in snapshot.Messages)
        {
            _messages[message.Id] = message;
            _clientMessageIds[(message.SenderId, message.ClientMessageId)] = message.Id;
        }
        _receipts.Clear();
        foreach (var row in snapshot.Receipts ?? [])
            if (_messages.TryGetValue(row.MessageId, out var message) && !message.DeletedForEveryone &&
                message.SenderId != row.Receipt.UserId && message.Payloads.Any(p => p.RecipientId == row.Receipt.UserId) &&
                row.Receipt.DeliveredAt is not null)
                _receipts[(row.MessageId, row.Receipt.UserId)] = row.Receipt;
        _privacy.Clear();
        _lastSeen.Clear();
        foreach (var row in snapshot.LastSeen ?? []) _lastSeen[row.UserId] = row.SeenAt;
        foreach (var row in snapshot.Privacy ?? []) _privacy[row.UserId] = row.Settings;
        _privateReads.Clear();
        foreach (var row in snapshot.PrivateReads ?? [])
            if (_messages.TryGetValue(row.MessageId, out var message) &&
                message.Payloads.Any(p => p.RecipientId == row.UserId)) _privateReads.Add((row.MessageId, row.UserId));
        _groupRoles.Clear();
        foreach (var row in snapshot.GroupRoles ?? [])
            if (_conversations.TryGetValue(row.ConversationId, out var room) && room.Kind != "direct" &&
                room.MemberIds.Contains(row.UserId) && row.Role is "admin" or "mod")
                _groupRoles[(row.ConversationId, row.UserId)] = row.Role;
        _devicesByUser.Clear();
        foreach (var device in snapshot.Devices) _devicesByUser[device.UserId] = device;
        _hiddenMessages.Clear();
        foreach (var item in snapshot.HiddenMessages) _hiddenMessages.Add((item.UserId, item.MessageId));
        _bannedUsers.Clear();
        foreach (var id in snapshot.BannedUsers) _bannedUsers.Add(id);
        _mutedUsers.Clear();
        foreach (var item in snapshot.MutedUsers) _mutedUsers[item.UserId] = item.Until;
        _chatMutedUsers.Clear();
        foreach (var item in snapshot.ChatMutedUsers) _chatMutedUsers[(item.ConversationId, item.UserId)] = item.Until;
        _chatBannedUsers.Clear();
        foreach (var item in snapshot.ChatBannedUsers) _chatBannedUsers.Add((item.ConversationId, item.UserId));
        _blockedUsers.Clear();
        foreach (var item in snapshot.Blocks) _blockedUsers[item.OwnerId] = item.TargetIds.ToHashSet();
        // Statuses restore only after their sender device and block metadata. Old
        // snapshots omit this optional field and retain all existing chat data.
        RestoreStatusesUnsafe(snapshot.Statuses);
        RestorePinsUnsafe(snapshot.Pins);
        _hiddenConversations.Clear();
        foreach (var item in snapshot.HiddenConversations ?? [])
            if (_conversations.TryGetValue(item.ConversationId, out var room) && room.MemberIds.Contains(item.UserId))
                _hiddenConversations.Add((item.ConversationId, item.UserId));
        _leftGroups.Clear();
        foreach (var item in snapshot.LeftGroups ?? [])
            if (_conversations.TryGetValue(item.ConversationId, out var room) && room.Kind != "direct" && !room.MemberIds.Contains(item.UserId))
                _leftGroups.Add((item.ConversationId, item.UserId));
        // Hash-only legacy entries cannot reconstruct URLs, but keep a deterministic id
        // and remain valid until expiry. Expired entries are retained for explicit manager
        // duration changes/deletion. Neither migration nor reads rotate a bearer secret.
        _groupInvites.Clear();
        foreach (var item in snapshot.GroupInvites ?? [])
            if (_conversations.TryGetValue(item.ConversationId, out var room) && room.Kind != "direct" &&
                IsInviteHash(item.TokenHash) && GroupInviteCountUnsafe(item.ConversationId) < MaximumGroupInvites)
            {
                var id = item.Id is null || item.Id == Guid.Empty ? item.ConversationId : item.Id.Value;
                // Room ids are reserved for that room's historical single-link slot.
                if (id != item.ConversationId && _conversations.ContainsKey(id)) continue;
                // A repeated/cross-room id is malformed snapshot data. Keep the first
                // valid binding; never let a later row overwrite another group's link.
                // Old snapshots omit NeverExpires and stay finite, even if their expiry
                // is unusually far away. Normalize only explicit unlimited rows so all
                // returned DTOs retain the same non-null compatibility date after restart.
                _groupInvites.TryAdd(id, item with
                {
                    Id = id,
                    ExpiresAt = item.NeverExpires ? DateTimeOffset.MaxValue : item.ExpiresAt
                });
            }

        var lobbyId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        if (!_conversations.ContainsKey(lobbyId))
            _conversations[lobbyId] = new ConversationState(lobbyId, "MTK Lounge",
                new HashSet<Guid> { Guid.Parse("22222222-2222-2222-2222-222222222222"),
                    Guid.Parse("33333333-3333-3333-3333-333333333333") });
    }

    public void SynchronizeSiteUsers(IReadOnlyList<SiteAccount> accounts)
    {
        lock (_gate)
        {
            var active = accounts.Where(item => item.IsActive).ToDictionary(item => item.ChatId);
            var changed = false;
            foreach (var account in active.Values)
            {
                var names = _profileNames.GetValueOrDefault(account.ChatId);
                var user = new ChatUser(account.ChatId, account.Username, account.Email, false, "#7C5CFC", account.Role,
                    _profilePhotos.GetValueOrDefault(account.ChatId)?.Version, names?.FirstName, names?.LastName);
                if (!_users.TryGetValue(user.Id, out var old) || old != user)
                {
                    _users[user.Id] = user;
                    changed = true;
                }
                var lobbyId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
                if (user.Role == "admin" && _conversations.TryGetValue(lobbyId, out var lobby) && !_leftGroups.Contains((lobbyId, user.Id)) &&
                    lobby.MemberIds.Add(user.Id)) changed = true;
            }
            foreach (var id in _users.Values.Where(item => !item.IsAgent && !active.ContainsKey(item.Id))
                         .Select(item => item.Id).ToArray())
            {
                _users.Remove(id);
                _profileNames.Remove(id);
                _devicesByUser.Remove(id);
                _bannedUsers.Remove(id);
                _mutedUsers.Remove(id);
                _blockedUsers.Remove(id);
                _privacy.Remove(id);
                _lastSeen.Remove(id);
                _privateReads.RemoveWhere(item => item.UserId == id);
                foreach (var key in _groupRoles.Keys.Where(item => item.UserId == id).ToArray()) _groupRoles.Remove(key);
                foreach (var targets in _blockedUsers.Values) targets.Remove(id);
                _hiddenMessages.RemoveWhere(item => item.UserId == id);
                _hiddenConversations.RemoveWhere(item => item.UserId == id);
                _leftGroups.RemoveWhere(item => item.UserId == id);
                _activeConversation.Remove(id);
                foreach (var key in _chatMutedUsers.Keys.Where(item => item.UserId == id).ToArray()) _chatMutedUsers.Remove(key);
                _chatBannedUsers.RemoveWhere(item => item.UserId == id);
                foreach (var conversation in _conversations.Values) conversation.MemberIds.Remove(id);
                foreach (var session in _sessions.Where(item => item.Value == id).ToArray()) _sessions.TryRemove(session.Key, out _);
                changed = true;
            }
            // Old snapshots may contain members that were never loaded into the site directory.
            foreach (var conversation in _conversations.Values)
                if (conversation.MemberIds.RemoveWhere(id => !_users.ContainsKey(id)) > 0) changed = true;
            foreach (var key in _groupRoles.Keys.Where(item => !_conversations.TryGetValue(item.ConversationId, out var room) ||
                         !room.MemberIds.Contains(item.UserId)).ToArray()) { _groupRoles.Remove(key); changed = true; }
            foreach (var id in _devicesByUser.Keys.Where(id => !_users.ContainsKey(id)).ToArray())
            {
                _devicesByUser.Remove(id);
                changed = true;
            }
            // On startup names restore before site users do. Prune entries for
            // deleted/inactive accounts even if those users were never loaded.
            foreach (var id in _profileNames.Keys.Where(id => !active.ContainsKey(id)).ToArray())
            {
                _profileNames.Remove(id);
                changed = true;
            }
            if (changed) PersistUnsafe();
        }
    }
}
