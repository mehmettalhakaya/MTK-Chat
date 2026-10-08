using System.Collections.Concurrent;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MTKChat.Contracts;
using MTKChat.Cryptography;
using MTKChat.Server.Services;
using Xunit;

namespace MTKChat.Cryptography.Tests;

public sealed class GroupInviteTests
{
    private static readonly Guid Lounge = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid Gemini = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Groq = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static ChatUser User(ChatState state, string name, string role = "user") =>
        state.UpsertSiteUser(Guid.NewGuid(), name, name + "@invites.invalid", role);

    private static SiteAccount Account(ChatUser user, int id = 1) => new(id, user.Id, user.DisplayName, user.Email, user.Role, true);
    private static string Token(GroupInviteResult invite) => new Uri(invite.InviteUrl).Fragment[1..];
    private static GroupInviteResult Invite(ChatState state, Guid actor, Guid room)
    {
        Assert.Equal(GroupInviteStatus.Success, state.CreateOrRotateGroupInvite(actor, room, out var invite));
        return invite!;
    }
    private static string Snapshot(ChatState state) => (string)typeof(ChatState).GetMethod("SerializeSnapshotUnsafe", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(state, [])!;
    private static void Restore(ChatState state, string json) => typeof(ChatState).GetMethod("RestoreSnapshot", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(state, [json]);

    [Fact]
    public void NormalAccountCreationRepeatedLoginAndDirectorySyncDoNotJoinLounge()
    {
        var state = new ChatState(); var first = User(state, "First");
        Assert.False(state.IsMember(first.Id, Lounge)); Assert.Empty(state.GetConversations(first.Id));
        state.UpsertSiteUser(first.Id, first.DisplayName, first.Email, "user");
        var next = new SiteAccount(2, Guid.NewGuid(), "Next", "next@invites.invalid", "user", true);
        state.SynchronizeSiteUsers([Account(first), next]); state.SynchronizeSiteUsers([Account(first), next]);
        Assert.False(state.IsMember(first.Id, Lounge)); Assert.False(state.IsMember(next.ChatId, Lounge));
        Assert.True(state.IsMember(Gemini, Lounge)); Assert.True(state.IsMember(Groq, Lounge));
        var admin = User(state, "Admin", "admin"); Assert.True(state.IsMember(admin.Id, Lounge));
        var syncedAdmin = new SiteAccount(3, Guid.NewGuid(), "SyncedAdmin", "admin2@invites.invalid", "admin", true);
        state.SynchronizeSiteUsers([Account(first), next, Account(admin, 4), syncedAdmin]);
        Assert.True(state.IsMember(syncedAdmin.ChatId, Lounge));
    }

    [Fact]
    public void ExistingNormalLoungeMembershipSurvivesUpgradeLoginAndSiteSync()
    {
        var state = new ChatState(); var existing = User(state, "Existing"); var newUser = User(state, "New");
        var legacy = JsonNode.Parse(Snapshot(state))!;
        var lounge = legacy["Conversations"]!.AsArray().Single(row => row!["Id"]!.GetValue<Guid>() == Lounge)!;
        lounge["Members"]!.AsArray().Add(existing.Id);
        legacy.AsObject().Remove("GroupInvites"); Restore(state, legacy.ToJsonString());
        state.UpsertSiteUser(existing.Id, existing.DisplayName, existing.Email, existing.Role);
        state.SynchronizeSiteUsers([Account(existing), Account(newUser, 2)]);
        Assert.True(state.IsMember(existing.Id, Lounge)); Assert.False(state.IsMember(newUser.Id, Lounge));
    }

    [Fact]
    public void InvitesAreRandomFixedDomainFragmentLinksAndExpireAtSevenDays()
    {
        var clock = new Clock(DateTimeOffset.UtcNow); var state = new ChatState(timeProvider: clock);
        var admin = User(state, "Admin", "admin"); var user = User(state, "User");
        var invite = Invite(state, admin.Id, Lounge); var token = Token(invite); var uri = new Uri(invite.InviteUrl);
        Assert.Equal("https", uri.Scheme); Assert.Equal("mtkaya.me", uri.Host); Assert.Equal("/chat/invite/", uri.AbsolutePath);
        Assert.Equal("", uri.Query); Assert.Equal(43, token.Length); Assert.Matches("^[A-Za-z0-9_-]{43}$", token);
        Assert.Equal(clock.Now.AddDays(7), invite.ExpiresAt);
        clock.Now = invite.ExpiresAt.AddTicks(-1);
        Assert.Equal(GroupInviteStatus.Success, state.PreviewGroupInvite(user.Id, token, out _));
        clock.Now = invite.ExpiresAt;
        Assert.Equal(GroupInviteStatus.InvalidOrExpired, state.PreviewGroupInvite(user.Id, token, out var expired)); Assert.Null(expired);
        Assert.Equal(GroupInviteStatus.InvalidOrExpired, state.JoinGroupInvite(user.Id, token, out _));
        Assert.False(state.IsMember(user.Id, Lounge));
    }

    [Fact]
    public void RotationAndIdempotentRevocationImmediatelyInvalidatePriorLinks()
    {
        var state = new ChatState(); var admin = User(state, "Admin", "admin"); var user = User(state, "User");
        var first = Invite(state, admin.Id, Lounge); var second = Invite(state, admin.Id, Lounge);
        Assert.NotEqual(first.InviteUrl, second.InviteUrl);
        Assert.Equal(GroupInviteStatus.InvalidOrExpired, state.JoinGroupInvite(user.Id, Token(first), out _));
        Assert.Equal(GroupInviteStatus.Success, state.PreviewGroupInvite(user.Id, Token(second), out _));
        Assert.Equal(GroupInviteStatus.Success, state.RevokeGroupInvite(admin.Id, Lounge));
        Assert.Equal(GroupInviteStatus.Success, state.RevokeGroupInvite(admin.Id, Lounge));
        Assert.Equal(GroupInviteStatus.InvalidOrExpired, state.JoinGroupInvite(user.Id, Token(second), out _));
    }

    [Fact]
    public void ManagementRequiresSiteAdminOrCurrentGroupAdminNotModUserBotOrDirect()
    {
        var state = new ChatState(); var owner = User(state, "Owner"); var mod = User(state, "Mod"); var member = User(state, "Member");
        var outside = User(state, "Outside"); var site = User(state, "Site", "admin");
        var room = state.CreateConversation(owner.Id, new("Private", [mod.Id, member.Id]))!;
        Assert.True(state.SetGroupRole(owner.Id, room.Id, mod.Id, "mod"));
        foreach (var actor in new[] { mod.Id, member.Id, outside.Id, Gemini, Guid.NewGuid() })
        {
            Assert.Equal(GroupInviteStatus.Forbidden, state.CreateOrRotateGroupInvite(actor, room.Id, out _));
            Assert.Equal(GroupInviteStatus.Forbidden, state.RevokeGroupInvite(actor, room.Id));
        }
        var invite = Invite(state, site.Id, room.Id); Assert.False(state.IsMember(site.Id, room.Id));
        Assert.Empty(state.GetMessages(site.Id, room.Id, null));
        Assert.Equal(GroupInviteStatus.Success, state.RevokeGroupInvite(owner.Id, room.Id));
        var direct = state.GetOrCreateDirect(owner.Id, member.Id)!;
        Assert.Equal(GroupInviteStatus.NotAGroup, state.CreateOrRotateGroupInvite(site.Id, direct.Id, out _));
        Assert.Equal(GroupInviteStatus.NotFound, state.CreateOrRotateGroupInvite(site.Id, Guid.NewGuid(), out _));
        Assert.Equal(GroupInviteStatus.InvalidOrExpired, state.PreviewGroupInvite(member.Id, Token(invite), out _));
    }

    [Fact]
    public void InvitePreviewIsReadOnlyMinimalAndRequiresARealUnbannedHuman()
    {
        var state = new ChatState(); var admin = User(state, "Admin", "admin"); var user = User(state, "User");
        var invite = Invite(state, admin.Id, Lounge); var token = Token(invite);
        Assert.Equal(GroupInviteStatus.Success, state.PreviewGroupInvite(user.Id, token, out var preview));
        Assert.Equal(Lounge, preview!.ConversationId); Assert.Equal("MTK Lounge", preview.Title); Assert.False(preview.AlreadyMember);
        Assert.False(state.IsMember(user.Id, Lounge)); Assert.Empty(state.GetMessages(user.Id, Lounge, null));
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(preview));
        Assert.Equal(new[] { "AlreadyMember", "ConversationId", "ExpiresAt", "NeverExpires", "Title" }, document.RootElement.EnumerateObject().Select(p => p.Name).Order().ToArray());
        foreach (var invalid in new[] { Guid.NewGuid(), Gemini })
        {
            Assert.Equal(GroupInviteStatus.Forbidden, state.PreviewGroupInvite(invalid, token, out _));
            Assert.Equal(GroupInviteStatus.Forbidden, state.JoinGroupInvite(invalid, token, out _));
        }
        Assert.True(state.ModerateUser(user.Id, "ban", null));
        Assert.Equal(GroupInviteStatus.Forbidden, state.PreviewGroupInvite(user.Id, token, out _));
        Assert.Equal(GroupInviteStatus.Forbidden, state.JoinGroupInvite(user.Id, token, out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA+")]
    public void InvalidOrForgedTokensCannotJoinOrRevealMetadata(string token)
    {
        var state = new ChatState(); var admin = User(state, "Admin", "admin"); var user = User(state, "User");
        Invite(state, admin.Id, Lounge);
        Assert.Equal(GroupInviteStatus.InvalidOrExpired, state.PreviewGroupInvite(user.Id, token, out var preview)); Assert.Null(preview);
        Assert.Equal(GroupInviteStatus.InvalidOrExpired, state.JoinGroupInvite(user.Id, token, out var group)); Assert.Null(group);
        Assert.False(state.IsMember(user.Id, Lounge));
    }

    [Fact]
    public void GroupBanIsRetainedAcrossLeaveAndDeniedForPreviewJoinAndManagement()
    {
        var state = new ChatState(); var owner = User(state, "Owner"); var member = User(state, "Member");
        var room = state.CreateConversation(owner.Id, new("Private", [member.Id]))!;
        var token = Token(Invite(state, owner.Id, room.Id));
        Assert.True(state.ModerateChatUser(room.Id, member.Id, "ban", null));
        Assert.Equal(LeaveGroupResult.Left, state.LeaveGroup(member.Id, room.Id));
        Assert.Equal(GroupInviteStatus.Forbidden, state.PreviewGroupInvite(member.Id, token, out _));
        Assert.Equal(GroupInviteStatus.Forbidden, state.JoinGroupInvite(member.Id, token, out _));
        Assert.False(state.IsMember(member.Id, room.Id));
        Assert.True(state.ModerateChatUser(room.Id, owner.Id, "ban", null));
        Assert.Equal(GroupInviteStatus.Forbidden, state.CreateOrRotateGroupInvite(owner.Id, room.Id, out _));
        Assert.Equal(GroupInviteStatus.Forbidden, state.RevokeGroupInvite(owner.Id, room.Id));
        Assert.True(state.ModerateUser(owner.Id, "ban", null));
        Assert.Equal(GroupInviteStatus.Forbidden, state.RevokeGroupInvite(owner.Id, room.Id));
    }

    [Fact]
    public void ExplicitJoinClearsLeftHiddenMarkersAndDoesNotRestoreOldGroupPrivileges()
    {
        var state = new ChatState(); var owner = User(state, "Owner"); var member = User(state, "Member");
        var room = state.CreateConversation(owner.Id, new("Private", [member.Id]))!;
        var token = Token(Invite(state, owner.Id, room.Id));
        Assert.True(state.SetGroupRole(owner.Id, room.Id, member.Id, "mod"));
        Assert.Equal(LeaveGroupResult.Left, state.LeaveGroup(member.Id, room.Id));
        Assert.Equal(GroupInviteStatus.Success, state.JoinGroupInvite(member.Id, token, out var joined));
        Assert.Equal("user", joined!.GroupRoles![member.Id]); Assert.False(state.IsAdmin(member.Id));
        Assert.True(state.RemoveConversationForMe(member.Id, room.Id)); Assert.DoesNotContain(state.GetConversations(member.Id), c => c.Id == room.Id);
        Assert.Equal(GroupInviteStatus.Success, state.JoinGroupInvite(member.Id, token, out _));
        Assert.Contains(state.GetConversations(member.Id), c => c.Id == room.Id);
        var snapshot = JsonNode.Parse(Snapshot(state))!;
        Assert.DoesNotContain(snapshot["LeftGroups"]!.AsArray(), row => row!["ConversationId"]!.GetValue<Guid>() == room.Id && row["UserId"]!.GetValue<Guid>() == member.Id);
        Assert.DoesNotContain(snapshot["HiddenConversations"]!.AsArray(), row => row!["ConversationId"]!.GetValue<Guid>() == room.Id && row["UserId"]!.GetValue<Guid>() == member.Id);
    }

    [Fact]
    public void MembershipIsIdempotentAtCapacityAndConcurrentJoinNeverExceedsFifty()
    {
        var state = new ChatState(); var owner = User(state, "Owner");
        var initial = Enumerable.Range(0, 47).Select(i => User(state, "Member" + i)).ToArray();
        var room = state.CreateConversation(owner.Id, new("Capacity", initial.Select(u => u.Id).ToArray()))!;
        var token = Token(Invite(state, owner.Id, room.Id));
        var applicants = Enumerable.Range(0, 12).Select(i => User(state, "Applicant" + i)).ToArray();
        var statuses = new ConcurrentBag<GroupInviteStatus>();
        Parallel.ForEach(applicants, applicant => statuses.Add(state.JoinGroupInvite(applicant.Id, token, out _)));
        Assert.Equal(2, statuses.Count(status => status == GroupInviteStatus.Success));
        Assert.Equal(10, statuses.Count(status => status == GroupInviteStatus.CapacityReached)); Assert.Equal(50, state.GetMembers(room.Id).Count);
        Assert.Equal(GroupInviteStatus.Success, state.JoinGroupInvite(owner.Id, token, out _));
        Assert.Equal("admin", state.GetGroupRole(room.Id, owner.Id));
        Assert.Equal(50, state.GetMembers(room.Id).Count);
    }

    [Fact]
    public void NewMemberCannotReadPastEnvelopesButCanDecryptFutureRecipientMessage()
    {
        var state = new ChatState(); var owner = User(state, "Owner"); var member = User(state, "Member"); var newcomer = User(state, "Newcomer");
        var room = state.CreateConversation(owner.Id, new("Private", [member.Id]))!;
        using var ownerDevice = DeviceIdentity.Create(); using var newcomerDevice = DeviceIdentity.Create();
        state.RegisterDevice(owner.Id, new("owner", ownerDevice.ExportEncryptionPublicKey(), ownerDevice.ExportSigningPublicKey()));
        state.RegisterDevice(newcomer.Id, new("newcomer", newcomerDevice.ExportEncryptionPublicKey(), newcomerDevice.ExportSigningPublicKey()));
        StoredMessage Send(Guid[] recipients, string text)
        {
            var id = Guid.NewGuid(); var at = DateTimeOffset.UtcNow;
            var payloads = recipients.Select(recipient => MessageCryptography.Encrypt(Encoding.UTF8.GetBytes(text), id, room.Id, owner.Id,
                recipient, at, recipient == newcomer.Id ? newcomerDevice.ExportEncryptionPublicKey() : ownerDevice.ExportEncryptionPublicKey(), ownerDevice.SigningKey)).ToArray();
            Assert.True(state.AddMessage(owner.Id, new(id, room.Id, "text", at, null, payloads, null), out var stored)); return stored!;
        }
        var past = Send([owner.Id, member.Id], "Before join"); var oldCipher = state.GetMessages(owner.Id, room.Id, null).Single().Payloads.Single();
        var token = Token(Invite(state, owner.Id, room.Id));
        Assert.Empty(state.GetMessages(newcomer.Id, room.Id, null));
        Assert.Equal(GroupInviteStatus.Success, state.JoinGroupInvite(newcomer.Id, token, out var joined));
        Assert.Null(joined!.LastMessageAt); Assert.Empty(state.GetMessages(newcomer.Id, room.Id, null)); Assert.Empty(state.GetDeliveryInbox(newcomer.Id));
        Assert.Equal(oldCipher, state.GetMessages(owner.Id, room.Id, null).Single().Payloads.Single());
        var next = Send([owner.Id, member.Id, newcomer.Id], "After join");
        var received = Assert.Single(state.GetMessages(newcomer.Id, room.Id, null)); Assert.Equal(next.Id, received.Id);
        Assert.Equal("After join", Encoding.UTF8.GetString(MessageCryptography.Decrypt(Assert.Single(received.Payloads), received.ClientMessageId,
            room.Id, owner.Id, received.CreatedAt, newcomerDevice.EncryptionKey, ownerDevice.ExportSigningPublicKey())));
        Assert.DoesNotContain(state.GetDeliveryInbox(newcomer.Id), message => message.Id == past.Id);
        Assert.Equal(GroupInviteStatus.Success, state.JoinGroupInvite(newcomer.Id, token, out _));
        Assert.Single(state.GetMessages(newcomer.Id, room.Id, null)); // An idempotent join does not clear already-visible history.
    }

    [Fact]
    public void NewMembershipDoesNotPermitDownloadingAnOlderEncryptedFileEvenWithItsStorageToken()
    {
        var state = new ChatState(); var owner = User(state, "Owner"); var member = User(state, "Member"); var newcomer = User(state, "Newcomer");
        var room = state.CreateConversation(owner.Id, new("Private", [member.Id]))!; var client = Guid.NewGuid();
        var encrypted = FileCryptography.Encrypt("Before invitation"u8.ToArray(), "before.txt", client, room.Id, owner.Id);
        var storage = state.UploadEncryptedFile(owner.Id, room.Id, client, encrypted.Ciphertext)!;
        Assert.True(state.AddMessage(owner.Id, new(client, room.Id, "file", DateTimeOffset.UtcNow, null,
            new[] { owner.Id, member.Id }.Select(id => new EncryptedPayload("test", "", "", "encrypted descriptor", "", "", id)).ToArray(),
            new("", "application/octet-stream", encrypted.Ciphertext.Length, storage, "", "")), out _));
        Assert.Equal(encrypted.Ciphertext, state.DownloadEncryptedFile(member.Id, storage));
        Assert.Null(state.DownloadEncryptedFile(newcomer.Id, storage));
        Assert.Equal(GroupInviteStatus.Success, state.JoinGroupInvite(newcomer.Id, Token(Invite(state, owner.Id, room.Id)), out _));
        Assert.Empty(state.GetMessages(newcomer.Id, room.Id, null)); Assert.Null(state.DownloadEncryptedFile(newcomer.Id, storage));
        Assert.Equal(encrypted.Ciphertext, state.DownloadEncryptedFile(member.Id, storage));
    }

    [Fact]
    public void SnapshotPersistsNoPlaintextAndRestoresValidInvitesButRejectsInvalidExpiredOrRevokedTokens()
    {
        var clock = new Clock(DateTimeOffset.UtcNow); var state = new ChatState(timeProvider: clock);
        var admin = User(state, "Admin", "admin"); var user = User(state, "User");
        var token = Token(Invite(state, admin.Id, Lounge)); var json = Snapshot(state);
        Assert.DoesNotContain(token, json); Assert.DoesNotContain("InviteUrl", json);
        var snapshot = JsonNode.Parse(json)!; var rows = snapshot["GroupInvites"]!.AsArray();
        Assert.Single(rows); Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(token))), rows[0]!["TokenHash"]!.GetValue<string>());
        var restored = new ChatState(timeProvider: clock);
        restored.UpsertSiteUser(admin.Id, admin.DisplayName, admin.Email, admin.Role); restored.UpsertSiteUser(user.Id, user.DisplayName, user.Email, user.Role);
        Restore(restored, json);
        Assert.Equal(GroupInviteStatus.Success, restored.PreviewGroupInvite(user.Id, token, out _));
        Assert.Equal(GroupInviteStatus.Success, restored.RevokeGroupInvite(admin.Id, Lounge));
        Restore(restored, Snapshot(restored)); Assert.Equal(GroupInviteStatus.InvalidOrExpired, restored.JoinGroupInvite(user.Id, token, out _));
        snapshot.AsObject().Remove("GroupInvites"); Restore(restored, snapshot.ToJsonString());
        Assert.Equal(GroupInviteStatus.InvalidOrExpired, restored.PreviewGroupInvite(user.Id, token, out _));
        snapshot = JsonNode.Parse(json)!; snapshot["GroupInvites"]![0]!["TokenHash"] = "not a hash"; Restore(restored, snapshot.ToJsonString());
        Assert.Equal(GroupInviteStatus.InvalidOrExpired, restored.JoinGroupInvite(user.Id, token, out _));
        clock.Now += TimeSpan.FromDays(7); Restore(restored, json);
        Assert.Equal(GroupInviteStatus.InvalidOrExpired, restored.PreviewGroupInvite(user.Id, token, out _));
    }
}
