using System.Collections.Concurrent;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using MTKChat.Contracts;
using MTKChat.Server.Configuration;
using MTKChat.Server.Services;
using Xunit;

namespace MTKChat.Cryptography.Tests;

public sealed class InviteManagementTests
{
    private static readonly Guid Lounge = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid Gemini = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 5, 10, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    // Opaque ids model a protector boundary without a real filesystem/key ring. The
    // production protector is tested independently; these tests detect state leaks and
    // incorrect use of the group/id binding rather than reimplementing encryption.
    private sealed class Protector : IGroupInviteProtector
    {
        private readonly Dictionary<string, (Guid Room, Guid Id, string Token)> _values = new();
        public bool FailProtect { get; set; }
        public bool FailUnprotect { get; set; }
        public string? ReturnToken { get; set; }
        public string Protect(Guid conversationId, Guid inviteId, string token)
        {
            if (FailProtect) throw new CryptographicException("Synthetic protection failure.");
            var opaque = "protected-" + Guid.NewGuid().ToString("N");
            _values.Add(opaque, (conversationId, inviteId, token));
            return opaque;
        }
        public string? TryUnprotect(Guid conversationId, Guid inviteId, string protectedToken)
        {
            if (FailUnprotect) throw new CryptographicException("Synthetic unprotection failure.");
            if (ReturnToken is not null) return ReturnToken;
            return _values.TryGetValue(protectedToken, out var entry) && entry.Room == conversationId && entry.Id == inviteId
                ? entry.Token : null;
        }
    }

    private static ChatUser User(ChatState state, string name, string role = "user") =>
        state.UpsertSiteUser(Guid.NewGuid(), name, name + "@invite-management.invalid", role);
    private static GroupInviteEntry Create(ChatState state, Guid actor, Guid room, int duration = 10080)
    {
        Assert.Equal(GroupInviteStatus.Success, state.CreateGroupInvite(actor, room, duration, out var invite));
        Assert.NotNull(invite); Assert.NotNull(invite.InviteUrl);
        return invite;
    }
    private static IReadOnlyList<GroupInviteEntry> List(ChatState state, Guid actor, Guid room)
    {
        Assert.Equal(GroupInviteStatus.Success, state.ListGroupInvites(actor, room, out var entries));
        return entries!;
    }
    private static string Token(GroupInviteEntry invite) => new Uri(invite.InviteUrl!).Fragment[1..];
    private static string Snapshot(ChatState state) => (string)typeof(ChatState)
        .GetMethod("SerializeSnapshotUnsafe", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(state, [])!;
    private static void Restore(ChatState state, string json) => typeof(ChatState)
        .GetMethod("RestoreSnapshot", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(state, [json]);

    [Fact]
    public void SeveralLinksRemainIndependentlyUsableAndOneDeletionDoesNotRemoveOtherLinksOrMembers()
    {
        var state = new ChatState(); var admin = User(state, "Admin", "admin"); var applicant = User(state, "Applicant");
        var first = Create(state, admin.Id, Lounge, 5); var second = Create(state, admin.Id, Lounge, 60);
        Assert.NotEqual(first.Id, second.Id); Assert.NotEqual(first.InviteUrl, second.InviteUrl);
        Assert.Equal(2, List(state, admin.Id, Lounge).Count);
        Assert.Equal(GroupInviteStatus.Success, state.JoinGroupInvite(applicant.Id, Token(first), out _));
        Assert.Equal(GroupInviteStatus.Success, state.DeleteGroupInvite(admin.Id, Lounge, first.Id));
        Assert.Equal(GroupInviteStatus.InviteNotFound, state.DeleteGroupInvite(admin.Id, Lounge, first.Id));
        Assert.Equal(GroupInviteStatus.InvalidOrExpired, state.PreviewGroupInvite(applicant.Id, Token(first), out _));
        Assert.Equal(GroupInviteStatus.Success, state.PreviewGroupInvite(applicant.Id, Token(second), out _));
        Assert.True(state.IsMember(applicant.Id, Lounge)); Assert.Equal(second.Id, Assert.Single(List(state, admin.Id, Lounge)).Id);
    }

    [Theory]
    [InlineData(int.MinValue)]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(525601)]
    [InlineData(int.MaxValue)]
    public void InvalidDurationsAreRejectedWithoutMutation(int duration)
    {
        var state = new ChatState(); var admin = User(state, "Admin", "admin"); var valid = Create(state, admin.Id, Lounge);
        var before = Snapshot(state);
        Assert.Equal(GroupInviteStatus.InvalidDuration, state.CreateGroupInvite(admin.Id, Lounge, duration, out var created)); Assert.Null(created);
        Assert.Equal(GroupInviteStatus.InvalidDuration, state.ChangeGroupInviteDuration(admin.Id, Lounge, valid.Id, duration, out var changed)); Assert.Null(changed);
        Assert.Equal(before, Snapshot(state));
    }

    [Theory]
    [InlineData(5)]
    [InlineData(525600)]
    public void ValidDurationBoundsUseServerTime(int duration)
    {
        var clock = new Clock(); var state = new ChatState(timeProvider: clock); var admin = User(state, "Admin", "admin");
        var invite = Create(state, admin.Id, Lounge, duration);
        Assert.Equal(clock.Now, invite.CreatedAt); Assert.Equal(clock.Now.AddMinutes(duration), invite.ExpiresAt);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(10080)]
    [InlineData(525600)]
    public void ExplicitUnlimitedDurationCreatesListableLinkWithStableCompatibilityDate(int duration)
    {
        var clock = new Clock(); var state = new ChatState(timeProvider: clock); var admin = User(state, "Admin", "admin");
        Assert.Equal(GroupInviteStatus.Success, state.CreateGroupInvite(admin.Id, Lounge, duration, out var invite, neverExpires: true));
        Assert.NotNull(invite); Assert.True(invite.NeverExpires); Assert.Equal(DateTimeOffset.MaxValue, invite.ExpiresAt);
        Assert.Equal(clock.Now, invite.CreatedAt); Assert.Equal(invite, Assert.Single(List(state, admin.Id, Lounge)));
        Assert.Equal("https://mtkaya.me/chat/invite/", new Uri(invite.InviteUrl!).GetLeftPart(UriPartial.Path));
        Assert.Equal(43, Token(invite).Length);
    }

    [Theory]
    [InlineData(int.MinValue)]
    [InlineData(-1)]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(525601)]
    [InlineData(int.MaxValue)]
    public void UnlimitedFlagDoesNotMakeMalformedDurationsValidOrChangeExistingState(int duration)
    {
        var state = new ChatState(); var admin = User(state, "Admin", "admin"); var finite = Create(state, admin.Id, Lounge);
        var before = Snapshot(state);
        Assert.Equal(GroupInviteStatus.InvalidDuration, state.CreateGroupInvite(admin.Id, Lounge, duration, out var created, neverExpires: true));
        Assert.Null(created);
        Assert.Equal(GroupInviteStatus.InvalidDuration, state.ChangeGroupInviteDuration(admin.Id, Lounge, finite.Id, duration, out var changed, neverExpires: true));
        Assert.Null(changed); Assert.Equal(before, Snapshot(state));
    }

    [Fact]
    public void UnlimitedPreviewAndJoinRemainValidEvenAtMaximumClockValueAndDeletionStillRevokes()
    {
        var clock = new Clock(); var state = new ChatState(timeProvider: clock); var admin = User(state, "Admin", "admin");
        var user = User(state, "User"); var second = User(state, "Second");
        Assert.True(state.AddMessage(admin.Id, new(Guid.NewGuid(), Lounge, "text", clock.Now, null,
            [new EncryptedPayload("test", "test", "test", "PAST-CIPHERTEXT", "test", "test", admin.Id)], null), out var past));
        Assert.Equal(GroupInviteStatus.Success, state.CreateGroupInvite(admin.Id, Lounge, 0, out var invite, neverExpires: true));
        var token = Token(invite!); clock.Now = DateTimeOffset.MaxValue;
        Assert.Equal(GroupInviteStatus.Success, state.PreviewGroupInvite(user.Id, token, out var preview));
        Assert.True(preview!.NeverExpires); Assert.Equal(DateTimeOffset.MaxValue, preview.ExpiresAt); Assert.False(preview.AlreadyMember);
        Assert.False(state.IsMember(user.Id, Lounge));
        Assert.Equal(GroupInviteStatus.Success, state.JoinGroupInvite(user.Id, token, out var joined));
        Assert.Equal("user", joined!.GroupRoles![user.Id]); Assert.False(state.IsAdmin(user.Id));
        Assert.Empty(state.GetMessages(user.Id, Lounge, null));
        Assert.Equal(past!.Id, Assert.Single(state.GetMessages(admin.Id, Lounge, null)).Id);
        Assert.Equal(GroupInviteStatus.Success, state.PreviewGroupInvite(user.Id, token, out preview)); Assert.True(preview!.AlreadyMember);
        Assert.Equal(GroupInviteStatus.Success, state.JoinGroupInvite(user.Id, token, out _));
        Assert.Equal(GroupInviteStatus.Success, state.DeleteGroupInvite(admin.Id, Lounge, invite!.Id));
        Assert.Equal(GroupInviteStatus.InvalidOrExpired, state.PreviewGroupInvite(second.Id, token, out _));
        Assert.Equal(GroupInviteStatus.InvalidOrExpired, state.JoinGroupInvite(second.Id, token, out _));
        Assert.True(state.IsMember(user.Id, Lounge)); Assert.False(state.IsMember(second.Id, Lounge));
    }

    [Fact]
    public void FiniteAndUnlimitedSwitchesKeepSameIdentityAndCanReturnToNormalExpiry()
    {
        var clock = new Clock(); var state = new ChatState(timeProvider: clock); var admin = User(state, "Admin", "admin"); var user = User(state, "User");
        var original = Create(state, admin.Id, Lounge, 5); Assert.False(original.NeverExpires);
        clock.Now = original.ExpiresAt;
        Assert.Equal(GroupInviteStatus.InvalidOrExpired, state.PreviewGroupInvite(user.Id, Token(original), out _));
        Assert.Equal(GroupInviteStatus.Success, state.ChangeGroupInviteDuration(admin.Id, Lounge, original.Id, 0, out var unlimited, neverExpires: true));
        Assert.True(unlimited!.NeverExpires); Assert.Equal(DateTimeOffset.MaxValue, unlimited.ExpiresAt);
        Assert.Equal(original.Id, unlimited.Id); Assert.Equal(original.InviteUrl, unlimited.InviteUrl); Assert.Equal(original.CreatedAt, unlimited.CreatedAt);
        clock.Now = clock.Now.AddYears(100);
        Assert.Equal(GroupInviteStatus.Success, state.PreviewGroupInvite(user.Id, Token(original), out _));
        Assert.Equal(GroupInviteStatus.Success, state.ChangeGroupInviteDuration(admin.Id, Lounge, original.Id, 60, out var finite));
        Assert.False(finite!.NeverExpires); Assert.Equal(clock.Now.AddHours(1), finite.ExpiresAt);
        Assert.Equal(original.Id, finite.Id); Assert.Equal(original.InviteUrl, finite.InviteUrl); Assert.Equal(original.CreatedAt, finite.CreatedAt);
        clock.Now = finite.ExpiresAt.AddTicks(-1);
        Assert.Equal(GroupInviteStatus.Success, state.PreviewGroupInvite(user.Id, Token(original), out _));
        clock.Now = finite.ExpiresAt;
        Assert.Equal(GroupInviteStatus.InvalidOrExpired, state.JoinGroupInvite(user.Id, Token(original), out _));
    }

    [Fact]
    public void RestartRetainsUnlimitedFlagAndProtectedTokenWithoutConvertingLegacyFiniteRows()
    {
        var clock = new Clock(); var protector = new Protector(); var state = new ChatState(timeProvider: clock, inviteProtector: protector);
        var admin = User(state, "Admin", "admin"); var user = User(state, "User");
        Assert.Equal(GroupInviteStatus.Success, state.CreateGroupInvite(admin.Id, Lounge, 0, out var unlimited, neverExpires: true));
        var finite = Create(state, admin.Id, Lounge, 5);
        var json = Snapshot(state); Assert.DoesNotContain(Token(unlimited!), json); Assert.DoesNotContain(Token(finite), json);
        var rows = JsonNode.Parse(json)!;
        var finiteRow = rows["GroupInvites"]!.AsArray().Single(row => row!["Id"]!.GetValue<Guid>() == finite.Id)!;
        finiteRow.AsObject().Remove("NeverExpires"); // Actual snapshots from older releases have no flag.
        var restored = new ChatState(timeProvider: clock, inviteProtector: protector);
        restored.UpsertSiteUser(admin.Id, admin.DisplayName, admin.Email, admin.Role);
        restored.UpsertSiteUser(user.Id, user.DisplayName, user.Email, user.Role);
        clock.Now = DateTimeOffset.MaxValue;
        Restore(restored, rows.ToJsonString()); var entries = List(restored, admin.Id, Lounge);
        Assert.Equal(unlimited, entries.Single(item => item.Id == unlimited!.Id));
        Assert.Equal(finite, entries.Single(item => item.Id == finite.Id)); Assert.False(entries.Single(item => item.Id == finite.Id).NeverExpires);
        Assert.Equal(GroupInviteStatus.Success, restored.PreviewGroupInvite(user.Id, Token(unlimited!), out var preview)); Assert.True(preview!.NeverExpires);
        Assert.Equal(GroupInviteStatus.InvalidOrExpired, restored.PreviewGroupInvite(user.Id, Token(finite), out _));
        Assert.Equal(GroupInviteStatus.Success, restored.JoinGroupInvite(user.Id, Token(unlimited!), out _));
    }

    [Fact]
    public void RestoreCanonicalizesOnlyExplicitUnlimitedFlagNotLargeFiniteCompatibilityDate()
    {
        var clock = new Clock(); var state = new ChatState(timeProvider: clock); var admin = User(state, "Admin", "admin"); var user = User(state, "User");
        var marked = Create(state, admin.Id, Lounge); var unmarked = Create(state, admin.Id, Lounge);
        var json = JsonNode.Parse(Snapshot(state))!;
        var rows = json["GroupInvites"]!.AsArray();
        var markedRow = rows.Single(row => row!["Id"]!.GetValue<Guid>() == marked.Id)!;
        markedRow["NeverExpires"] = true; markedRow["ExpiresAt"] = JsonValue.Create(clock.Now.AddDays(-1));
        var unmarkedRow = rows.Single(row => row!["Id"]!.GetValue<Guid>() == unmarked.Id)!;
        unmarkedRow.AsObject().Remove("NeverExpires"); unmarkedRow["ExpiresAt"] = JsonValue.Create(DateTimeOffset.MaxValue);
        Restore(state, json.ToJsonString()); var entries = List(state, admin.Id, Lounge);
        Assert.True(entries.Single(item => item.Id == marked.Id).NeverExpires);
        Assert.Equal(DateTimeOffset.MaxValue, entries.Single(item => item.Id == marked.Id).ExpiresAt);
        Assert.False(entries.Single(item => item.Id == unmarked.Id).NeverExpires);
        clock.Now = DateTimeOffset.MaxValue;
        Assert.Equal(GroupInviteStatus.Success, state.PreviewGroupInvite(user.Id, Token(marked), out _));
        Assert.Equal(GroupInviteStatus.InvalidOrExpired, state.PreviewGroupInvite(user.Id, Token(unmarked), out _));
    }

    [Fact]
    public void UnlimitedFlagCannotBypassGroupManagementAuthorityBanOrDirectRoomRules()
    {
        var state = new ChatState(); var owner = User(state, "Owner"); var mod = User(state, "Mod"); var member = User(state, "Member"); var outside = User(state, "Outside");
        var room = state.CreateConversation(owner.Id, new("Private", [mod.Id, member.Id]))!;
        Assert.True(state.SetGroupRole(owner.Id, room.Id, mod.Id, "mod"));
        Assert.Equal(GroupInviteStatus.Success, state.CreateGroupInvite(owner.Id, room.Id, 0, out var invite, neverExpires: true));
        foreach (var actor in new[] { mod.Id, member.Id, outside.Id, Gemini })
        {
            Assert.Equal(GroupInviteStatus.Forbidden, state.CreateGroupInvite(actor, room.Id, 0, out _, neverExpires: true));
            Assert.Equal(GroupInviteStatus.Forbidden, state.ChangeGroupInviteDuration(actor, room.Id, invite!.Id, 0, out _, neverExpires: true));
        }
        // Chat moderation legitimately targets current members, not a never-joined
        // outsider. Ban a real member, then leave; the retained ban must block even an
        // unlimited invitation from readmitting that account.
        Assert.True(state.ModerateChatUser(room.Id, member.Id, "ban", null));
        Assert.Equal(LeaveGroupResult.Left, state.LeaveGroup(member.Id, room.Id));
        Assert.Equal(GroupInviteStatus.Forbidden, state.PreviewGroupInvite(member.Id, Token(invite!), out _));
        Assert.Equal(GroupInviteStatus.Forbidden, state.JoinGroupInvite(member.Id, Token(invite!), out _));
        Assert.False(state.IsMember(member.Id, room.Id));
        var siteAdmin = User(state, "SiteAdmin", "admin"); var direct = state.GetOrCreateDirect(owner.Id, member.Id)!;
        Assert.Equal(GroupInviteStatus.NotAGroup, state.CreateGroupInvite(siteAdmin.Id, direct.Id, 0, out _, neverExpires: true));
        Assert.True(state.ModerateUser(owner.Id, "ban", null));
        Assert.Equal(GroupInviteStatus.Forbidden, state.ChangeGroupInviteDuration(owner.Id, room.Id, invite!.Id, 0, out _, neverExpires: true));
    }

    [Fact]
    public void UnlimitedChangesAndCreationsRollbackCompletelyIfSnapshotSaveFails()
    {
        var state = new ChatState(); var admin = User(state, "Admin", "admin"); var finite = Create(state, admin.Id, Lounge);
        Assert.Equal(GroupInviteStatus.Success, state.CreateGroupInvite(admin.Id, Lounge, 0, out var unlimited, neverExpires: true));
        typeof(ChatState).GetField("_database", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(state, new ChatDatabase(Options.Create(new DatabaseOptions { Provider = "disabled" })));
        var before = Snapshot(state);
        Assert.Throws<InvalidOperationException>(() => state.CreateGroupInvite(admin.Id, Lounge, 0, out _, neverExpires: true)); Assert.Equal(before, Snapshot(state));
        Assert.Throws<InvalidOperationException>(() => state.ChangeGroupInviteDuration(admin.Id, Lounge, finite.Id, 0, out _, neverExpires: true)); Assert.Equal(before, Snapshot(state));
        Assert.Throws<InvalidOperationException>(() => state.ChangeGroupInviteDuration(admin.Id, Lounge, unlimited!.Id, 60, out _)); Assert.Equal(before, Snapshot(state));
        Assert.Throws<InvalidOperationException>(() => state.DeleteGroupInvite(admin.Id, Lounge, unlimited!.Id)); Assert.Equal(before, Snapshot(state));
    }

    [Fact]
    public void UnlimitedProtectionFailureAndFiftyLinkLimitLeaveNoPartialEntry()
    {
        var protector = new Protector(); var state = new ChatState(inviteProtector: protector); var admin = User(state, "Admin", "admin");
        var original = Create(state, admin.Id, Lounge); var before = Snapshot(state); protector.FailProtect = true;
        Assert.Throws<CryptographicException>(() => state.CreateGroupInvite(admin.Id, Lounge, 0, out _, neverExpires: true)); Assert.Equal(before, Snapshot(state));
        protector.FailProtect = false;
        for (var index = 1; index < 50; index++)
            Assert.Equal(GroupInviteStatus.Success, state.CreateGroupInvite(admin.Id, Lounge, 0, out _, neverExpires: true));
        Assert.Equal(GroupInviteStatus.InviteLimitReached, state.CreateGroupInvite(admin.Id, Lounge, 0, out _, neverExpires: true));
        Assert.Equal(GroupInviteStatus.InviteLimitReached, state.CreateGroupInvite(admin.Id, Lounge, 60, out _));
        Assert.Equal(50, List(state, admin.Id, Lounge).Count); Assert.Contains(List(state, admin.Id, Lounge), item => item.Id == original.Id);
    }

    [Fact]
    public void LegacyRequestAndPreviewJsonStayFiniteWhenFlagIsOmitted()
    {
        Assert.False(JsonSerializer.Deserialize<CreateGroupInviteRequest>("{}")!.NeverExpires);
        Assert.Equal(10080, JsonSerializer.Deserialize<CreateGroupInviteRequest>("{}")!.DurationMinutes);
        Assert.False(JsonSerializer.Deserialize<ChangeGroupInviteDurationRequest>("{\"DurationMinutes\":60}")!.NeverExpires);
        var preview = JsonSerializer.Deserialize<GroupInvitePreview>(JsonSerializer.Serialize(new
        { ConversationId = Lounge, Title = "Legacy", ExpiresAt = DateTimeOffset.MaxValue, AlreadyMember = false }))!;
        Assert.False(preview.NeverExpires); Assert.Equal(DateTimeOffset.MaxValue, preview.ExpiresAt);
    }

    [Fact]
    public void DurationChangeKeepsTheSameUrlAndCreationDateAndCanExplicitlyReviveAnExpiredEntry()
    {
        var clock = new Clock(); var state = new ChatState(timeProvider: clock); var admin = User(state, "Admin", "admin"); var user = User(state, "User");
        var invite = Create(state, admin.Id, Lounge, 5); clock.Now = invite.ExpiresAt;
        Assert.Equal(GroupInviteStatus.InvalidOrExpired, state.JoinGroupInvite(user.Id, Token(invite), out _));
        Assert.Single(List(state, admin.Id, Lounge));
        Assert.Equal(GroupInviteStatus.Success, state.ChangeGroupInviteDuration(admin.Id, Lounge, invite.Id, 60, out var changed));
        Assert.Equal(invite.Id, changed!.Id); Assert.Equal(invite.InviteUrl, changed.InviteUrl); Assert.Equal(invite.CreatedAt, changed.CreatedAt);
        Assert.Equal(clock.Now.AddHours(1), changed.ExpiresAt);
        Assert.Equal(GroupInviteStatus.Success, state.PreviewGroupInvite(user.Id, Token(invite), out _));
        clock.Now = changed.ExpiresAt.AddTicks(-1); Assert.Equal(GroupInviteStatus.Success, state.PreviewGroupInvite(user.Id, Token(invite), out _));
        clock.Now = changed.ExpiresAt; Assert.Equal(GroupInviteStatus.InvalidOrExpired, state.PreviewGroupInvite(user.Id, Token(invite), out _));
    }

    [Fact]
    public void LegacyRotationDoesNotInvalidateManagedLinksButExplicitLegacyRevocationRemovesAllRoomLinks()
    {
        var state = new ChatState(); var admin = User(state, "Admin", "admin"); var user = User(state, "User");
        var managed = Create(state, admin.Id, Lounge);
        Assert.Equal(GroupInviteStatus.Success, state.CreateOrRotateGroupInvite(admin.Id, Lounge, out var first));
        Assert.Equal(GroupInviteStatus.Success, state.CreateOrRotateGroupInvite(admin.Id, Lounge, out var second));
        Assert.Equal(GroupInviteStatus.InvalidOrExpired, state.PreviewGroupInvite(user.Id, new Uri(first!.InviteUrl).Fragment[1..], out _));
        Assert.Equal(GroupInviteStatus.Success, state.PreviewGroupInvite(user.Id, new Uri(second!.InviteUrl).Fragment[1..], out _));
        Assert.Equal(GroupInviteStatus.Success, state.PreviewGroupInvite(user.Id, Token(managed), out _));
        Assert.Equal(2, List(state, admin.Id, Lounge).Count);
        Assert.Contains(List(state, admin.Id, Lounge), item => item.Id == Lounge);
        Assert.Equal(GroupInviteStatus.Success, state.RevokeGroupInvite(admin.Id, Lounge));
        Assert.Empty(List(state, admin.Id, Lounge));
        Assert.Equal(GroupInviteStatus.InvalidOrExpired, state.PreviewGroupInvite(user.Id, Token(managed), out _));
        Assert.Equal(GroupInviteStatus.InvalidOrExpired, state.PreviewGroupInvite(user.Id, new Uri(second.InviteUrl).Fragment[1..], out _));
    }

    [Fact]
    public void ManagementDeniesUsersModsBotsBansAndDirectRoomsWithoutExposingLinkEntries()
    {
        var state = new ChatState(); var owner = User(state, "Owner"); var mod = User(state, "Mod"); var member = User(state, "Member");
        var outside = User(state, "Outside"); var siteAdmin = User(state, "SiteAdmin", "admin");
        var room = state.CreateConversation(owner.Id, new("Private", [mod.Id, member.Id]))!;
        Assert.True(state.SetGroupRole(owner.Id, room.Id, mod.Id, "mod")); var invite = Create(state, owner.Id, room.Id);
        foreach (var actor in new[] { mod.Id, member.Id, outside.Id, Gemini, Guid.NewGuid() })
        {
            Assert.Equal(GroupInviteStatus.Forbidden, state.ListGroupInvites(actor, room.Id, out var entries)); Assert.Null(entries);
            Assert.Equal(GroupInviteStatus.Forbidden, state.CreateGroupInvite(actor, room.Id, 60, out _));
            Assert.Equal(GroupInviteStatus.Forbidden, state.ChangeGroupInviteDuration(actor, room.Id, invite.Id, 60, out _));
            Assert.Equal(GroupInviteStatus.Forbidden, state.DeleteGroupInvite(actor, room.Id, invite.Id));
        }
        Assert.Equal(invite.Id, Assert.Single(List(state, siteAdmin.Id, room.Id)).Id);
        Assert.False(state.IsMember(siteAdmin.Id, room.Id)); Assert.Empty(state.GetMessages(siteAdmin.Id, room.Id, null));
        var direct = state.GetOrCreateDirect(owner.Id, member.Id)!;
        Assert.Equal(GroupInviteStatus.NotAGroup, state.ListGroupInvites(siteAdmin.Id, direct.Id, out _));
        Assert.Equal(GroupInviteStatus.NotAGroup, state.CreateGroupInvite(siteAdmin.Id, direct.Id, 60, out _));
        Assert.Equal(GroupInviteStatus.NotFound, state.ListGroupInvites(siteAdmin.Id, Guid.NewGuid(), out _));
        Assert.True(state.ModerateChatUser(room.Id, owner.Id, "ban", null));
        Assert.Equal(GroupInviteStatus.Forbidden, state.ListGroupInvites(owner.Id, room.Id, out _));
        Assert.True(state.ModerateUser(siteAdmin.Id, "ban", null));
        Assert.Equal(GroupInviteStatus.Forbidden, state.ListGroupInvites(siteAdmin.Id, room.Id, out _));
    }

    [Fact]
    public void InviteIdsAreScopedToTheirGroupAndInvalidIdsDoNotMutateOtherRooms()
    {
        var state = new ChatState(); var admin = User(state, "Admin", "admin"); var member = User(state, "Member");
        var room = state.CreateConversation(admin.Id, new("Other", [member.Id]))!;
        var invite = Create(state, admin.Id, Lounge); var before = Snapshot(state);
        Assert.Equal(GroupInviteStatus.InviteNotFound, state.DeleteGroupInvite(admin.Id, room.Id, invite.Id));
        Assert.Equal(GroupInviteStatus.InviteNotFound, state.ChangeGroupInviteDuration(admin.Id, room.Id, invite.Id, 60, out _));
        Assert.Equal(GroupInviteStatus.InviteNotFound, state.DeleteGroupInvite(admin.Id, Lounge, Guid.Empty));
        Assert.Equal(before, Snapshot(state));
    }

    [Fact]
    public void FiftyEntryCapIncludesExpiredLinksAndConcurrentCreationCannotBypassIt()
    {
        var clock = new Clock(); var state = new ChatState(timeProvider: clock); var admin = User(state, "Admin", "admin");
        var first = Create(state, admin.Id, Lounge, 5);
        clock.Now = first.ExpiresAt;
        var results = new ConcurrentBag<GroupInviteStatus>();
        Parallel.For(0, 65, index => results.Add(state.CreateGroupInvite(admin.Id, Lounge, 60, out _)));
        Assert.Equal(49, results.Count(item => item == GroupInviteStatus.Success));
        Assert.Equal(16, results.Count(item => item == GroupInviteStatus.InviteLimitReached));
        Assert.Equal(50, List(state, admin.Id, Lounge).Count);
        Assert.Equal(GroupInviteStatus.InviteLimitReached, state.CreateOrRotateGroupInvite(admin.Id, Lounge, out _));
        Assert.Equal(GroupInviteStatus.Success, state.DeleteGroupInvite(admin.Id, Lounge, first.Id));
        Assert.Equal(GroupInviteStatus.Success, state.CreateOrRotateGroupInvite(admin.Id, Lounge, out _));
        Assert.Equal(GroupInviteStatus.Success, state.CreateOrRotateGroupInvite(admin.Id, Lounge, out _));
        Assert.Equal(50, List(state, admin.Id, Lounge).Count);
    }

    [Fact]
    public void ProtectedTokensPersistWithoutPlaintextAndRemainListableAfterRestore()
    {
        var clock = new Clock(); var protector = new Protector(); var state = new ChatState(timeProvider: clock, inviteProtector: protector);
        var admin = User(state, "Admin", "admin"); var first = Create(state, admin.Id, Lounge, 5); var second = Create(state, admin.Id, Lounge, 60);
        var json = Snapshot(state); Assert.DoesNotContain(Token(first), json); Assert.DoesNotContain(Token(second), json); Assert.DoesNotContain("InviteUrl", json);
        var rows = JsonNode.Parse(json)!["GroupInvites"]!.AsArray(); Assert.Equal(2, rows.Count);
        Assert.All(rows, row => { Assert.NotNull(row!["ProtectedToken"]); Assert.NotNull(row["CreatedAt"]); });
        clock.Now = first.ExpiresAt;
        var restored = new ChatState(timeProvider: clock, inviteProtector: protector); restored.UpsertSiteUser(admin.Id, admin.DisplayName, admin.Email, admin.Role);
        Restore(restored, json); var entries = List(restored, admin.Id, Lounge);
        Assert.Equal(2, entries.Count); Assert.Equal(first, entries.Single(item => item.Id == first.Id)); Assert.Equal(second, entries.Single(item => item.Id == second.Id));
    }

    [Fact]
    public void HashOnlyLegacyMigrationKeepsItsIdAndJoinValidityButDoesNotFabricateOrExposeUrl()
    {
        var clock = new Clock(); var state = new ChatState(timeProvider: clock); var admin = User(state, "Admin", "admin"); var user = User(state, "User");
        Assert.Equal(GroupInviteStatus.Success, state.CreateOrRotateGroupInvite(admin.Id, Lounge, out var original));
        var token = new Uri(original!.InviteUrl).Fragment[1..]; var snapshot = JsonNode.Parse(Snapshot(state))!;
        var row = snapshot["GroupInvites"]![0]!.AsObject(); row.Remove("Id"); row.Remove("ProtectedToken"); row.Remove("CreatedAt");
        Restore(state, snapshot.ToJsonString()); var before = Snapshot(state); var entry = Assert.Single(List(state, admin.Id, Lounge));
        Assert.Equal(Lounge, entry.Id); Assert.Null(entry.InviteUrl); Assert.Null(entry.CreatedAt); Assert.Equal(before, Snapshot(state));
        Assert.Equal(GroupInviteStatus.Success, state.PreviewGroupInvite(user.Id, token, out _));
        clock.Now = original.ExpiresAt;
        Restore(state, Snapshot(state)); Assert.Single(List(state, admin.Id, Lounge));
        Assert.Equal(GroupInviteStatus.InvalidOrExpired, state.JoinGroupInvite(user.Id, token, out _));
        Assert.Equal(GroupInviteStatus.Success, state.ChangeGroupInviteDuration(admin.Id, Lounge, Lounge, 30, out var extended));
        Assert.Null(extended!.InviteUrl); Assert.Equal(GroupInviteStatus.Success, state.PreviewGroupInvite(user.Id, token, out _));
        Assert.Equal(GroupInviteStatus.Success, state.DeleteGroupInvite(admin.Id, Lounge, Lounge));
        Assert.Equal(GroupInviteStatus.InvalidOrExpired, state.PreviewGroupInvite(user.Id, token, out _));
    }

    [Fact]
    public void MissingKeyCorruptCipherAndHashMismatchListUnavailableUrlWithoutRotationOrMutation()
    {
        var protector = new Protector(); var state = new ChatState(inviteProtector: protector); var admin = User(state, "Admin", "admin");
        var invite = Create(state, admin.Id, Lounge); var original = Snapshot(state);
        protector.FailUnprotect = true; Assert.Null(Assert.Single(List(state, admin.Id, Lounge)).InviteUrl); Assert.Equal(original, Snapshot(state));
        protector.FailUnprotect = false; protector.ReturnToken = new string('A', 43);
        Assert.Null(Assert.Single(List(state, admin.Id, Lounge)).InviteUrl); Assert.Equal(original, Snapshot(state));
        protector.ReturnToken = "malformed"; Assert.Null(Assert.Single(List(state, admin.Id, Lounge)).InviteUrl);
        protector.ReturnToken = null;
        var snapshot = JsonNode.Parse(original)!; snapshot["GroupInvites"]![0]!["ProtectedToken"] = "unrecognized opaque value";
        Restore(state, snapshot.ToJsonString()); var corrupt = Snapshot(state);
        Assert.Null(Assert.Single(List(state, admin.Id, Lounge)).InviteUrl); Assert.Equal(corrupt, Snapshot(state));
        // The acceptance boundary is still the original token hash, not recoverability.
        var user = User(state, "User"); Assert.Equal(GroupInviteStatus.Success, state.PreviewGroupInvite(user.Id, Token(invite), out _));
    }

    [Fact]
    public void ProtectionFailureNeverCreatesOrRotatesAnyLink()
    {
        var protector = new Protector(); var state = new ChatState(inviteProtector: protector); var admin = User(state, "Admin", "admin");
        Create(state, admin.Id, Lounge); Assert.Equal(GroupInviteStatus.Success, state.CreateOrRotateGroupInvite(admin.Id, Lounge, out _));
        var before = Snapshot(state); protector.FailProtect = true;
        Assert.Throws<CryptographicException>(() => state.CreateGroupInvite(admin.Id, Lounge, 60, out _));
        Assert.Throws<CryptographicException>(() => state.CreateOrRotateGroupInvite(admin.Id, Lounge, out _));
        Assert.Equal(before, Snapshot(state));
    }

    [Fact]
    public void SaveFailureRollsBackCreationDurationDeletionLegacyRotationAndBulkRevocation()
    {
        var state = new ChatState(); var admin = User(state, "Admin", "admin"); var existing = Create(state, admin.Id, Lounge);
        Create(state, admin.Id, Lounge); Assert.Equal(GroupInviteStatus.Success, state.CreateOrRotateGroupInvite(admin.Id, Lounge, out _));
        // Inject an unconfigured database only after setup: its Open throws immediately
        // before creating a connection, giving deterministic failure without a network.
        typeof(ChatState).GetField("_database", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(state, new ChatDatabase(Options.Create(new DatabaseOptions { Provider = "disabled" })));
        var before = Snapshot(state);
        Assert.Throws<InvalidOperationException>(() => state.CreateGroupInvite(admin.Id, Lounge, 60, out _)); Assert.Equal(before, Snapshot(state));
        Assert.Throws<InvalidOperationException>(() => state.ChangeGroupInviteDuration(admin.Id, Lounge, existing.Id, 60, out _)); Assert.Equal(before, Snapshot(state));
        Assert.Throws<InvalidOperationException>(() => state.DeleteGroupInvite(admin.Id, Lounge, existing.Id)); Assert.Equal(before, Snapshot(state));
        Assert.Throws<InvalidOperationException>(() => state.CreateOrRotateGroupInvite(admin.Id, Lounge, out _)); Assert.Equal(before, Snapshot(state));
        Assert.Throws<InvalidOperationException>(() => state.RevokeGroupInvite(admin.Id, Lounge));
        // Dictionary insertion order may differ after bulk rollback, but exact retained
        // entries and their identities/URLs/expiry must match the original snapshot.
        var restoredEntries = JsonNode.Parse(Snapshot(state))!["GroupInvites"]!.AsArray().OrderBy(row => row!["Id"]!.GetValue<Guid>()).Select(row => row!.ToJsonString()).ToArray();
        var originalEntries = JsonNode.Parse(before)!["GroupInvites"]!.AsArray().OrderBy(row => row!["Id"]!.GetValue<Guid>()).Select(row => row!.ToJsonString()).ToArray();
        Assert.Equal(originalEntries, restoredEntries); Assert.Equal(3, List(state, admin.Id, Lounge).Count);
    }

    [Fact]
    public void RestoreRejectsInvalidBindingsAndHashesAndBoundsRetainedEntriesToFiftyPerGroup()
    {
        var state = new ChatState(); var admin = User(state, "Admin", "admin"); var member = User(state, "Member");
        var direct = state.GetOrCreateDirect(admin.Id, member.Id)!;
        var anotherGroup = state.CreateConversation(admin.Id, new("Another", [member.Id]))!;
        var valid = Create(state, admin.Id, Lounge); var snapshot = JsonNode.Parse(Snapshot(state))!; var rows = snapshot["GroupInvites"]!.AsArray();
        var validRow = rows[0]!.DeepClone();
        for (var i = 0; i < 55; i++) { var entry = validRow.DeepClone(); entry["Id"] = Guid.NewGuid(); rows.Add(entry); }
        var malformed = validRow.DeepClone(); malformed["Id"] = Guid.NewGuid(); malformed["TokenHash"] = "invalid"; rows.Insert(0, malformed);
        var directEntry = validRow.DeepClone(); directEntry["Id"] = Guid.NewGuid(); directEntry["ConversationId"] = direct.Id; rows.Insert(0, directEntry);
        var missing = validRow.DeepClone(); missing["Id"] = Guid.NewGuid(); missing["ConversationId"] = Guid.NewGuid(); rows.Insert(0, missing);
        var collision = validRow.DeepClone(); collision["Id"] = anotherGroup.Id; rows.Insert(0, collision);
        Restore(state, snapshot.ToJsonString()); var entries = List(state, admin.Id, Lounge);
        Assert.Equal(50, entries.Count); Assert.Contains(entries, item => item.Id == valid.Id);
        Assert.DoesNotContain(entries, item => item.Id == malformed["Id"]!.GetValue<Guid>());
        Assert.DoesNotContain(entries, item => item.Id == anotherGroup.Id);
        Assert.Equal(GroupInviteStatus.NotAGroup, state.ListGroupInvites(admin.Id, direct.Id, out _));
    }
}
