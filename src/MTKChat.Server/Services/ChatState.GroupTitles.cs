using MTKChat.Contracts;

namespace MTKChat.Server.Services;

public sealed partial class ChatState
{
    public RenameGroupResult RenameGroup(Guid actorId, Guid conversationId, string? title, out GroupTitleResult? result)
    {
        result = null;
        lock (_gate)
        {
            // Check authority inside the same lock as the write; a concurrent role
            // revocation must not leave an already-authorized rename queued behind it.
            if (!_users.TryGetValue(actorId, out var actor) || actor.IsAgent || _bannedUsers.Contains(actorId))
                return RenameGroupResult.Forbidden;
            if (!_conversations.TryGetValue(conversationId, out var room)) return RenameGroupResult.NotFound;
            if (actor.Role != "admin" && !room.MemberIds.Contains(actorId))
                return RenameGroupResult.Forbidden;
            if (room.Kind == "direct") return RenameGroupResult.NotAGroup;
            if (actor.Role != "admin" && GetGroupRole(conversationId, actorId) != "admin") return RenameGroupResult.Forbidden;
            var normalized = title?.Trim();
            if (string.IsNullOrEmpty(normalized) || normalized.Length > 80 || title!.Any(char.IsControl))
                return RenameGroupResult.InvalidTitle;
            if (room.Title != normalized)
            {
                // Record copies retain the original membership and message-ID list.
                // Renaming metadata never regenerates encrypted message envelopes.
                _conversations[conversationId] = room with { Title = normalized };
                try { PersistUnsafe(); }
                catch { _conversations[conversationId] = room; throw; }
            }
            result = new(conversationId, normalized);
            return RenameGroupResult.Renamed;
        }
    }
}

public enum RenameGroupResult { Renamed, Forbidden, NotFound, NotAGroup, InvalidTitle }
