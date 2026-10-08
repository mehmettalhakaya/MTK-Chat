namespace MTKChat.Server.Services;

public sealed partial class ChatState
{
    public GroupMemberRemovalStatus RemoveGroupMember(Guid actorId, Guid conversationId, Guid targetId)
    {
        lock (_gate)
        {
            // Check the actor first: an unrelated/banned account must not use removal
            // requests to discover whether a private group's users exist.
            if (!_users.TryGetValue(actorId, out var actor) || actor.IsAgent || _bannedUsers.Contains(actorId) ||
                _chatBannedUsers.Contains((conversationId, actorId))) return GroupMemberRemovalStatus.Forbidden;
            if (!_conversations.TryGetValue(conversationId, out var room)) return GroupMemberRemovalStatus.NotFound;
            if (room.Kind == "direct") return GroupMemberRemovalStatus.NotAGroup;
            var siteAdmin = actor.Role == "admin";
            if (!siteAdmin && (!room.MemberIds.Contains(actorId) || GetGroupRole(conversationId, actorId) != "admin"))
                return GroupMemberRemovalStatus.Forbidden;
            if (!room.MemberIds.Contains(targetId) || !_users.TryGetValue(targetId, out var target))
                return GroupMemberRemovalStatus.NotFound;
            if (actorId == targetId || target.IsAgent || target.Role == "admin" ||
                !siteAdmin && _groupRoles.GetValueOrDefault((conversationId, targetId)) == "admin")
                return GroupMemberRemovalStatus.ProtectedTarget;

            var key = (conversationId, targetId);
            var oldActivity = _conversationActivity.GetValueOrDefault(key);
            _conversationActivity.Remove(key);
            var hadRole = _groupRoles.Remove(key, out var oldRole);
            var wasLeft = _leftGroups.Contains(key);
            var wasHidden = _hiddenConversations.Remove(key);
            var oldPresence = _activeConversation.GetValueOrDefault(targetId);
            var clearedPresence = oldPresence.ConversationId == conversationId && _activeConversation.Remove(targetId);
            // Removing membership never deletes another participant's ciphertext. Hide
            // old message metadata only for the removed user, also across later rejoin.
            var newHidden = room.MessageIds.Where(id => _hiddenMessages.Add((targetId, id))).ToArray();
            room.MemberIds.Remove(targetId);
            _leftGroups.Add(key); // Directory synchronization must not put the user back.
            try { PersistUnsafe(); }
            catch
            {
                room.MemberIds.Add(targetId);
                if (hadRole) _groupRoles[key] = oldRole!;
                if (oldActivity is not null) _conversationActivity[key] = oldActivity;
                if (!wasLeft) _leftGroups.Remove(key);
                if (wasHidden) _hiddenConversations.Add(key);
                if (clearedPresence) _activeConversation[targetId] = oldPresence;
                foreach (var id in newHidden) _hiddenMessages.Remove((targetId, id));
                throw;
            }
            // Account, device identity, blocks, bans and mutes deliberately remain:
            // removal is not an account ban, nor a way to bypass existing restrictions.
            return GroupMemberRemovalStatus.Removed;
        }
    }
}

public enum GroupMemberRemovalStatus { Removed, NotFound, Forbidden, NotAGroup, ProtectedTarget }
