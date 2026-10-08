using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using MTKChat.Contracts;
using MTKChat.Server.Services;
using Xunit;

namespace MTKChat.Cryptography.Tests;

public sealed class ConversationActivityTests
{
    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static ChatUser User(ChatState state, string name, string role = "user") =>
        state.UpsertSiteUser(Guid.NewGuid(), name, name + "@example.test", role);

    private static StoredMessage Send(ChatState state, ChatUser sender, ConversationSummary room,
        DateTimeOffset at, params Guid[] recipients) => Send(state, sender, room, at, null, recipients);

    private static StoredMessage Send(ChatState state, ChatUser sender, ConversationSummary room,
        DateTimeOffset at, DateTimeOffset? expiry, params Guid[] recipients)
    {
        Assert.True(state.AddMessage(sender.Id, new(Guid.NewGuid(), room.Id, "text", at, expiry,
            recipients.Select(id => new EncryptedPayload("test", "", "", "opaque", "", "", id)).ToArray(), null), out var stored));
        return stored!;
    }

    private static ConversationSummary Summary(ChatState state, ChatUser user, ConversationSummary room) =>
        state.GetConversations(user.Id).Single(item => item.Id == room.Id);

    private static string Snapshot(ChatState state) => (string)typeof(ChatState)
        .GetMethod("SerializeSnapshotUnsafe", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(state, [])!;

    private static ChatState Restore(string json, Clock clock, params ChatUser[] users)
    {
        var state = new ChatState(timeProvider: clock);
        foreach (var user in users) state.UpsertSiteUser(user.Id, user.DisplayName, user.Email, user.Role);
        typeof(ChatState).GetMethod("RestoreSnapshot", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(state, [json]);
        return state;
    }

    [Fact]
    public void IndividualAndEveryoneDeletionKeepActivityWithoutKeepingVisiblePreview()
    {
        var clock = new Clock(DateTimeOffset.UtcNow); var state = new ChatState(timeProvider: clock);
        var a = User(state, "A"); var b = User(state, "B"); var room = state.GetOrCreateDirect(a.Id, b.Id)!;
        var old = Send(state, a, room, clock.Now.AddMinutes(-2), a.Id, b.Id);
        var newest = Send(state, a, room, clock.Now, a.Id, b.Id);
        Assert.Equal(DeleteResult.Deleted, state.DeleteMessage(b.Id, newest.Id, false));
        var personal = Summary(state, b, room);
        Assert.True(personal.ActivityMetadataAvailable);
        Assert.Equal(old.CreatedAt, personal.LastMessageAt);
        Assert.Equal(newest.CreatedAt, personal.LastActivityAt);
        Assert.Equal(DeleteResult.Deleted, state.DeleteMessage(a.Id, newest.Id, true));
        Assert.Equal(newest.CreatedAt, Summary(state, a, room).LastActivityAt);
        Assert.Equal(newest.CreatedAt, Summary(state, b, room).LastActivityAt);
        Assert.All(state.GetMessages(a.Id, room.Id, null).Where(m => m.DeletedForEveryone), m => Assert.Empty(m.Payloads));
        Assert.Equal(1, state.ClearHistory(b.Id, room.Id, false));
        Assert.Null(Summary(state, b, room).LastMessageAt);
        Assert.Equal(newest.CreatedAt, Summary(state, b, room).LastActivityAt);
    }

    [Fact]
    public void NewestDirectAndGroupActivityOrdersListEvenAfterNewestMessageWasDeleted()
    {
        var clock = new Clock(DateTimeOffset.UtcNow); var state = new ChatState(timeProvider: clock);
        var a = User(state, "A"); var b = User(state, "B"); var c = User(state, "C");
        var direct = state.GetOrCreateDirect(a.Id, b.Id)!;
        var group = state.CreateConversation(a.Id, new("Group", [b.Id, c.Id]))!;
        Send(state, b, direct, clock.Now.AddMinutes(-2), a.Id, b.Id);
        var newest = Send(state, c, group, clock.Now, a.Id, b.Id, c.Id);
        Assert.Equal(DeleteResult.Deleted, state.DeleteMessage(c.Id, newest.Id, true));
        Assert.Equal(new[] { group.Id, direct.Id }, state.GetConversations(a.Id).Select(item => item.Id));
        Assert.Null(Summary(state, a, group).LastMessageAt);
        Assert.Equal(newest.CreatedAt, Summary(state, a, group).LastActivityAt);
    }

    [Fact]
    public void BlockAndUnaddressedEnvelopesDoNotAdvanceOrRevealActivity()
    {
        var clock = new Clock(DateTimeOffset.UtcNow); var state = new ChatState(timeProvider: clock);
        var a = User(state, "A"); var b = User(state, "B"); var c = User(state, "C");
        var room = state.CreateConversation(a.Id, new("Group", [b.Id, c.Id]))!;
        var before = Send(state, a, room, clock.Now.AddMinutes(-2), a.Id, b.Id, c.Id);
        Assert.True(state.SetBlocked(b.Id, a.Id, true));
        Assert.Null(Summary(state, b, room).LastActivityAt);
        Send(state, a, room, clock.Now.AddMinutes(-1), a.Id, b.Id, c.Id);
        Assert.Null(Summary(state, b, room).LastActivityAt);
        Send(state, c, room, clock.Now, a.Id, c.Id); // B is a member, but has no envelope.
        Assert.Null(Summary(state, b, room).LastMessageAt);
        Assert.Null(Summary(state, b, room).LastActivityAt);
        var allowed = Send(state, c, room, clock.Now.AddMinutes(1), a.Id, b.Id, c.Id);
        Assert.Equal(DeleteResult.Deleted, state.DeleteMessage(c.Id, allowed.Id, true));
        Assert.Equal(allowed.CreatedAt, Summary(state, b, room).LastActivityAt);
        Assert.Null(Summary(state, b, room).LastMessageAt);
        Assert.Equal(before.CreatedAt, state.GetMessages(c.Id, room.Id, null).First().CreatedAt);
    }

    [Fact]
    public void RemoveAndLeaveResetActivityWithoutRestoringItOnReopenOrRejoin()
    {
        var clock = new Clock(DateTimeOffset.UtcNow); var state = new ChatState(timeProvider: clock);
        var owner = User(state, "Owner"); var b = User(state, "B"); var c = User(state, "C");
        var room = state.CreateConversation(owner.Id, new("Group", [b.Id, c.Id]))!;
        Send(state, owner, room, clock.Now, owner.Id, b.Id, c.Id);
        Assert.True(state.RemoveConversationForMe(b.Id, room.Id));
        Assert.DoesNotContain(state.GetConversations(b.Id), item => item.Id == room.Id);
        Assert.Null(state.ReopenConversation(b.Id, room.Id)!.LastActivityAt);
        Assert.Equal(LeaveGroupResult.Left, state.LeaveGroup(b.Id, room.Id));
        Assert.Equal(GroupInviteStatus.Success, state.CreateOrRotateGroupInvite(owner.Id, room.Id, out var invite));
        Assert.Equal(GroupInviteStatus.Success, state.JoinGroupInvite(b.Id, new Uri(invite!.InviteUrl).Fragment[1..], out var joined));
        Assert.Null(joined!.LastMessageAt); Assert.Null(joined.LastActivityAt);
        Assert.Empty(state.GetMessages(b.Id, room.Id, null));
        var next = Send(state, c, room, clock.Now.AddMinutes(1), owner.Id, b.Id, c.Id);
        Assert.Equal(next.CreatedAt, Summary(state, b, room).LastActivityAt);
        Assert.Equal(GroupMemberRemovalStatus.Removed, state.RemoveGroupMember(owner.Id, room.Id, b.Id));
        Assert.Equal(GroupInviteStatus.Success, state.JoinGroupInvite(b.Id, new Uri(invite.InviteUrl).Fragment[1..], out joined));
        Assert.Null(joined!.LastActivityAt);
    }

    [Fact]
    public void FreshInviteMemberNeverReceivesExistingGroupActivity()
    {
        var clock = new Clock(DateTimeOffset.UtcNow); var state = new ChatState(timeProvider: clock);
        var owner = User(state, "Owner"); var b = User(state, "B"); var newcomer = User(state, "Newcomer");
        var room = state.CreateConversation(owner.Id, new("Group", [b.Id]))!;
        var newest = Send(state, owner, room, clock.Now, owner.Id, b.Id);
        Assert.Equal(DeleteResult.Deleted, state.DeleteMessage(owner.Id, newest.Id, true));
        Assert.Equal(GroupInviteStatus.Success, state.CreateOrRotateGroupInvite(owner.Id, room.Id, out var invite));
        Assert.Equal(GroupInviteStatus.Success, state.JoinGroupInvite(newcomer.Id, new Uri(invite!.InviteUrl).Fragment[1..], out var joined));
        Assert.True(joined!.ActivityMetadataAvailable);
        Assert.Null(joined.LastMessageAt); Assert.Null(joined.LastActivityAt);
    }

    [Fact]
    public void DeletedActivityPersistsAndLegacySnapshotsNeverInferRecipientAccess()
    {
        var clock = new Clock(DateTimeOffset.UtcNow); var state = new ChatState(timeProvider: clock);
        var a = User(state, "A"); var b = User(state, "B"); var room = state.GetOrCreateDirect(a.Id, b.Id)!;
        var older = Send(state, a, room, clock.Now.AddMinutes(-2), a.Id, b.Id);
        var newest = Send(state, a, room, clock.Now, a.Id, b.Id);
        Assert.Equal(DeleteResult.Deleted, state.DeleteMessage(a.Id, newest.Id, true));
        Assert.Equal(DeleteResult.Deleted, state.DeleteMessage(b.Id, older.Id, false));
        var json = Snapshot(state);
        var restored = Restore(json, clock, a, b);
        Assert.Equal(newest.CreatedAt, Summary(restored, b, room).LastActivityAt);
        Assert.Null(Summary(restored, b, room).LastMessageAt);
        var legacy = JsonNode.Parse(json)!; legacy.AsObject().Remove("ConversationActivities");
        legacy.AsObject().Remove("DeletedMessageAudiences");
        var old = Restore(legacy.ToJsonString(), clock, a, b);
        Assert.True(Summary(old, b, room).ActivityMetadataAvailable);
        Assert.Null(Summary(old, b, room).LastActivityAt);
        // A legacy sender still knows its own tombstone even when the old snapshot
        // lacks recipient evidence; arbitrary recipients must not infer that access.
        Assert.Equal(newest.CreatedAt, Summary(old, a, room).LastActivityAt);
        Assert.Equal(older.CreatedAt, Summary(old, a, room).LastMessageAt);
    }

    [Fact]
    public void ExpiredActivityAndRetryDoNotCreateNewOrResurrectedDates()
    {
        var clock = new Clock(DateTimeOffset.UtcNow); var state = new ChatState(timeProvider: clock);
        var a = User(state, "A"); var b = User(state, "B"); var room = state.GetOrCreateDirect(a.Id, b.Id)!;
        var expiring = Send(state, a, room, clock.Now, clock.Now.AddMinutes(1), a.Id, b.Id);
        Assert.Equal(expiring.CreatedAt, Summary(state, b, room).LastActivityAt);
        clock.Now = expiring.ExpiresAt!.Value;
        Assert.Null(Summary(state, b, room).LastMessageAt);
        Assert.Null(Summary(state, b, room).LastActivityAt);
        var latest = Send(state, a, room, clock.Now, a.Id, b.Id);
        Assert.True(state.AddMessage(a.Id, new(latest.ClientMessageId, room.Id, "text", clock.Now.AddDays(1), null,
            latest.Payloads, null), out var duplicate));
        Assert.Equal(latest.Id, duplicate!.Id);
        Assert.Equal(latest.CreatedAt, Summary(state, b, room).LastActivityAt);
        Send(state, a, room, latest.CreatedAt.AddMinutes(-1), a.Id, b.Id);
        Assert.Equal(latest.CreatedAt, Summary(state, b, room).LastActivityAt);
    }

    [Fact]
    public void ContractDistinguishesLegacyMissingMetadataFromAuthoritativeEmptyMetadata()
    {
        var legacy = JsonSerializer.Deserialize<ConversationSummary>("""
            {"Id":"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa","Title":"Empty","Participants":[],"UnreadCount":0}
            """)!;
        Assert.False(legacy.ActivityMetadataAvailable); Assert.Null(legacy.LastActivityAt);
        var state = new ChatState(); var a = User(state, "A"); var b = User(state, "B");
        var empty = state.GetOrCreateDirect(a.Id, b.Id)!;
        Assert.True(empty.ActivityMetadataAvailable); Assert.Null(empty.LastActivityAt);
        var roundTrip = JsonSerializer.Deserialize<ConversationSummary>(JsonSerializer.Serialize(empty))!;
        Assert.True(roundTrip.ActivityMetadataAvailable); Assert.Null(roundTrip.LastActivityAt);
    }
}
