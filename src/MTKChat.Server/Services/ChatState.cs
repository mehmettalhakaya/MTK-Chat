using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.AspNetCore.DataProtection;
using MTKChat.Contracts;

namespace MTKChat.Server.Services;

public sealed partial class ChatState
{
    private readonly ChatDatabase? _database;
    private readonly TimeProvider _time;
    private readonly IGroupInviteProtector _inviteProtector;
    private readonly Dictionary<Guid, ProfilePhoto> _profilePhotos = new();
    private readonly Dictionary<Guid, ProfilePhoto> _groupPhotos = new();
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<string, Guid> _sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, ChatUser> _users = new();
    private readonly Dictionary<Guid, DeviceKeyBundle> _devicesByUser = new();
    private readonly Dictionary<Guid, ConversationState> _conversations = new();
    private readonly Dictionary<Guid, StoredMessage> _messages = new();
    private readonly Dictionary<(Guid SenderId, Guid ClientMessageId), Guid> _clientMessageIds = new();
    private readonly HashSet<(Guid UserId, Guid MessageId)> _hiddenMessages = new();
    private readonly HashSet<Guid> _bannedUsers = new();
    private readonly Dictionary<Guid, DateTimeOffset?> _mutedUsers = new();
    private readonly Dictionary<(Guid ConversationId, Guid UserId), DateTimeOffset?> _chatMutedUsers = new();
    private readonly HashSet<(Guid ConversationId, Guid UserId)> _chatBannedUsers = new();
    private readonly Dictionary<Guid, DateTimeOffset> _lastSeen = new();
    private DateTimeOffset _lastSeenPersistedAt = DateTimeOffset.UtcNow;
    private readonly Dictionary<Guid, (Guid ConversationId, DateTimeOffset SeenAt)> _activeConversation = new();
    private readonly Dictionary<Guid, HashSet<Guid>> _blockedUsers = new();
    private readonly Channel<StoredMessage> _newMessages = Channel.CreateUnbounded<StoredMessage>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    public ChannelReader<StoredMessage> NewMessages => _newMessages.Reader;

    public ChatState(ChatDatabase? database = null, TimeProvider? timeProvider = null, IGroupInviteProtector? inviteProtector = null)
    {
        _time = timeProvider ?? TimeProvider.System;
        _database = database?.IsConfigured == true ? database : null;
        // Ephemeral protection is only acceptable for in-memory development/tests.
        // A persistent database must never silently lose recoverable URLs on restart.
        _inviteProtector = inviteProtector ?? (_database is null
            ? new DataProtectionGroupInviteProtector(new EphemeralDataProtectionProvider())
            : throw new InvalidOperationException("Kalıcı davet anahtar sağlayıcısı gerekli."));
        var gemini = new ChatUser(Guid.Parse("22222222-2222-2222-2222-222222222222"), "Gemini Agent", "agent-gemini@local", true, "#2DD4BF", "agent");
        var groq = new ChatUser(Guid.Parse("33333333-3333-3333-3333-333333333333"), "Groq Agent", "agent-groq@local", true, "#FB7185", "agent");
        _users.Add(gemini.Id, gemini);
        _users.Add(groq.Id, groq);

        var lobby = new ConversationState(
            Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            "MTK Lounge",
            new HashSet<Guid> { gemini.Id, groq.Id });
        _conversations.Add(lobby.Id, lobby);

        if (_database is not null)
        {
            _database.EnsureSchema();
            _database.EnsurePhotoSchema();
            _database.EnsureGroupPhotoSchema();
            _database.EnsureEncryptedFileSchema();
            foreach (var photo in _database.LoadPhotos()) _profilePhotos[photo.Key] = photo.Value;
            foreach (var photo in _database.LoadGroupPhotos()) _groupPhotos[photo.Key] = photo.Value;
            var snapshot = _database.LoadSnapshot();
            if (!string.IsNullOrWhiteSpace(snapshot)) RestoreSnapshot(snapshot);
            SynchronizeSiteUsers(_database.ReadSiteAccounts());
            _database.DeleteUnreferencedEncryptedFiles(_messages.Values
                .Where(m => m.Attachment is not null).Select(m => m.Attachment!.StorageToken).ToHashSet());
        }
    }

    public ChatUser? GetUser(Guid id)
    {
        lock (_gate) return _users.GetValueOrDefault(id);
    }

    public ChatUser UpsertSiteUser(Guid id, string displayName, string email, string role)
    {
        lock (_gate)
        {
            var names = _profileNames.GetValueOrDefault(id);
            var user = new ChatUser(id, displayName, email, false, "#7C5CFC", role,
                _profilePhotos.GetValueOrDefault(id)?.Version, names?.FirstName, names?.LastName);
            _users[id] = user;
            var lobbyId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
            // A site account is an application identity, not a Lounge invitation. Preserve
            // historical memberships, but only site administrators receive default access.
            if (user.Role == "admin" && _conversations.TryGetValue(lobbyId, out var lobby) &&
                !_leftGroups.Contains((lobbyId, id))) lobby.MemberIds.Add(id);
            PersistUnsafe();
            return user;
        }
    }

    public IReadOnlyList<ChatUser> GetUsers(Guid requesterId)
    {
        lock (_gate) return _users.Values.Where(user => user.Id != requesterId).ToArray();
    }

    public ChatUser? SetProfilePhoto(Guid userId, ProfilePhoto? photo)
    {
        lock (_gate)
        {
            if (!_users.TryGetValue(userId, out var user) || user.IsAgent) return null;
            // Commit before updating the visible profile so a DB failure cannot claim success.
            _database?.SavePhoto(userId, photo);
            if (photo is null) _profilePhotos.Remove(userId);
            else _profilePhotos[userId] = photo;
            return _users[userId] = user with { PhotoVersion = photo?.Version };
        }
    }

    public ProfilePhoto? GetProfilePhoto(Guid userId)
    {
        lock (_gate) return _users.ContainsKey(userId) ? _profilePhotos.GetValueOrDefault(userId) : null;
    }

    public GroupPhotoResult? SetGroupPhoto(Guid actorId, Guid conversationId, ProfilePhoto? photo)
    {
        lock (_gate)
        {
            if (!_users.TryGetValue(actorId, out var actor) || actor.IsAgent ||
                !_conversations.TryGetValue(conversationId, out var conversation) ||
                !conversation.MemberIds.Contains(actorId)) return null;
            _database?.SaveGroupPhoto(conversationId, photo);
            if (photo is null) _groupPhotos.Remove(conversationId);
            else _groupPhotos[conversationId] = photo;
            return new GroupPhotoResult(conversationId, photo?.Version);
        }
    }

    public ProfilePhoto? GetGroupPhoto(Guid actorId, Guid conversationId)
    {
        lock (_gate) return _conversations.TryGetValue(conversationId, out var conversation) &&
            conversation.MemberIds.Contains(actorId) ? _groupPhotos.GetValueOrDefault(conversationId) : null;
    }

    public string CreateSession(Guid userId)
    {
        var token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        _sessions[token] = userId;
        TouchActivity(userId);
        return token;
    }

    public Guid? ResolveSession(string token) =>
        _sessions.TryGetValue(token, out var userId) && !IsBanned(userId) ? userId : null;

    public void TouchActivity(Guid userId, Guid? conversationId = null)
    {
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            _lastSeen[userId] = now;
            if (conversationId is not null && _conversations.TryGetValue(conversationId.Value, out var conversation) &&
                conversation.MemberIds.Contains(userId))
                _activeConversation[userId] = (conversationId.Value, now);
            if (_database is not null && now - _lastSeenPersistedAt >= TimeSpan.FromSeconds(30))
            {
                PersistUnsafe();
                _lastSeenPersistedAt = now;
            }
        }
    }

    public IReadOnlyList<PresenceView> GetPresence(Guid conversationId, Guid? requesterId = null)
    {
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            var members = _conversations.GetValueOrDefault(conversationId)?.MemberIds;
            return _users.Values
                .Select(user =>
                {
                    var seen = _lastSeen.GetValueOrDefault(user.Id);
                    var viewing = _activeConversation.GetValueOrDefault(user.Id);
                    return new PresenceView(user, !user.IsAgent && seen > now.AddSeconds(-45),
                        members?.Contains(user.Id) == true,
                        !user.IsAgent && viewing.ConversationId == conversationId && viewing.SeenAt > now.AddSeconds(-45),
                        seen == default || requesterId != user.Id && !GetPrivacy(user.Id).ShowLastSeen ? null : seen,
                        GetGroupRole(conversationId, user.Id));
                })
                .OrderByDescending(item => item.IsInConversation)
                .ThenByDescending(item => item.IsOnline)
                .ThenBy(item => item.User.DisplayName)
                .ToArray();
        }
    }

    public bool IsAdmin(Guid userId)
    {
        lock (_gate) return _users.TryGetValue(userId, out var user) && user.Role == "admin";
    }

    public bool IsBanned(Guid userId)
    {
        lock (_gate) return _bannedUsers.Contains(userId);
    }

    public bool IsMuted(Guid userId, out DateTimeOffset? until)
    {
        lock (_gate)
        {
            if (!_mutedUsers.TryGetValue(userId, out until)) return false;
            if (until is null || until > DateTimeOffset.UtcNow) return true;
            _mutedUsers.Remove(userId);
            until = null;
            return false;
        }
    }

    public ChatModerationView? GetChatModeration(Guid conversationId, Guid userId)
    {
        lock (_gate)
        {
            if (!_conversations.TryGetValue(conversationId, out var conversation) || !conversation.MemberIds.Contains(userId))
                return null;
            var key = (conversationId, userId);
            var muted = _chatMutedUsers.TryGetValue(key, out var until);
            if (muted && until is not null && until <= DateTimeOffset.UtcNow)
            {
                _chatMutedUsers.Remove(key);
                muted = false;
                until = null;
            }
            return new ChatModerationView(userId, muted, until, _chatBannedUsers.Contains(key));
        }
    }

    public IReadOnlyList<ChatModerationView> GetChatModerationList(Guid conversationId)
    {
        lock (_gate)
        {
            if (!_conversations.TryGetValue(conversationId, out var conversation)) return Array.Empty<ChatModerationView>();
            return conversation.MemberIds.Select(id => GetChatModeration(conversationId, id)!).ToArray();
        }
    }

    public bool ModerateChatUser(Guid conversationId, Guid userId, string action, int? durationMinutes)
    {
        lock (_gate)
        {
            if (!_conversations.TryGetValue(conversationId, out var conversation) ||
                !conversation.MemberIds.Contains(userId) || !_users.TryGetValue(userId, out var user) || user.IsAgent)
                return false;
            var key = (conversationId, userId);
            switch (action.ToLowerInvariant())
            {
                case "mute":
                    _chatMutedUsers[key] = durationMinutes is > 0 ? DateTimeOffset.UtcNow.AddMinutes(durationMinutes.Value) : null;
                    PersistUnsafe();
                    return true;
                case "unmute":
                    _chatMutedUsers.Remove(key);
                    PersistUnsafe();
                    return true;
                case "ban":
                    _chatBannedUsers.Add(key);
                    PersistUnsafe();
                    return true;
                case "unban":
                    _chatBannedUsers.Remove(key);
                    PersistUnsafe();
                    return true;
                default:
                    return false;
            }
        }
    }

    public bool IsChatWriteRestricted(Guid conversationId, Guid userId, out string reason)
    {
        lock (_gate)
        {
            var key = (conversationId, userId);
            if (_chatBannedUsers.Contains(key))
            {
                reason = "Bu sohbette mesaj gönderme yetkiniz kaldırıldı.";
                return true;
            }
            if (_chatMutedUsers.TryGetValue(key, out var until))
            {
                if (until is null || until > DateTimeOffset.UtcNow)
                {
                    reason = until is null ? "Bu sohbette süresiz susturuldunuz."
                        : $"Bu sohbette {until.Value.ToLocalTime():g} tarihine kadar susturuldunuz.";
                    return true;
                }
                _chatMutedUsers.Remove(key);
            }
            reason = string.Empty;
            return false;
        }
    }

    public IReadOnlyList<AdminUserView> GetAdminUsers()
    {
        lock (_gate)
        {
            return _users.Values
                .Where(user => !user.IsAgent)
                .Select(user => new AdminUserView(user, _bannedUsers.Contains(user.Id), _mutedUsers.GetValueOrDefault(user.Id)))
                .OrderBy(user => user.User.DisplayName)
                .ToArray();
        }
    }

    public bool SetRole(Guid userId, string role)
    {
        lock (_gate)
        {
            if (!_users.TryGetValue(userId, out var user) || user.IsAgent) return false;
            if (_database is not null && !_database.UpdateSiteRole(userId, role)) return false;
            _users[userId] = user with { Role = role };
            return true;
        }
    }

    public bool ModerateUser(Guid userId, string action, int? durationMinutes)
    {
        lock (_gate)
        {
            if (!_users.TryGetValue(userId, out var user) || user.IsAgent) return false;
            switch (action.ToLowerInvariant())
            {
                case "ban":
                    _bannedUsers.Add(userId);
                    foreach (var session in _sessions.Where(item => item.Value == userId).ToArray()) _sessions.TryRemove(session.Key, out _);
                    PersistUnsafe();
                    return true;
                case "unban":
                    _bannedUsers.Remove(userId);
                    PersistUnsafe();
                    return true;
                case "mute":
                    _mutedUsers[userId] = durationMinutes is > 0 ? DateTimeOffset.UtcNow.AddMinutes(durationMinutes.Value) : null;
                    PersistUnsafe();
                    return true;
                case "unmute":
                    _mutedUsers.Remove(userId);
                    PersistUnsafe();
                    return true;
                default:
                    return false;
            }
        }
    }

    public bool SetBlocked(Guid ownerId, Guid targetId, bool blocked)
    {
        lock (_gate)
        {
            if (ownerId == targetId || !_users.ContainsKey(targetId)) return false;
            if (!_blockedUsers.TryGetValue(ownerId, out var targets))
            {
                targets = new HashSet<Guid>();
                _blockedUsers[ownerId] = targets;
            }
            if (blocked) targets.Add(targetId); else targets.Remove(targetId);
            PersistUnsafe();
            return true;
        }
    }

    public IReadOnlySet<Guid> GetBlocked(Guid ownerId)
    {
        lock (_gate) return _blockedUsers.TryGetValue(ownerId, out var targets) ? targets.ToHashSet() : new HashSet<Guid>();
    }

    public bool RegisterDevice(Guid userId, RegisterDeviceRequest request)
    {
        lock (_gate)
        {
            if (_devicesByUser.TryGetValue(userId, out var existing))
                return string.Equals(existing.EncryptionPublicKey, request.EncryptionPublicKey, StringComparison.Ordinal) &&
                       string.Equals(existing.SigningPublicKey, request.SigningPublicKey, StringComparison.Ordinal);
            _devicesByUser[userId] = new DeviceKeyBundle(
                userId,
                Guid.NewGuid(),
                request.EncryptionPublicKey,
                request.SigningPublicKey);
            PersistUnsafe();
            return true;
        }
    }

    public DeviceKeyBundle? GetDevice(Guid userId)
    {
        lock (_gate) return _devicesByUser.GetValueOrDefault(userId);
    }

    public IReadOnlyList<ConversationSummary> GetConversations(Guid userId)
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            return _conversations.Values
                .Where(conversation => conversation.MemberIds.Contains(userId) && !_hiddenConversations.Contains((conversation.Id, userId)))
                .Select(conversation =>
                {
                    var last = conversation.MessageIds
                        .Select(id => _messages.GetValueOrDefault(id))
                        .Where(message => message is not null && !message.DeletedForEveryone &&
                                          (message.ExpiresAt is null || message.ExpiresAt > now) &&
                                          !_hiddenMessages.Contains((userId, message.Id)))
                        .Where(message => !IsBlockedUnsafe(userId, message!.SenderId))
                        .Where(message => message!.SenderId == userId || message.Payloads.Any(payload => payload.RecipientId == userId))
                        .OrderByDescending(message => message!.CreatedAt)
                        .FirstOrDefault();
                    var deletedAt = LastDeletedMessageAtUnsafe(conversation, userId, now);
                    var deletedIsLatest = deletedAt is not null && (last is null || deletedAt >= last.CreatedAt);
                    var visibleActivityAt = deletedIsLatest
                        ? deletedAt : last?.CreatedAt;
                    return new ConversationSummary(
                        conversation.Id,
                        conversation.Title,
                        conversation.MemberIds.Select(id => _users[id]).ToArray(),
                        deletedIsLatest ? "Bu mesaj silindi" : last is null ? "Henüz mesaj yok" : "🔒 Uçtan uca şifreli mesaj",
                        last?.CreatedAt,
                        conversation.MessageIds.Count(id => _messages.TryGetValue(id, out var message) &&
                            CanReceiveUnsafe(userId, message) && _receipts.GetValueOrDefault((id, userId))?.ReadAt is null &&
                            !_privateReads.Contains((id, userId))),
                        _groupPhotos.GetValueOrDefault(conversation.Id)?.Version,
                        conversation.Kind,
                        conversation.Kind == "direct" ? null : conversation.MemberIds.ToDictionary(id => id, id => GetGroupRole(conversation.Id, id)),
                        ConversationActivityAtUnsafe(conversation.Id, userId, visibleActivityAt, now), true,
                        deletedAt, true) with
                    {
                        Title = conversation.Kind == "direct"
                            ? conversation.MemberIds.Where(id => id != userId).Select(id => _users[id].DisplayName).FirstOrDefault() ?? "Özel sohbet"
                            : conversation.Title
                    };
                })
                .OrderByDescending(item => item.LastActivityAt ?? item.LastMessageAt)
                .ThenBy(item => item.Id)
                .ToArray();
        }
    }

    public ConversationSummary? CreateConversation(Guid creatorId, CreateConversationRequest request)
    {
        lock (_gate)
        {
            if (!_users.TryGetValue(creatorId, out var creator) || creator.IsAgent || IsBanned(creatorId) ||
                string.IsNullOrWhiteSpace(request.Title) || request.Title.Trim().Length > 80 || request.Title.Any(char.IsControl) ||
                request.ParticipantIds is null || request.ParticipantIds.Count is < 1 or > 49) return null;
            var memberIds = request.ParticipantIds.Append(creatorId).ToHashSet();
            if (memberIds.Count < 2 || memberIds.Any(id => !_users.ContainsKey(id) || IsBanned(id) ||
                id != creatorId && (IsBlockedUnsafe(creatorId, id) || IsBlockedUnsafe(id, creatorId)))) return null;
            var state = new ConversationState(Guid.NewGuid(), request.Title.Trim(), memberIds);
            _conversations.Add(state.Id, state);
            _groupRoles[(state.Id, creatorId)] = "admin";
            PersistUnsafe();
            return new ConversationSummary(state.Id, state.Title, memberIds.Select(id => _users[id]).ToArray(), "Henüz mesaj yok", null, 0,
                _groupPhotos.GetValueOrDefault(state.Id)?.Version, "group",
                memberIds.ToDictionary(id => id, id => GetGroupRole(state.Id, id)),
                ActivityMetadataAvailable: true, DeletedMessageMetadataAvailable: true);
        }
    }

    public bool IsMember(Guid userId, Guid conversationId)
    {
        lock (_gate) return _conversations.TryGetValue(conversationId, out var conversation) && conversation.MemberIds.Contains(userId);
    }

    public IReadOnlyList<Guid> GetMembers(Guid conversationId)
    {
        lock (_gate) return _conversations.TryGetValue(conversationId, out var conversation) ? conversation.MemberIds.ToArray() : Array.Empty<Guid>();
    }

    public bool AddMessage(Guid senderId, SendMessageRequest request, out StoredMessage? stored)
    {
        StoredMessage? created;
        lock (_gate)
        {
            stored = null;
            if (!_conversations.TryGetValue(request.ConversationId, out var conversation) || !conversation.MemberIds.Contains(senderId)) return false;
            if (IsChatWriteRestricted(request.ConversationId, senderId, out _)) return false;
            if (conversation.MemberIds.Count == 2 && conversation.MemberIds.Any(otherId =>
                    otherId != senderId && (IsBlockedUnsafe(senderId, otherId) || IsBlockedUnsafe(otherId, senderId)))) return false;
            if (request.Payloads.Count == 0 ||
                request.Payloads.Select(payload => payload.RecipientId).Distinct().Count() != request.Payloads.Count ||
                request.Payloads.Any(payload => !conversation.MemberIds.Contains(payload.RecipientId))) return false;
            if (_clientMessageIds.TryGetValue((senderId, request.ClientMessageId), out var existingId))
            {
                stored = _messages.GetValueOrDefault(existingId);
                return stored?.ConversationId == request.ConversationId;
            }
            if (request.Kind == "file" && (request.Attachment is null ||
                request.Attachment.FileName != "" || request.Attachment.ContentType != "application/octet-stream" ||
                request.Attachment.Size is < 1 or > 5_242_880 ||
                request.Attachment.Nonce != "" || request.Attachment.Tag != "" ||
                !OwnsUploadedFileUnsafe(request.Attachment.StorageToken, senderId, request.ConversationId, request.ClientMessageId, request.Attachment.Size)))
                return false;
            if (request.Kind != "file" && request.Attachment is not null) return false;

            created = new StoredMessage(
                Guid.NewGuid(),
                request.ClientMessageId,
                request.ConversationId,
                senderId,
                request.Kind,
                request.CreatedAt,
                request.ExpiresAt,
                false,
                request.Payloads,
                request.Attachment,
                DeleteForEveryoneUntil: _time.GetUtcNow().AddMinutes(15));
            _messages.Add(created.Id, created);
            _clientMessageIds[(senderId, request.ClientMessageId)] = created.Id;
            conversation.MessageIds.Add(created.Id);
            var activityChanges = RecordConversationActivityUnsafe(created);
            // Only a genuinely new incoming envelope restores a personally removed chat.
            // Retries and a sender's own sends cannot restore another user's cleared history.
            var restoredChats = request.Payloads.Where(payload => payload.RecipientId != senderId &&
                !IsBlockedUnsafe(payload.RecipientId, senderId) &&
                (created.ExpiresAt is null || created.ExpiresAt > _time.GetUtcNow()))
                .Select(payload => (request.ConversationId, payload.RecipientId))
                .Where(key => _hiddenConversations.Remove(key)).ToArray();
            try { PersistUnsafe(); }
            catch
            {
                _messages.Remove(created.Id);
                _clientMessageIds.Remove((senderId, request.ClientMessageId));
                conversation.MessageIds.Remove(created.Id);
                foreach (var change in activityChanges)
                    if (change.Previous is null) _conversationActivity.Remove(change.Key);
                    else _conversationActivity[change.Key] = change.Previous;
                foreach (var key in restoredChats) _hiddenConversations.Add(key);
                throw;
            }
            stored = created;
        }
        _newMessages.Writer.TryWrite(created);
        return true;
    }

    public bool IsMessageActive(Guid messageId)
    {
        lock (_gate)
            return _messages.TryGetValue(messageId, out var message) &&
                   !message.DeletedForEveryone &&
                   (message.ExpiresAt is null || message.ExpiresAt > DateTimeOffset.UtcNow);
    }

    public IReadOnlyList<StoredMessage> GetMessages(Guid userId, Guid conversationId, DateTimeOffset? after)
    {
        lock (_gate)
        {
            if (!_conversations.TryGetValue(conversationId, out var conversation) || !conversation.MemberIds.Contains(userId))
                return Array.Empty<StoredMessage>();

            var now = _time.GetUtcNow();
            return conversation.MessageIds
                .Select(id => _messages.GetValueOrDefault(id))
                .Where(message => message is not null)
                .Select(message => message!)
                .Where(message => !_hiddenMessages.Contains((userId, message.Id)))
                .Where(message => !IsBlockedUnsafe(userId, message.SenderId))
                .Where(message => message.ExpiresAt is null || message.ExpiresAt > now)
                .Where(message => !message.DeletedForEveryone || CanSeeDeletionUnsafe(userId, message))
                .Where(message => after is null || message.CreatedAt > after)
                .Select(message => message with {
                    Payloads = message.Payloads.Where(payload => payload.RecipientId == userId).ToArray(),
                    Delivery = message.SenderId == userId && !message.DeletedForEveryone ? DeliveryUnsafe(message) : null })
                .OrderBy(message => message.CreatedAt)
                .ToArray();
        }
    }

    public DeleteResult DeleteMessage(Guid userId, Guid messageId, bool forEveryone)
    {
        lock (_gate)
        {
            if (!_messages.TryGetValue(messageId, out var message)) return DeleteResult.NotFound;
            if (!IsMember(userId, message.ConversationId)) return DeleteResult.Forbidden;
            if (!forEveryone)
            {
                _hiddenMessages.Add((userId, messageId));
                PersistUnsafe();
                return DeleteResult.Deleted;
            }
            if (message.SenderId != userId) return DeleteResult.Forbidden;
            if (message.DeletedForEveryone) return DeleteResult.Deleted;
            // Legacy messages have no trusted server timestamp and deliberately cannot
            // acquire a fresh deletion window when the server upgrades or restarts.
            if (message.DeleteForEveryoneUntil is null || _time.GetUtcNow() >= message.DeleteForEveryoneUntil)
                return DeleteResult.WindowExpired;
            _deletedMessageAudiences[messageId] = message.Payloads.Select(payload => payload.RecipientId)
                .Append(message.SenderId).ToHashSet();
            _messages[messageId] = message with { DeletedForEveryone = true, Payloads = Array.Empty<EncryptedPayload>(), Attachment = null };
            RemoveReceiptsUnsafe([messageId]);
            PersistUnsafe();
            if (message.Attachment is not null) DeleteEncryptedFilesUnsafe([message.Attachment.StorageToken]);
            return DeleteResult.Deleted;
        }
    }

    public int ClearHistory(Guid userId, Guid conversationId, bool forEveryone)
    {
        lock (_gate)
        {
            // There is no everyone-history-clear operation, including for site admins.
            // Keep the flag for older clients, but reject it before any mutation.
            if (forEveryone) return -2;
            if (!_conversations.TryGetValue(conversationId, out var conversation) || !conversation.MemberIds.Contains(userId)) return -1;
            var count = HideConversationMessagesUnsafe(userId, conversation);
            if (count > 0) PersistUnsafe();
            return count;
        }
    }

    public int RemoveExpired()
    {
        lock (_gate)
        {
            var ids = _messages.Values.Where(message => message.ExpiresAt <= _time.GetUtcNow()).Select(message => message.Id).ToArray();
            var fileTokens = ids.Select(id => _messages[id].Attachment?.StorageToken)
                .Where(token => token is not null).Select(token => token!).ToArray();
            foreach (var id in ids)
            {
                if (_messages.Remove(id, out var message)) _clientMessageIds.Remove((message.SenderId, message.ClientMessageId));
                _deletedMessageAudiences.Remove(id);
                foreach (var conversation in _conversations.Values) conversation.MessageIds.Remove(id);
            }
            var removedIds = ids.ToHashSet();
            _hiddenMessages.RemoveWhere(item => removedIds.Contains(item.MessageId));
            RemoveReceiptsUnsafe(removedIds);
            if (ids.Length > 0) PersistUnsafe();
            if (fileTokens.Length > 0) DeleteEncryptedFilesUnsafe(fileTokens);
            return ids.Length;
        }
    }

    private sealed record ConversationState(Guid Id, string Title, HashSet<Guid> MemberIds, string Kind = "group")
    {
        public List<Guid> MessageIds { get; } = new();
    }

    private bool IsBlockedUnsafe(Guid ownerId, Guid senderId) =>
        _blockedUsers.TryGetValue(ownerId, out var blocked) && blocked.Contains(senderId);
}

public enum DeleteResult { Deleted, NotFound, Forbidden, WindowExpired }
