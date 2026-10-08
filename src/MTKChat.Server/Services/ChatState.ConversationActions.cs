using MTKChat.Contracts;

namespace MTKChat.Server.Services;

public sealed partial class ChatState
{
    private readonly HashSet<(Guid ConversationId, Guid UserId)> _hiddenConversations = new();
    private readonly HashSet<(Guid ConversationId, Guid UserId)> _leftGroups = new();

    private int HideConversationMessagesUnsafe(Guid userId, ConversationState room) =>
        room.MessageIds.Count(id => _hiddenMessages.Add((userId, id)));

    public bool RemoveConversationForMe(Guid userId, Guid conversationId)
    {
        lock (_gate)
        {
            if (!_conversations.TryGetValue(conversationId, out var room) || !room.MemberIds.Contains(userId)) return false;
            HideConversationMessagesUnsafe(userId, room);
            _conversationActivity.Remove((conversationId, userId));
            _hiddenConversations.Add((conversationId, userId));
            if (_activeConversation.GetValueOrDefault(userId).ConversationId == conversationId) _activeConversation.Remove(userId);
            PersistUnsafe();
            return true;
        }
    }

    public ConversationSummary? ReopenConversation(Guid userId, Guid conversationId)
    {
        lock (_gate)
        {
            if (!_conversations.TryGetValue(conversationId, out var room) || !room.MemberIds.Contains(userId)) return null;
            if (_hiddenConversations.Remove((conversationId, userId))) PersistUnsafe();
            return GetConversations(userId).Single(c => c.Id == conversationId);
        }
    }

    public LeaveGroupResult LeaveGroup(Guid userId, Guid conversationId)
    {
        lock (_gate)
        {
            if (!_conversations.TryGetValue(conversationId, out var room)) return LeaveGroupResult.NotFound;
            if (!room.MemberIds.Contains(userId) || !_users.TryGetValue(userId, out var user) || user.IsAgent)
                return LeaveGroupResult.Forbidden;
            if (room.Kind == "direct") return LeaveGroupResult.NotAGroup;
            var remainingHumans = room.MemberIds.Where(id => id != userId && _users.TryGetValue(id, out var member) && !member.IsAgent).ToArray();
            // A local group cannot silently lose its only administrator. The existing
            // role-assignment action permits transferring responsibility before leaving.
            if (GetGroupRole(conversationId, userId) == "admin" && remainingHumans.Length > 0 &&
                !remainingHumans.Any(id => GetGroupRole(conversationId, id) == "admin"))
                return LeaveGroupResult.AdminTransferRequired;
            HideConversationMessagesUnsafe(userId, room);
            _conversationActivity.Remove((conversationId, userId));
            room.MemberIds.Remove(userId);
            _groupRoles.Remove((conversationId, userId));
            _leftGroups.Add((conversationId, userId));
            _hiddenConversations.Remove((conversationId, userId));
            // Moderation records are retained to prevent leaving from evading a ban/mute
            // if a later membership-invitation feature admits this account again.
            if (_activeConversation.GetValueOrDefault(userId).ConversationId == conversationId) _activeConversation.Remove(userId);
            PersistUnsafe();
            return LeaveGroupResult.Left;
        }
    }
}

public enum LeaveGroupResult { Left, NotFound, NotAGroup, Forbidden, AdminTransferRequired }
