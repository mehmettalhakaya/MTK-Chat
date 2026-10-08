using System.Security.Cryptography;
using System.Text;
using MTKChat.Contracts;

namespace MTKChat.Server.Services;

public sealed partial class ChatState
{
    // The optional tail keeps hash-only snapshots readable. A legacy link keeps its
    // deterministic id and validity, but cannot reveal a token that was never stored.
    private sealed record GroupInviteData(Guid ConversationId, string TokenHash, DateTimeOffset ExpiresAt,
        Guid? Id = null, string? ProtectedToken = null, DateTimeOffset? CreatedAt = null, bool NeverExpires = false);
    private readonly Dictionary<Guid, GroupInviteData> _groupInvites = new();
    private const int MinimumInviteDurationMinutes = 5;
    private const int MaximumInviteDurationMinutes = 525600;
    private const int MaximumGroupInvites = 50;

    public GroupInviteStatus ListGroupInvites(Guid actorId, Guid conversationId, out IReadOnlyList<GroupInviteEntry>? invites)
    {
        lock (_gate)
        {
            invites = null;
            var authorization = AuthorizeInviteManagerUnsafe(actorId, conversationId, out _);
            if (authorization != GroupInviteStatus.Success) return authorization;
            // Expired links remain manageable: their duration can be changed or the entry
            // explicitly deleted. Reading the list never replaces or revives a secret.
            invites = _groupInvites.Values.Where(item => item.ConversationId == conversationId)
                .OrderByDescending(item => item.CreatedAt).ThenBy(item => item.Id)
                .Select(ToGroupInviteEntryUnsafe).ToArray();
            return GroupInviteStatus.Success;
        }
    }

    public GroupInviteStatus CreateGroupInvite(Guid actorId, Guid conversationId, int durationMinutes,
        out GroupInviteEntry? invite, bool neverExpires = false)
    {
        lock (_gate)
        {
            invite = null;
            var authorization = AuthorizeInviteManagerUnsafe(actorId, conversationId, out _);
            if (authorization != GroupInviteStatus.Success) return authorization;
            if (!ValidInviteDuration(durationMinutes, neverExpires)) return GroupInviteStatus.InvalidDuration;
            if (GroupInviteCountUnsafe(conversationId) >= MaximumGroupInvites) return GroupInviteStatus.InviteLimitReached;
            Guid id;
            do { id = Guid.NewGuid(); } while (_groupInvites.ContainsKey(id) || _conversations.ContainsKey(id));
            var token = NewInviteToken();
            // Encryption succeeds before touching mutable state. An unavailable key must
            // not publish an unrecoverable link or invalidate an existing one.
            var data = NewGroupInviteDataUnsafe(conversationId, id, token, durationMinutes, neverExpires);
            _groupInvites.Add(id, data);
            try { PersistUnsafe(); }
            catch { _groupInvites.Remove(id); throw; }
            invite = new(id, conversationId, InviteUrl(token), data.CreatedAt, data.ExpiresAt, data.NeverExpires);
            return GroupInviteStatus.Success;
        }
    }

    public GroupInviteStatus ChangeGroupInviteDuration(Guid actorId, Guid conversationId, Guid inviteId,
        int durationMinutes, out GroupInviteEntry? invite, bool neverExpires = false)
    {
        lock (_gate)
        {
            invite = null;
            var authorization = AuthorizeInviteManagerUnsafe(actorId, conversationId, out _);
            if (authorization != GroupInviteStatus.Success) return authorization;
            if (!ValidInviteDuration(durationMinutes, neverExpires)) return GroupInviteStatus.InvalidDuration;
            if (!_groupInvites.TryGetValue(inviteId, out var old) || old.ConversationId != conversationId)
                return GroupInviteStatus.InviteNotFound;
            // Duration is relative to this explicit action, not to the original creation
            // date. Keep the token and creation timestamp so copied links stay identical.
            var data = old with
            {
                ExpiresAt = InviteExpiry(_time.GetUtcNow(), durationMinutes, neverExpires),
                NeverExpires = neverExpires
            };
            _groupInvites[inviteId] = data;
            try { PersistUnsafe(); }
            catch { _groupInvites[inviteId] = old; throw; }
            invite = ToGroupInviteEntryUnsafe(data);
            return GroupInviteStatus.Success;
        }
    }

    public GroupInviteStatus DeleteGroupInvite(Guid actorId, Guid conversationId, Guid inviteId)
    {
        lock (_gate)
        {
            var authorization = AuthorizeInviteManagerUnsafe(actorId, conversationId, out _);
            if (authorization != GroupInviteStatus.Success) return authorization;
            if (!_groupInvites.TryGetValue(inviteId, out var old) || old.ConversationId != conversationId)
                return GroupInviteStatus.InviteNotFound;
            _groupInvites.Remove(inviteId);
            try { PersistUnsafe(); }
            catch { _groupInvites[inviteId] = old; throw; }
            return GroupInviteStatus.Success;
        }
    }

    public GroupInviteStatus CreateOrRotateGroupInvite(Guid actorId, Guid conversationId, out GroupInviteResult? invite)
    {
        lock (_gate)
        {
            invite = null;
            var authorization = AuthorizeInviteManagerUnsafe(actorId, conversationId, out var room);
            if (authorization != GroupInviteStatus.Success) return authorization;
            // Compatibility endpoint rotates only its historical slot; separately created
            // managed links must not be silently invalidated by an older client.
            var old = _groupInvites.GetValueOrDefault(conversationId);
            if (old is not null && old.ConversationId != conversationId) return GroupInviteStatus.InviteNotFound;
            if (old is null && GroupInviteCountUnsafe(conversationId) >= MaximumGroupInvites)
                return GroupInviteStatus.InviteLimitReached;
            var token = NewInviteToken();
            var data = NewGroupInviteDataUnsafe(conversationId, conversationId, token, 10080);
            _groupInvites[conversationId] = data;
            try { PersistUnsafe(); }
            catch
            {
                if (old is null) _groupInvites.Remove(conversationId); else _groupInvites[conversationId] = old;
                throw;
            }
            // A fixed HTTPS origin prevents a forged Host header from generating phishing
            // links. A fragment prevents unchanged proxy access logs from recording tokens.
            invite = new(conversationId, room!.Title, InviteUrl(token), data.ExpiresAt, data.NeverExpires);
            return GroupInviteStatus.Success;
        }
    }

    public GroupInviteStatus RevokeGroupInvite(Guid actorId, Guid conversationId)
    {
        lock (_gate)
        {
            var authorization = AuthorizeInviteManagerUnsafe(actorId, conversationId, out _);
            if (authorization != GroupInviteStatus.Success) return authorization;
            // This legacy action was already an explicit "revoke group invite" endpoint.
            // Retain its bulk meaning while the new DELETE-id action targets one entry.
            var old = _groupInvites.Where(item => item.Value.ConversationId == conversationId).ToArray();
            if (old.Length == 0) return GroupInviteStatus.Success;
            foreach (var entry in old) _groupInvites.Remove(entry.Key);
            try { PersistUnsafe(); }
            catch { foreach (var entry in old) _groupInvites[entry.Key] = entry.Value; throw; }
            return GroupInviteStatus.Success;
        }
    }

    public GroupInviteStatus PreviewGroupInvite(Guid userId, string token, out GroupInvitePreview? preview)
    {
        lock (_gate)
        {
            preview = null;
            var status = ResolveGroupInviteUnsafe(userId, token, out var data, out var room);
            if (status != GroupInviteStatus.Success) return status;
            preview = new(room!.Id, room.Title, data!.ExpiresAt, room.MemberIds.Contains(userId), data.NeverExpires);
            return GroupInviteStatus.Success;
        }
    }

    public GroupInviteStatus JoinGroupInvite(Guid userId, string token, out ConversationSummary? conversation)
    {
        lock (_gate)
        {
            conversation = null;
            var status = ResolveGroupInviteUnsafe(userId, token, out _, out var room);
            if (status != GroupInviteStatus.Success) return status;
            var wasMember = room!.MemberIds.Contains(userId);
            if (!wasMember && room.MemberIds.Count >= 50) return GroupInviteStatus.CapacityReached;
            var key = (room.Id, userId);
            var wasLeft = _leftGroups.Remove(key);
            var wasHidden = _hiddenConversations.Remove(key);
            var oldRole = _groupRoles.GetValueOrDefault(key);
            var newHidden = Array.Empty<Guid>();
            if (!wasMember)
            {
                room.MemberIds.Add(userId);
                _groupRoles.Remove(key); // Rejoining is not a way to recover an old privileged role.
                // Membership cannot grant historical decryption. Hide existing metadata as
                // well, without changing any stored ciphertext or generating new envelopes.
                newHidden = room.MessageIds.Where(id => _hiddenMessages.Add((userId, id))).ToArray();
            }
            try { if (!wasMember || wasLeft || wasHidden) PersistUnsafe(); }
            catch
            {
                if (!wasMember) room.MemberIds.Remove(userId);
                if (wasLeft) _leftGroups.Add(key);
                if (wasHidden) _hiddenConversations.Add(key);
                if (oldRole is not null) _groupRoles[key] = oldRole;
                foreach (var id in newHidden) _hiddenMessages.Remove((userId, id));
                throw;
            }
            conversation = GetConversations(userId).Single(item => item.Id == room.Id);
            return GroupInviteStatus.Success;
        }
    }

    private GroupInviteStatus AuthorizeInviteManagerUnsafe(Guid actorId, Guid conversationId, out ConversationState? room)
    {
        room = null;
        if (!_users.TryGetValue(actorId, out var actor) || actor.IsAgent || _bannedUsers.Contains(actorId) ||
            _chatBannedUsers.Contains((conversationId, actorId))) return GroupInviteStatus.Forbidden;
        if (!_conversations.TryGetValue(conversationId, out room)) return GroupInviteStatus.NotFound;
        if (room.Kind == "direct") return GroupInviteStatus.NotAGroup;
        // Site administrators manage metadata even outside a private group. This does not
        // grant membership, device keys, photos or access to that group's message payloads.
        return actor.Role == "admin" || room.MemberIds.Contains(actorId) && GetGroupRole(room.Id, actorId) == "admin"
            ? GroupInviteStatus.Success : GroupInviteStatus.Forbidden;
    }

    private GroupInviteStatus ResolveGroupInviteUnsafe(Guid userId, string token, out GroupInviteData? data, out ConversationState? room)
    {
        data = null;
        room = null;
        if (!_users.TryGetValue(userId, out var user) || user.IsAgent || _bannedUsers.Contains(userId))
            return GroupInviteStatus.Forbidden;
        if (!IsInviteToken(token)) return GroupInviteStatus.InvalidOrExpired;
        var hash = HashInviteToken(token);
        data = _groupInvites.Values.FirstOrDefault(item => item.TokenHash == hash);
        // An unlimited link is truly indefinite: do not compare even its compatibility
        // date to the clock. Deletion, bans, real account and room checks still apply.
        if (data is null || (!data.NeverExpires && data.ExpiresAt <= _time.GetUtcNow()) ||
            !_conversations.TryGetValue(data.ConversationId, out room) || room.Kind == "direct")
            return GroupInviteStatus.InvalidOrExpired;
        if (_chatBannedUsers.Contains((room.Id, userId))) return GroupInviteStatus.Forbidden;
        return GroupInviteStatus.Success;
    }

    private static bool IsInviteToken(string? token) => token is { Length: 43 } &&
        token.All(character => character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_');

    private static bool IsInviteHash(string? hash) => hash is { Length: 64 } &&
        hash.All(character => character is >= '0' and <= '9' or >= 'A' and <= 'F');

    private static string HashInviteToken(string token) => Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(token)));

    private int GroupInviteCountUnsafe(Guid conversationId) => _groupInvites.Values.Count(item => item.ConversationId == conversationId);
    private static bool ValidInviteDuration(int durationMinutes, bool neverExpires = false) =>
        durationMinutes is >= MinimumInviteDurationMinutes and <= MaximumInviteDurationMinutes ||
        (neverExpires && durationMinutes == 0);
    private static DateTimeOffset InviteExpiry(DateTimeOffset now, int durationMinutes, bool neverExpires) =>
        neverExpires ? DateTimeOffset.MaxValue : now.AddMinutes(durationMinutes);
    private static string NewInviteToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static string InviteUrl(string token) => "https://mtkaya.me/chat/invite/#" + token;

    private GroupInviteData NewGroupInviteDataUnsafe(Guid conversationId, Guid id, string token, int durationMinutes,
        bool neverExpires = false)
    {
        var now = _time.GetUtcNow();
        var protectedToken = _inviteProtector.Protect(conversationId, id, token);
        if (string.IsNullOrWhiteSpace(protectedToken)) throw new CryptographicException("Davet bağlantısı güvenli biçimde saklanamadı.");
        return new(conversationId, HashInviteToken(token), InviteExpiry(now, durationMinutes, neverExpires),
            id, protectedToken, now, neverExpires);
    }

    private GroupInviteEntry ToGroupInviteEntryUnsafe(GroupInviteData data)
    {
        var id = data.Id ?? data.ConversationId;
        string? token = null;
        if (!string.IsNullOrWhiteSpace(data.ProtectedToken))
        {
            try { token = _inviteProtector.TryUnprotect(data.ConversationId, id, data.ProtectedToken); }
            catch (CryptographicException) { }
            catch (InvalidOperationException) { }
            catch (FormatException) { }
        }
        // A ciphertext copied between groups/entries, corrupted ciphertext or a missing
        // protector key must not return an arbitrary URL; hash-only entries remain listed.
        var url = IsInviteToken(token) && HashInviteToken(token!) == data.TokenHash ? InviteUrl(token!) : null;
        return new(id, data.ConversationId, url, data.CreatedAt, data.ExpiresAt, data.NeverExpires);
    }
}

public enum GroupInviteStatus
{
    Success, NotFound, Forbidden, NotAGroup, InvalidOrExpired, CapacityReached,
    InvalidDuration, InviteNotFound, InviteLimitReached
}
