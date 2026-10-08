using System.Reflection;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using MTKChat.Contracts;
using MTKChat.Server.Configuration;
using MTKChat.Server.Services;
using Xunit;

namespace MTKChat.Cryptography.Tests;

public sealed class PinnedMessageTests
{
    internal sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    internal sealed class Fixture
    {
        public Clock Time { get; } = new();
        public ChatState State { get; }
        public ChatUser Owner { get; }
        public ChatUser Member { get; }
        public ChatUser Outside { get; }
        public ChatUser SiteAdmin { get; }
        public ConversationSummary Group { get; }
        public Fixture()
        {
            State = new ChatState(timeProvider: Time);
            Owner = User("owner"); Member = User("member"); Outside = User("outside"); SiteAdmin = User("site", "admin");
            Group = State.CreateConversation(Owner.Id, new("Synthetic pins group", [Member.Id]))!;
        }
        public ChatUser User(string name, string role = "user") => State.UpsertSiteUser(Guid.NewGuid(), name, name + "@pins.invalid", role);
        public StoredMessage Message(Guid? conversation = null, Guid? sender = null, Guid[]? recipients = null, DateTimeOffset? expires = null)
        {
            var room = conversation ?? Group.Id;
            var payloads = (recipients ?? State.GetMembers(room).ToArray())
                .Select(id => new EncryptedPayload("synthetic", "synthetic", "synthetic", "SYNTHETIC-CIPHERTEXT", "synthetic", "synthetic", id)).ToArray();
            Assert.True(State.AddMessage(sender ?? Owner.Id, new(Guid.NewGuid(), room, "text", Time.Now, expires, payloads, null), out var result));
            return result!;
        }
        public PinnedMessageView Pin(StoredMessage message, Guid? actor = null, int duration = 168)
        {
            Assert.Equal(PinMessageResult.Success, State.PinMessage(actor ?? Owner.Id, message.ConversationId, new(message.Id, duration), out var result));
            return result!;
        }
    }

    private static string Snapshot(ChatState state) => (string)typeof(ChatState)
        .GetMethod("SerializeSnapshotUnsafe", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(state, [])!;
    private static void Restore(ChatState state, string json) => typeof(ChatState)
        .GetMethod("RestoreSnapshot", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(state, [json]);

    [Theory]
    [InlineData(24)] [InlineData(168)] [InlineData(720)]
    public void PinUsesServerClockAndExpiresAtExactBoundary(int duration)
    {
        var f = new Fixture(); var message = f.Message(); var pin = f.Pin(message, duration: duration);
        Assert.Equal(f.Time.Now, pin.PinnedAt); Assert.Equal(f.Time.Now.AddHours(duration), pin.ExpiresAt);
        Assert.Equal(f.Owner.Id, pin.PinnedBy); Assert.Equal(f.Group.Id, pin.ConversationId); Assert.Equal(message.Id, pin.MessageId);
        f.Time.Now = pin.ExpiresAt.AddTicks(-1); Assert.Single(f.State.GetPinnedMessages(f.Member.Id, f.Group.Id)!);
        f.Time.Now = pin.ExpiresAt; Assert.Empty(f.State.GetPinnedMessages(f.Member.Id, f.Group.Id)!);
        Assert.Equal(PinMessageResult.NotFound, f.State.UnpinMessage(f.Owner.Id, f.Group.Id, message.Id));
    }

    [Fact]
    public void DefaultDurationAndMetadataHaveNoMessageBodyOrKeys()
    {
        var f = new Fixture(); var pin = f.Pin(f.Message());
        Assert.Equal(168, new PinMessageRequest(Guid.NewGuid()).DurationHours);
        var json = System.Text.Json.JsonSerializer.Serialize(pin);
        Assert.DoesNotContain("Ciphertext", json); Assert.DoesNotContain("Payload", json); Assert.DoesNotContain("Key", json);
        Assert.Equal(5, JsonNode.Parse(json)!.AsObject().Count);
    }

    [Theory]
    [InlineData(-1)] [InlineData(0)] [InlineData(1)] [InlineData(25)] [InlineData(169)] [InlineData(721)] [InlineData(int.MaxValue)]
    public void UnsupportedDurationAndEmptyIdDoNotMutate(int duration)
    {
        var f = new Fixture(); var message = f.Message(); var before = Snapshot(f.State);
        Assert.Equal(PinMessageResult.InvalidDuration, f.State.PinMessage(f.Owner.Id, f.Group.Id, new(message.Id, duration), out var result));
        Assert.Null(result); Assert.Equal(before, Snapshot(f.State));
        Assert.Equal(PinMessageResult.InvalidDuration, f.State.PinMessage(f.Owner.Id, f.Group.Id, new(Guid.Empty), out _));
        Assert.Equal(PinMessageResult.InvalidDuration, f.State.PinMessage(f.Owner.Id, f.Group.Id, null, out _));
    }

    [Fact]
    public void ThreePinLimitRenewalUnpinAndExpiredSlotAreAtomic()
    {
        var f = new Fixture(); var messages = Enumerable.Range(0, 4).Select(_ => f.Message()).ToArray();
        var pins = messages.Take(3).Select(message => f.Pin(message, duration: 24)).ToArray();
        Assert.Equal(PinMessageResult.CapacityReached, f.State.PinMessage(f.Owner.Id, f.Group.Id, new(messages[3].Id), out _));
        f.Time.Now = f.Time.Now.AddMinutes(1); var renewed = f.Pin(messages[0], duration: 168);
        Assert.Equal(f.Time.Now.AddHours(168), renewed.ExpiresAt); Assert.Equal(3, f.State.GetPinnedMessages(f.Member.Id, f.Group.Id)!.Count);
        Assert.Equal(PinMessageResult.Success, f.State.UnpinMessage(f.Owner.Id, f.Group.Id, messages[1].Id));
        f.Pin(messages[3]); Assert.Equal(3, f.State.GetPinnedMessages(f.Member.Id, f.Group.Id)!.Count);
        f.Time.Now = pins[2].ExpiresAt; f.Pin(f.Message());
        Assert.Equal(3, f.State.GetPinnedMessages(f.Member.Id, f.Group.Id)!.Count);
    }

    [Fact]
    public async Task ConcurrentPinsCannotExceedThreeSharedEntries()
    {
        var f = new Fixture(); var messages = Enumerable.Range(0, 20).Select(_ => f.Message()).ToArray();
        var statuses = await Task.WhenAll(messages.Select(message => Task.Run(() =>
            f.State.PinMessage(f.Owner.Id, f.Group.Id, new(message.Id), out _))));
        Assert.Equal(3, statuses.Count(status => status == PinMessageResult.Success));
        Assert.Equal(17, statuses.Count(status => status == PinMessageResult.CapacityReached));
        Assert.Equal(3, f.State.GetPinnedMessages(f.Member.Id, f.Group.Id)!.Count);
    }

    [Fact]
    public void GroupRolesAreCheckedAtMutationAndSiteAdminCannotBypassMembership()
    {
        var f = new Fixture(); var message = f.Message(); var pin = f.Pin(message);
        foreach (var outsider in new[] { f.Outside, f.SiteAdmin })
        {
            Assert.Null(f.State.GetPinnedMessages(outsider.Id, f.Group.Id));
            Assert.Equal(PinMessageResult.Forbidden, f.State.PinMessage(outsider.Id, f.Group.Id, new(message.Id), out _));
            Assert.Equal(PinMessageResult.Forbidden, f.State.UnpinMessage(outsider.Id, f.Group.Id, message.Id));
        }
        Assert.Equal(pin, Assert.Single(f.State.GetPinnedMessages(f.Member.Id, f.Group.Id)!));
        Assert.Equal(PinMessageResult.Forbidden, f.State.PinMessage(f.Member.Id, f.Group.Id, new(message.Id), out _));
        Assert.Equal(PinMessageResult.Forbidden, f.State.UnpinMessage(f.Member.Id, f.Group.Id, message.Id));
        foreach (var role in new[] { "mod", "admin" })
        {
            Assert.True(f.State.SetGroupRole(f.Owner.Id, f.Group.Id, f.Member.Id, role));
            Assert.Equal(PinMessageResult.Success, f.State.PinMessage(f.Member.Id, f.Group.Id, new(message.Id), out _));
            Assert.Equal(PinMessageResult.Success, f.State.UnpinMessage(f.Member.Id, f.Group.Id, message.Id)); f.Pin(message);
        }
        Assert.True(f.State.SetGroupRole(f.Owner.Id, f.Group.Id, f.Member.Id, "user"));
        Assert.Equal(PinMessageResult.Forbidden, f.State.UnpinMessage(f.Member.Id, f.Group.Id, message.Id));
        f.State.UpsertSiteUser(f.Member.Id, f.Member.DisplayName, f.Member.Email, "admin");
        Assert.Equal(PinMessageResult.Success, f.State.UnpinMessage(f.Member.Id, f.Group.Id, message.Id)); f.Pin(message);
        f.State.UpsertSiteUser(f.Member.Id, f.Member.DisplayName, f.Member.Email, "user");
        Assert.Equal(PinMessageResult.Forbidden, f.State.UnpinMessage(f.Member.Id, f.Group.Id, message.Id));
    }

    [Fact]
    public void BothDirectParticipantsCanManagePinsWithoutGroupAuthority()
    {
        var f = new Fixture(); var direct = f.State.GetOrCreateDirect(f.Owner.Id, f.Member.Id)!;
        var message = f.Message(direct.Id); f.Pin(message, f.Member.Id);
        Assert.Single(f.State.GetPinnedMessages(f.Owner.Id, direct.Id)!);
        Assert.Equal(PinMessageResult.Success, f.State.UnpinMessage(f.Owner.Id, direct.Id, message.Id));
        f.Pin(message); Assert.Equal(PinMessageResult.Success, f.State.UnpinMessage(f.Member.Id, direct.Id, message.Id));
        Assert.Null(f.State.GetPinnedMessages(f.SiteAdmin.Id, direct.Id));
    }

    [Theory]
    [InlineData("mute", false)] [InlineData("ban", false)] [InlineData("mute", true)] [InlineData("ban", true)]
    public void ModerationRestrictionsRejectPinAndUnpin(string action, bool chatOnly)
    {
        var f = new Fixture(); var message = f.Message(); f.Pin(message);
        Assert.True(chatOnly ? f.State.ModerateChatUser(f.Group.Id, f.Owner.Id, action, null) : f.State.ModerateUser(f.Owner.Id, action, null));
        Assert.Equal(PinMessageResult.Forbidden, f.State.PinMessage(f.Owner.Id, f.Group.Id, new(message.Id), out _));
        Assert.Equal(PinMessageResult.Forbidden, f.State.UnpinMessage(f.Owner.Id, f.Group.Id, message.Id));
        Assert.Single(f.State.GetPinnedMessages(f.Member.Id, f.Group.Id)!);
    }

    [Fact]
    public void BotUnknownMemberAndMissingConversationCannotReadOrWrite()
    {
        var f = new Fixture(); var bot = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var group = f.State.CreateConversation(f.Owner.Id, new("Bot group", [bot, f.Member.Id]))!;
        var message = f.Message(group.Id); f.Pin(message);
        foreach (var actor in new[] { bot, Guid.NewGuid() })
        {
            Assert.Null(f.State.GetPinnedMessages(actor, group.Id));
            Assert.Equal(PinMessageResult.Forbidden, f.State.PinMessage(actor, group.Id, new(message.Id), out _));
        }
        Assert.Null(f.State.GetPinnedMessages(f.Owner.Id, Guid.NewGuid()));
    }

    [Fact]
    public void HiddenClearedRemovedBlockedAndMissingEnvelopesNeverResurrectHistory()
    {
        var f = new Fixture(); var message = f.Message(); f.Pin(message);
        Assert.Equal(DeleteResult.Deleted, f.State.DeleteMessage(f.Member.Id, message.Id, false));
        Assert.Empty(f.State.GetPinnedMessages(f.Member.Id, f.Group.Id)!); Assert.Single(f.State.GetPinnedMessages(f.Owner.Id, f.Group.Id)!);
        Assert.Equal(PinMessageResult.NotFound, f.State.PinMessage(f.Owner.Id, f.Group.Id, new(Guid.NewGuid()), out _));
        var inaccessible = f.Message(recipients: [f.Member.Id]);
        Assert.Equal(PinMessageResult.NotFound, f.State.PinMessage(f.Owner.Id, f.Group.Id, new(inaccessible.Id), out _));
        f.State.SetGroupRole(f.Owner.Id, f.Group.Id, f.Member.Id, "mod"); f.Pin(inaccessible, f.Member.Id);
        Assert.Single(f.State.GetPinnedMessages(f.Owner.Id, f.Group.Id)!); // Only the original accessible pin.
        Assert.Equal(PinMessageResult.NotFound, f.State.UnpinMessage(f.Owner.Id, f.Group.Id, inaccessible.Id));
        Assert.Equal(1, f.State.ClearHistory(f.Member.Id, f.Group.Id, false));
        Assert.Empty(f.State.GetPinnedMessages(f.Member.Id, f.Group.Id)!);
        Assert.True(f.State.RemoveConversationForMe(f.Owner.Id, f.Group.Id)); Assert.Empty(f.State.GetPinnedMessages(f.Owner.Id, f.Group.Id)!);
        f.State.ReopenConversation(f.Owner.Id, f.Group.Id); Assert.Empty(f.State.GetPinnedMessages(f.Owner.Id, f.Group.Id)!);

        var newMessage = f.Message(sender: f.Member.Id); f.Pin(newMessage);
        Assert.True(f.State.SetBlocked(f.Owner.Id, f.Member.Id, true));
        Assert.Empty(f.State.GetPinnedMessages(f.Owner.Id, f.Group.Id)!);
        Assert.Equal(PinMessageResult.NotFound, f.State.PinMessage(f.Owner.Id, f.Group.Id, new(newMessage.Id), out _));
        Assert.Single(f.State.GetPinnedMessages(f.Member.Id, f.Group.Id)!);
    }

    [Fact]
    public void DirectBlockingEitherDirectionStopsPinMutations()
    {
        var f = new Fixture(); var direct = f.State.GetOrCreateDirect(f.Owner.Id, f.Member.Id)!;
        var message = f.Message(direct.Id); f.Pin(message);
        Assert.True(f.State.SetBlocked(f.Member.Id, f.Owner.Id, true));
        foreach (var actor in new[] { f.Owner.Id, f.Member.Id })
        {
            Assert.Equal(PinMessageResult.Forbidden, f.State.PinMessage(actor, direct.Id, new(message.Id), out _));
            Assert.Equal(PinMessageResult.Forbidden, f.State.UnpinMessage(actor, direct.Id, message.Id));
        }
    }

    [Fact]
    public void DeletionExpiryOtherRoomAndLeavingImmediatelyHideSharedPins()
    {
        var f = new Fixture(); var message = f.Message(); f.Pin(message);
        var other = f.State.CreateConversation(f.Owner.Id, new("Other", [f.Member.Id]))!;
        Assert.Equal(PinMessageResult.NotFound, f.State.PinMessage(f.Owner.Id, other.Id, new(message.Id), out _));
        Assert.Equal(PinMessageResult.NotFound, f.State.UnpinMessage(f.Owner.Id, other.Id, message.Id));
        Assert.Equal(DeleteResult.Deleted, f.State.DeleteMessage(f.Owner.Id, message.Id, true));
        Assert.Empty(f.State.GetPinnedMessages(f.Member.Id, f.Group.Id)!);
        var expiring = f.Message(expires: f.Time.Now.AddHours(1)); f.Pin(expiring);
        f.Time.Now = expiring.ExpiresAt!.Value; Assert.Empty(f.State.GetPinnedMessages(f.Member.Id, f.Group.Id)!);
        Assert.Equal(PinMessageResult.NotFound, f.State.PinMessage(f.Owner.Id, f.Group.Id, new(expiring.Id), out _));
        f.Pin(f.Message()); Assert.Equal(LeaveGroupResult.Left, f.State.LeaveGroup(f.Member.Id, f.Group.Id));
        Assert.Null(f.State.GetPinnedMessages(f.Member.Id, f.Group.Id)); Assert.Single(f.State.GetPinnedMessages(f.Owner.Id, f.Group.Id)!);
    }

    [Fact]
    public void NewMemberDoesNotReceiveOldPinnedMessageWithoutOriginalEnvelope()
    {
        var f = new Fixture(); var message = f.Message(); f.Pin(message);
        Assert.Equal(GroupInviteStatus.Success, f.State.CreateOrRotateGroupInvite(f.Owner.Id, f.Group.Id, out var invite));
        var token = new Uri(invite!.InviteUrl).Fragment[1..];
        Assert.Equal(GroupInviteStatus.Success, f.State.JoinGroupInvite(f.Outside.Id, token, out _));
        Assert.Empty(f.State.GetPinnedMessages(f.Outside.Id, f.Group.Id)!);
        Assert.True(f.State.SetGroupRole(f.Owner.Id, f.Group.Id, f.Outside.Id, "mod"));
        Assert.Equal(PinMessageResult.NotFound, f.State.PinMessage(f.Outside.Id, f.Group.Id, new(message.Id), out _));
        Assert.Equal(PinMessageResult.NotFound, f.State.UnpinMessage(f.Outside.Id, f.Group.Id, message.Id));
    }

    [Fact]
    public void SnapshotRoundTripPreservesPinsAndLegacySnapshotHasNone()
    {
        var f = new Fixture(); var pin = f.Pin(f.Message()); var snapshot = Snapshot(f.State);
        Restore(f.State, snapshot); Assert.Equal(pin, Assert.Single(f.State.GetPinnedMessages(f.Owner.Id, f.Group.Id)!));
        var legacy = JsonNode.Parse(snapshot)!.AsObject(); legacy.Remove("Pins");
        Restore(f.State, legacy.ToJsonString()); Assert.Empty(f.State.GetPinnedMessages(f.Owner.Id, f.Group.Id)!);
        Assert.Single(f.State.GetMessages(f.Owner.Id, f.Group.Id, null));
    }

    [Fact]
    public void OrphanedMessageNotInConversationHistoryCannotBePinned()
    {
        var f = new Fixture(); var message = f.Message(); var snapshot = JsonNode.Parse(Snapshot(f.State))!.AsObject();
        var room = snapshot["Conversations"]!.AsArray().Single(row => row!["Id"]!.GetValue<Guid>() == f.Group.Id)!;
        room["Messages"] = new JsonArray(); Restore(f.State, snapshot.ToJsonString());
        Assert.Empty(f.State.GetMessages(f.Owner.Id, f.Group.Id, null));
        Assert.Equal(PinMessageResult.NotFound, f.State.PinMessage(f.Owner.Id, f.Group.Id, new(message.Id), out _));
    }

    [Fact]
    public void MalformedRestoredPinsDoNotOccupyCapacityOrBindAnotherRoom()
    {
        var f = new Fixture(); f.Pin(f.Message()); var original = JsonNode.Parse(Snapshot(f.State))!.AsObject();
        foreach (var mutate in new Action<JsonObject>[]
        {
            row => row["MessageId"] = Guid.NewGuid(), row => row["ConversationId"] = Guid.NewGuid(),
            row => row["PinnedBy"] = Guid.Empty, row => row["PinnedAt"] = f.Time.Now.AddHours(1),
            row => row["ExpiresAt"] = f.Time.Now, row => row["ExpiresAt"] = f.Time.Now.AddHours(169),
            row => row["PinnedBy"] = Guid.Parse("22222222-2222-2222-2222-222222222222")
        })
        {
            var changed = original.DeepClone().AsObject(); mutate(changed["Pins"]![0]!.AsObject());
            Restore(f.State, changed.ToJsonString()); Assert.Empty(f.State.GetPinnedMessages(f.Owner.Id, f.Group.Id)!);
        }
        var duplicate = original.DeepClone().AsObject(); duplicate["Pins"]!.AsArray().Add(duplicate["Pins"]![0]!.DeepClone());
        duplicate["Pins"]!.AsArray().Insert(0, null); Restore(f.State, duplicate.ToJsonString());
        Assert.Single(f.State.GetPinnedMessages(f.Owner.Id, f.Group.Id)!);
    }

    [Fact]
    public void FailedPersistenceRollsBackNewRenewedUnpinnedAndPrunedRows()
    {
        var f = new Fixture(); var message = f.Message(); f.Pin(message, duration: 24); var another = f.Message();
        typeof(ChatState).GetField("_database", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(f.State, new ChatDatabase(Options.Create(new DatabaseOptions { Provider = "disabled" })));
        var before = Snapshot(f.State);
        Assert.Throws<InvalidOperationException>(() => f.State.PinMessage(f.Owner.Id, f.Group.Id, new(another.Id), out _));
        Assert.Equal(before, Snapshot(f.State));
        Assert.Throws<InvalidOperationException>(() => f.State.PinMessage(f.Owner.Id, f.Group.Id, new(message.Id, 720), out _));
        Assert.Equal(before, Snapshot(f.State));
        Assert.Throws<InvalidOperationException>(() => f.State.UnpinMessage(f.Owner.Id, f.Group.Id, message.Id)); Assert.Equal(before, Snapshot(f.State));
        f.Time.Now = f.Time.Now.AddHours(24);
        Assert.Throws<InvalidOperationException>(() => f.State.PinMessage(f.Owner.Id, f.Group.Id, new(another.Id), out _));
        Assert.Equal(before, Snapshot(f.State));
    }
}
