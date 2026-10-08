using MTKChat.Contracts;

namespace MTKChat.Server.Services;

public sealed partial class ChatState
{
    private readonly Dictionary<Guid, PrivacySettings> _privacy = new();
    private readonly HashSet<(Guid MessageId, Guid UserId)> _privateReads = new();
    private readonly Dictionary<(Guid ConversationId, Guid UserId), string> _groupRoles = new();

    public PrivacySettings GetPrivacy(Guid userId)
    {
        lock (_gate) return _privacy.GetValueOrDefault(userId) ?? new PrivacySettings();
    }

    public bool SetPrivacy(Guid userId, PrivacySettings settings)
    {
        lock (_gate)
        {
            if (!_users.TryGetValue(userId, out var user) || user.IsAgent) return false;
            _privacy[userId] = settings;
            PersistUnsafe();
            return true;
        }
    }

    // Site admins can manage every group without receiving membership or decryptable envelopes.
    public string GetGroupRole(Guid conversationId, Guid userId)
    {
        lock (_gate)
        {
            if (!_conversations.TryGetValue(conversationId, out var room) || room.Kind == "direct" ||
                !_users.TryGetValue(userId, out var user) || user.IsAgent || IsBanned(userId)) return "user";
            if (user.Role == "admin") return "admin";
            if (!room.MemberIds.Contains(userId)) return "user";
            return _groupRoles.GetValueOrDefault((conversationId, userId), "user");
        }
    }

    public bool SetGroupRole(Guid actorId, Guid conversationId, Guid targetId, string role)
    {
        lock (_gate)
        {
            if (role is not ("admin" or "mod" or "user") || actorId == targetId ||
                _chatBannedUsers.Contains((conversationId, actorId)) ||
                !_conversations.TryGetValue(conversationId, out var room) || room.Kind == "direct" ||
                !room.MemberIds.Contains(targetId) || !_users.TryGetValue(targetId, out var target) || target.IsAgent ||
                GetGroupRole(conversationId, actorId) != "admin" || target.Role == "admin") return false;
            var key = (conversationId, targetId);
            if (role == "user") _groupRoles.Remove(key); else _groupRoles[key] = role;
            PersistUnsafe();
            return true;
        }
    }

    public bool CanModerateGroupMember(Guid actorId, Guid conversationId, Guid targetId)
    {
        lock (_gate)
        {
            if (actorId == targetId || _chatBannedUsers.Contains((conversationId, actorId)) ||
                !_conversations.TryGetValue(conversationId, out var room) ||
                room.Kind == "direct" || !room.MemberIds.Contains(targetId) ||
                !_users.TryGetValue(targetId, out var target) || target.IsAgent) return false;
            var actorRole = GetGroupRole(conversationId, actorId);
            var targetRole = GetGroupRole(conversationId, targetId);
            if (IsAdmin(actorId) && !IsBanned(actorId)) return target.Role != "admin";
            return actorRole == "admin" && targetRole != "admin" || actorRole == "mod" && targetRole == "user";
        }
    }

    public IReadOnlyList<GroupManagementView> GetManageableGroups(Guid actorId)
    {
        // A stored/display role is not an override for a restriction in that room.
        // Match invite and member-removal authorization without changing role colors.
        lock (_gate) return _conversations.Values.Where(room => room.Kind != "direct" &&
            !_chatBannedUsers.Contains((room.Id, actorId)) &&
            GetGroupRole(room.Id, actorId) is "admin" or "mod").Select(room =>
                new GroupManagementView(room.Id, room.Title, GetGroupRole(room.Id, actorId),
                    room.MemberIds.Where(_users.ContainsKey).Select(id => new GroupMemberView(_users[id],
                        GetGroupRole(room.Id, id), GetChatModeration(room.Id, id)!)).ToArray(), IsAdmin(actorId))).ToArray();
    }

    public bool ModerateGroupUser(Guid actorId, Guid conversationId, Guid targetId, string action, int? durationMinutes)
    {
        // Authorization and mutation share the lock: a concurrent demotion cannot leave a
        // previously authorized moderator able to perform one more privileged operation.
        lock (_gate) return !string.IsNullOrWhiteSpace(action) &&
            CanModerateGroupMember(actorId, conversationId, targetId) &&
            ModerateChatUser(conversationId, targetId, action, durationMinutes);
    }
}
