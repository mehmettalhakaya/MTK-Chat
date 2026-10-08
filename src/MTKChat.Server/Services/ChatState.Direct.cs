using MTKChat.Contracts;

namespace MTKChat.Server.Services;

public sealed partial class ChatState
{
    public ConversationSummary? GetOrCreateDirect(Guid requester, Guid target)
    {
        lock (_gate)
        {
            if (requester == target || !_users.TryGetValue(requester, out var from) || from.IsAgent ||
                !_users.TryGetValue(target, out var to) || to.IsAgent || IsBanned(requester) || IsBanned(target) ||
                IsBlockedUnsafe(requester, target) || IsBlockedUnsafe(target, requester)) return null;
            // Atomic pair lookup avoids duplicate rooms when both users initiate simultaneously.
            var room = _conversations.Values.FirstOrDefault(c => c.Kind == "direct" && c.MemberIds.Count == 2 &&
                c.MemberIds.Contains(requester) && c.MemberIds.Contains(target));
            if (room is null)
            {
                room = new ConversationState(Guid.NewGuid(), "Özel sohbet", [requester, target], "direct");
                _conversations.Add(room.Id, room);
                PersistUnsafe();
            }
            else if (_hiddenConversations.Remove((room.Id, requester))) PersistUnsafe();
            return GetConversations(requester).Single(c => c.Id == room.Id);
        }
    }
}
