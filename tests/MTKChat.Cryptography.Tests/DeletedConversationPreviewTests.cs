using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using MTKChat.Contracts;
using MTKChat.Server.Services;
using Xunit;

namespace MTKChat.Cryptography.Tests;

public sealed class DeletedConversationPreviewTests
{
    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Fixture
    {
        public Clock Clock { get; } = new(DateTimeOffset.UtcNow);
        public ChatState State { get; }
        public ChatUser Owner { get; }
        public ChatUser Member { get; }
        public ChatUser Other { get; }
        public ConversationSummary Room { get; }
        public Fixture(bool group = false)
        {
            State = new(timeProvider: Clock);
            Owner = User(State, "Owner"); Member = User(State, "Member"); Other = User(State, "Other");
            Room = group ? State.CreateConversation(Owner.Id, new("Group", [Member.Id, Other.Id]))!
                : State.GetOrCreateDirect(Owner.Id, Member.Id)!;
        }
        public StoredMessage Send(DateTimeOffset at, Guid[]? audience = null, DateTimeOffset? expires = null)
        {
            audience ??= Room.Participants.Select(user => user.Id).ToArray();
            Assert.True(State.AddMessage(Owner.Id, new(Guid.NewGuid(), Room.Id, "text", at, expires,
                audience.Select(id => new EncryptedPayload("test", "", "", "secret-ciphertext", "", "", id)).ToArray(), null), out var message));
            return message!;
        }
        public ConversationSummary Summary(ChatUser? user = null) =>
            State.GetConversations((user ?? Member).Id).Single(room => room.Id == Room.Id);
        public void Delete(StoredMessage message) =>
            Assert.Equal(DeleteResult.Deleted, State.DeleteMessage(Owner.Id, message.Id, true));
    }

    private static ChatUser User(ChatState state, string name) =>
        state.UpsertSiteUser(Guid.NewGuid(), name, name + "@example.test", "user");

    private static string Snapshot(ChatState state) => (string)typeof(ChatState)
        .GetMethod("SerializeSnapshotUnsafe", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(state, [])!;

    private static ChatState Restore(string json, Fixture fixture)
    {
        var state = new ChatState(timeProvider: fixture.Clock);
        foreach (var user in new[] { fixture.Owner, fixture.Member, fixture.Other })
            state.UpsertSiteUser(user.Id, user.DisplayName, user.Email, user.Role);
        typeof(ChatState).GetMethod("RestoreSnapshot", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(state, [json]);
        return state;
    }

    [Fact]
    public void LatestEveryoneDeletedMessageHasPlaceholderAndOriginalActivityTime()
    {
        var f = new Fixture(); var old = f.Send(f.Clock.Now.AddMinutes(-1)); var latest = f.Send(f.Clock.Now);
        f.Delete(latest);
        foreach (var user in new[] { f.Owner, f.Member })
        {
            var summary = f.Summary(user);
            Assert.True(summary.DeletedMessageMetadataAvailable);
            Assert.Equal(latest.CreatedAt, summary.LastDeletedMessageAt);
            Assert.Equal(latest.CreatedAt, summary.LastActivityAt);
            Assert.Equal(old.CreatedAt, summary.LastMessageAt); // Decryption-cache timestamp stays independent.
            Assert.Equal("Bu mesaj silindi", summary.LastMessagePreview);
        }
        var tombstone = f.State.GetMessages(f.Member.Id, f.Room.Id, null).Single(message => message.Id == latest.Id);
        Assert.True(tombstone.DeletedForEveryone); Assert.Empty(tombstone.Payloads); Assert.Null(tombstone.Attachment);
    }

    [Fact]
    public void OlderDeletionCannotReplaceNewerVisibleMessage()
    {
        var f = new Fixture(); var deleted = f.Send(f.Clock.Now.AddMinutes(-1)); var latest = f.Send(f.Clock.Now);
        f.Delete(deleted);
        Assert.Equal(deleted.CreatedAt, f.Summary().LastDeletedMessageAt);
        Assert.Equal(latest.CreatedAt, f.Summary().LastMessageAt);
        Assert.Equal(latest.CreatedAt, f.Summary().LastActivityAt);
        Assert.NotEqual("Bu mesaj silindi", f.Summary().LastMessagePreview);
    }

    [Fact]
    public void PersonalDeletionAndClearHideTombstoneWithoutChangingOtherParticipants()
    {
        var f = new Fixture(); var first = f.Send(f.Clock.Now.AddMinutes(-1)); var latest = f.Send(f.Clock.Now); f.Delete(latest);
        Assert.Equal(DeleteResult.Deleted, f.State.DeleteMessage(f.Member.Id, latest.Id, false));
        Assert.Null(f.Summary().LastDeletedMessageAt); Assert.Equal(first.CreatedAt, f.Summary().LastMessageAt);
        Assert.Equal(latest.CreatedAt, f.Summary(f.Owner).LastDeletedMessageAt);
        Assert.Equal(2, f.State.ClearHistory(f.Owner.Id, f.Room.Id, false));
        Assert.Null(f.Summary(f.Owner).LastDeletedMessageAt); Assert.Null(f.Summary(f.Owner).LastMessageAt);
        Assert.Equal(latest.CreatedAt, f.Summary(f.Owner).LastActivityAt);
    }

    [Fact]
    public void OlderAuthorizedTombstoneBecomesPreviewWhenNewerTextIsHiddenPersonally()
    {
        var f = new Fixture(); var deleted = f.Send(f.Clock.Now.AddMinutes(-1)); var latest = f.Send(f.Clock.Now); f.Delete(deleted);
        Assert.Equal(DeleteResult.Deleted, f.State.DeleteMessage(f.Member.Id, latest.Id, false));
        Assert.Equal("Bu mesaj silindi", f.Summary().LastMessagePreview);
        Assert.Equal(deleted.CreatedAt, f.Summary().LastDeletedMessageAt);
        Assert.Null(f.Summary().LastMessageAt);
        // List activity remains monotonic; the displayed marker still has its own time.
        Assert.Equal(latest.CreatedAt, f.Summary().LastActivityAt);
    }

    [Fact]
    public void GroupMemberWithoutAnOriginalEnvelopeNeverSeesDeletionMetadata()
    {
        var f = new Fixture(group: true); var message = f.Send(f.Clock.Now, [f.Owner.Id, f.Member.Id]); f.Delete(message);
        f.Delete(message); // An idempotent retry must not replace the stored audience with erased payloads.
        Assert.Equal(message.CreatedAt, f.Summary().LastDeletedMessageAt);
        Assert.Null(f.Summary(f.Other).LastDeletedMessageAt); Assert.Null(f.Summary(f.Other).LastActivityAt);
        Assert.Empty(f.State.GetMessages(f.Other.Id, f.Room.Id, null));
        Assert.Empty(f.State.GetMessages(Guid.NewGuid(), f.Room.Id, null));
    }

    [Fact]
    public void JoinAfterSendCannotRevealDeletionWhetherDeletionHappensBeforeOrAfterJoin()
    {
        var f = new Fixture(group: true); var before = f.Send(f.Clock.Now.AddMinutes(-1)); f.Delete(before);
        var after = f.Send(f.Clock.Now);
        var newcomer = User(f.State, "Newcomer");
        Assert.Equal(GroupInviteStatus.Success, f.State.CreateOrRotateGroupInvite(f.Owner.Id, f.Room.Id, out var invite));
        Assert.Equal(GroupInviteStatus.Success, f.State.JoinGroupInvite(newcomer.Id, new Uri(invite!.InviteUrl).Fragment[1..], out _));
        f.Delete(after);
        Assert.Null(f.Summary(newcomer).LastDeletedMessageAt); Assert.Null(f.Summary(newcomer).LastActivityAt);
        Assert.Empty(f.State.GetMessages(newcomer.Id, f.Room.Id, null));
    }

    [Fact]
    public void RemovedConversationAndRemovedRejoinedGroupKeepOldTombstonesHidden()
    {
        var f = new Fixture(group: true); var message = f.Send(f.Clock.Now); f.Delete(message);
        Assert.True(f.State.RemoveConversationForMe(f.Member.Id, f.Room.Id));
        Assert.Null(f.State.ReopenConversation(f.Member.Id, f.Room.Id)!.LastDeletedMessageAt);
        Assert.Equal(GroupMemberRemovalStatus.Removed, f.State.RemoveGroupMember(f.Owner.Id, f.Room.Id, f.Member.Id));
        Assert.Equal(GroupInviteStatus.Success, f.State.CreateOrRotateGroupInvite(f.Owner.Id, f.Room.Id, out var invite));
        Assert.Equal(GroupInviteStatus.Success, f.State.JoinGroupInvite(f.Member.Id, new Uri(invite!.InviteUrl).Fragment[1..], out var joined));
        Assert.Null(joined!.LastDeletedMessageAt); Assert.Null(joined.LastActivityAt);
        Assert.Empty(f.State.GetMessages(f.Member.Id, f.Room.Id, null));
    }

    [Fact]
    public void BlockedSenderAndExpiredDeletionCannotProducePlaceholder()
    {
        var f = new Fixture(group: true); var message = f.Send(f.Clock.Now, expires: f.Clock.Now.AddMinutes(1)); f.Delete(message);
        Assert.True(f.State.SetBlocked(f.Member.Id, f.Owner.Id, true));
        Assert.Null(f.Summary().LastDeletedMessageAt); Assert.Empty(f.State.GetMessages(f.Member.Id, f.Room.Id, null));
        Assert.True(f.State.SetBlocked(f.Member.Id, f.Owner.Id, false));
        Assert.Equal(message.CreatedAt, f.Summary().LastDeletedMessageAt);
        f.Clock.Now = message.ExpiresAt!.Value;
        Assert.Null(f.Summary().LastDeletedMessageAt); Assert.Null(f.Summary().LastActivityAt);
        Assert.Empty(f.State.GetMessages(f.Member.Id, f.Room.Id, null));
        Assert.Equal(1, f.State.RemoveExpired());
        Assert.Empty(JsonNode.Parse(Snapshot(f.State))!["DeletedMessageAudiences"]!.AsArray());
    }

    [Fact]
    public void OriginalAudiencePersistsWithoutPreservingDeletedCiphertext()
    {
        var f = new Fixture(group: true); var message = f.Send(f.Clock.Now, [f.Owner.Id, f.Member.Id]); f.Delete(message);
        var json = Snapshot(f.State); Assert.DoesNotContain("secret-ciphertext", json);
        var restored = Restore(json, f);
        var summary = restored.GetConversations(f.Member.Id).Single(room => room.Id == f.Room.Id);
        Assert.Equal(message.CreatedAt, summary.LastDeletedMessageAt); Assert.Equal("Bu mesaj silindi", summary.LastMessagePreview);
        Assert.Single(restored.GetMessages(f.Member.Id, f.Room.Id, null));
        Assert.Null(restored.GetConversations(f.Other.Id).Single(room => room.Id == f.Room.Id).LastDeletedMessageAt);
        Assert.Empty(restored.GetMessages(f.Other.Id, f.Room.Id, null));
    }

    [Fact]
    public void LegacySnapshotDoesNotInferTombstoneAudienceFromCurrentMembership()
    {
        var f = new Fixture(group: true); var message = f.Send(f.Clock.Now); f.Delete(message);
        var legacy = JsonNode.Parse(Snapshot(f.State))!;
        legacy.AsObject().Remove("DeletedMessageAudiences"); legacy.AsObject().Remove("ConversationActivities");
        var restored = Restore(legacy.ToJsonString(), f);
        var own = restored.GetConversations(f.Owner.Id).Single(room => room.Id == f.Room.Id);
        Assert.Equal(message.CreatedAt, own.LastDeletedMessageAt); Assert.Equal(message.CreatedAt, own.LastActivityAt);
        foreach (var other in new[] { f.Member, f.Other })
        {
            var summary = restored.GetConversations(other.Id).Single(room => room.Id == f.Room.Id);
            Assert.True(summary.DeletedMessageMetadataAvailable); Assert.Null(summary.LastDeletedMessageAt);
            Assert.Null(summary.LastActivityAt); Assert.Empty(restored.GetMessages(other.Id, f.Room.Id, null));
        }
    }

    [Fact]
    public void PrivateHiddenDeletionStillStaysHiddenAfterSnapshotRestore()
    {
        var f = new Fixture(); var deleted = f.Send(f.Clock.Now); f.Delete(deleted);
        Assert.Equal(DeleteResult.Deleted, f.State.DeleteMessage(f.Member.Id, deleted.Id, false));
        var restored = Restore(Snapshot(f.State), f);
        Assert.Null(restored.GetConversations(f.Member.Id).Single(room => room.Id == f.Room.Id).LastDeletedMessageAt);
        Assert.Empty(restored.GetMessages(f.Member.Id, f.Room.Id, null));
        Assert.Equal(deleted.CreatedAt, restored.GetConversations(f.Owner.Id).Single(room => room.Id == f.Room.Id).LastDeletedMessageAt);
    }

    [Fact]
    public void ContractSeparatesOlderServerFromAuthoritativeNoDeletion()
    {
        var legacy = JsonSerializer.Deserialize<ConversationSummary>("""
            {"Id":"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa","Title":"Empty","Participants":[],"UnreadCount":0}
            """)!;
        Assert.False(legacy.DeletedMessageMetadataAvailable); Assert.Null(legacy.LastDeletedMessageAt);
        var f = new Fixture(); var empty = f.Summary();
        Assert.True(empty.DeletedMessageMetadataAvailable); Assert.Null(empty.LastDeletedMessageAt);
        var roundTrip = JsonSerializer.Deserialize<ConversationSummary>(JsonSerializer.Serialize(empty))!;
        Assert.True(roundTrip.DeletedMessageMetadataAvailable); Assert.Null(roundTrip.LastDeletedMessageAt);
    }
}
