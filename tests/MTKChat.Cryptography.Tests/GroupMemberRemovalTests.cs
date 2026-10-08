using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MTKChat.Contracts;
using MTKChat.Cryptography;
using MTKChat.Server.Configuration;
using MTKChat.Server.Services;
using Xunit;

namespace MTKChat.Cryptography.Tests;

public sealed class GroupMemberRemovalTests
{
    private static readonly Guid Gemini = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Lounge = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static ChatUser User(ChatState state, string name, string role = "user") =>
        state.UpsertSiteUser(Guid.NewGuid(), name, name + "@member-removal.invalid", role);
    private static string Snapshot(ChatState state) => (string)typeof(ChatState)
        .GetMethod("SerializeSnapshotUnsafe", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(state, [])!;
    private static void Restore(ChatState state, string json) => typeof(ChatState)
        .GetMethod("RestoreSnapshot", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(state, [json]);
    private static string InviteToken(ChatState state, Guid owner, Guid room)
    {
        Assert.Equal(GroupInviteStatus.Success, state.CreateOrRotateGroupInvite(owner, room, out var invite));
        return new Uri(invite!.InviteUrl).Fragment[1..];
    }
    private static StoredMessage Send(ChatState state, Guid owner, Guid room, params Guid[] recipients)
    {
        Assert.True(state.AddMessage(owner, new(Guid.NewGuid(), room, "text", DateTimeOffset.UtcNow, null,
            recipients.Select(id => new EncryptedPayload("test", "test", "test", "ciphertext", "test", "test", id)).ToArray(), null), out var message));
        return message!;
    }

    [Fact]
    public void OnlyRealUnrestrictedSiteOrGroupAdministratorsCanRemoveMembers()
    {
        var state = new ChatState(); var owner = User(state, "owner"); var target = User(state, "target");
        var mod = User(state, "mod"); var member = User(state, "member"); var outside = User(state, "outside");
        var site = User(state, "site", "admin");
        var group = state.CreateConversation(owner.Id, new("Group", [target.Id, mod.Id, member.Id]))!;
        Assert.True(state.SetGroupRole(owner.Id, group.Id, mod.Id, "mod"));
        foreach (var id in new[] { mod.Id, member.Id, outside.Id, Gemini, Guid.NewGuid() })
            Assert.Equal(GroupMemberRemovalStatus.Forbidden, state.RemoveGroupMember(id, group.Id, target.Id));
        Assert.True(state.IsMember(target.Id, group.Id));
        Assert.True(state.ModerateUser(owner.Id, "ban", null));
        Assert.Equal(GroupMemberRemovalStatus.Forbidden, state.RemoveGroupMember(owner.Id, group.Id, target.Id));
        Assert.True(state.ModerateUser(owner.Id, "unban", null));
        Assert.True(state.ModerateChatUser(group.Id, owner.Id, "ban", null));
        Assert.Equal(GroupMemberRemovalStatus.Forbidden, state.RemoveGroupMember(owner.Id, group.Id, target.Id));
        Assert.True(state.ModerateChatUser(group.Id, owner.Id, "unban", null));
        Assert.Equal(GroupMemberRemovalStatus.Removed, state.RemoveGroupMember(owner.Id, group.Id, target.Id));
        Assert.Equal(GroupMemberRemovalStatus.Removed, state.RemoveGroupMember(site.Id, group.Id, mod.Id));
        Assert.False(state.IsMember(site.Id, group.Id)); // Metadata authority does not grant private-group reading.
    }

    [Fact]
    public void SelfBotsSiteAdminsAndPeerGroupAdminsAreProtected()
    {
        var state = new ChatState(); var owner = User(state, "owner"); var target = User(state, "target");
        var site = User(state, "site", "admin");
        var room = state.CreateConversation(owner.Id, new("Group", [target.Id, site.Id, Gemini]))!;
        Assert.True(state.SetGroupRole(owner.Id, room.Id, target.Id, "admin"));
        foreach (var id in new[] { owner.Id, target.Id, site.Id, Gemini })
            Assert.Equal(GroupMemberRemovalStatus.ProtectedTarget, state.RemoveGroupMember(owner.Id, room.Id, id));
        Assert.True(state.ModerateUser(target.Id, "ban", null));
        Assert.Equal(GroupMemberRemovalStatus.ProtectedTarget, state.RemoveGroupMember(owner.Id, room.Id, target.Id));
        Assert.True(state.ModerateUser(target.Id, "unban", null));
        Assert.Equal(GroupMemberRemovalStatus.ProtectedTarget, state.RemoveGroupMember(site.Id, room.Id, site.Id));
        Assert.Equal(GroupMemberRemovalStatus.ProtectedTarget, state.RemoveGroupMember(site.Id, room.Id, Gemini));
        Assert.Equal(GroupMemberRemovalStatus.Removed, state.RemoveGroupMember(site.Id, room.Id, target.Id));
        Assert.Equal(GroupMemberRemovalStatus.Removed, state.RemoveGroupMember(site.Id, room.Id, owner.Id));
        Assert.True(state.IsMember(site.Id, room.Id));
    }

    [Fact]
    public void UnknownTargetsAndDirectChatsCannotBeRemovedAsGroupMembers()
    {
        var state = new ChatState(); var owner = User(state, "owner"); var target = User(state, "target"); var outsider = User(state, "outside");
        var room = state.CreateConversation(owner.Id, new("Group", [target.Id]))!;
        Assert.Equal(GroupMemberRemovalStatus.NotFound, state.RemoveGroupMember(owner.Id, Guid.NewGuid(), target.Id));
        Assert.Equal(GroupMemberRemovalStatus.NotFound, state.RemoveGroupMember(owner.Id, room.Id, outsider.Id));
        Assert.Equal(GroupMemberRemovalStatus.NotFound, state.RemoveGroupMember(owner.Id, room.Id, Guid.NewGuid()));
        var direct = state.GetOrCreateDirect(owner.Id, target.Id)!;
        Assert.Equal(GroupMemberRemovalStatus.NotAGroup, state.RemoveGroupMember(owner.Id, direct.Id, target.Id));
        Assert.True(state.IsMember(target.Id, direct.Id));
    }

    [Fact]
    public void RemovalRevokesOnlyGroupAccessAndRetainsAccountDevicesOtherChatsAndCiphertext()
    {
        var state = new ChatState(); var owner = User(state, "owner"); var target = User(state, "target"); var other = User(state, "other");
        var room = state.CreateConversation(owner.Id, new("Group", [target.Id, other.Id]))!;
        var separate = state.CreateConversation(target.Id, new("Separate", [other.Id]))!;
        var message = Send(state, owner.Id, room.Id, owner.Id, target.Id, other.Id);
        var separateMessage = Send(state, target.Id, separate.Id, target.Id, other.Id);
        Assert.True(state.RegisterDevice(target.Id, new("device", "encryption", "signature")));
        var device = state.GetDevice(target.Id); var session = state.CreateSession(target.Id);
        state.TouchActivity(target.Id, room.Id);
        Assert.True(state.SetGroupRole(owner.Id, room.Id, target.Id, "mod"));
        Assert.Equal(GroupMemberRemovalStatus.Removed, state.RemoveGroupMember(owner.Id, room.Id, target.Id));
        Assert.False(state.IsMember(target.Id, room.Id)); Assert.Equal("user", state.GetGroupRole(room.Id, target.Id));
        Assert.Empty(state.GetMessages(target.Id, room.Id, null)); Assert.DoesNotContain(state.GetDeliveryInbox(target.Id), m => m.Id == message.Id);
        Assert.DoesNotContain(state.GetConversations(target.Id), group => group.Id == room.Id);
        Assert.Equal(message.Id, Assert.Single(state.GetMessages(other.Id, room.Id, null)).Id);
        Assert.Equal("ciphertext", Assert.Single(state.GetMessages(other.Id, room.Id, null).Single().Payloads).Ciphertext);
        Assert.Equal(separateMessage.Id, Assert.Single(state.GetMessages(target.Id, separate.Id, null)).Id);
        Assert.Equal(target, state.GetUser(target.Id)); Assert.Equal(device, state.GetDevice(target.Id));
        Assert.Equal(target.Id, state.ResolveSession(session)); Assert.False(state.IsBanned(target.Id));
        var presence = state.GetPresence(room.Id, owner.Id).Single(item => item.User.Id == target.Id);
        Assert.False(presence.IsInConversation); Assert.False(presence.IsViewingConversation);
        Assert.False(state.AddMessage(owner.Id, new(Guid.NewGuid(), room.Id, "text", DateTimeOffset.UtcNow, null,
            [new("test", "test", "test", "excluded", "test", "test", target.Id)], null), out _));
        Assert.Equal(GroupMemberRemovalStatus.NotFound, state.RemoveGroupMember(owner.Id, room.Id, target.Id));
    }

    [Fact]
    public void RemovedUserCanRejoinOnlyByInviteWithoutOldHistoryOrOldRoleAndDirectorySyncDoesNotReadd()
    {
        var state = new ChatState(); var owner = User(state, "owner", "admin"); var target = User(state, "target");
        var token = InviteToken(state, owner.Id, Lounge);
        Assert.Equal(GroupInviteStatus.Success, state.JoinGroupInvite(target.Id, token, out _));
        Assert.True(state.SetGroupRole(owner.Id, Lounge, target.Id, "mod"));
        var old = Send(state, owner.Id, Lounge, owner.Id, target.Id);
        Assert.Equal(GroupMemberRemovalStatus.Removed, state.RemoveGroupMember(owner.Id, Lounge, target.Id));
        var json = Snapshot(state);
        var restored = new ChatState(); restored.UpsertSiteUser(owner.Id, owner.DisplayName, owner.Email, owner.Role);
        restored.UpsertSiteUser(target.Id, target.DisplayName, target.Email, target.Role); Restore(restored, json);
        restored.SynchronizeSiteUsers([new(1, owner.Id, owner.DisplayName, owner.Email, owner.Role, true),
            new(2, target.Id, target.DisplayName, target.Email, target.Role, true)]);
        Assert.False(restored.IsMember(target.Id, Lounge));
        Assert.Equal(GroupInviteStatus.Success, restored.JoinGroupInvite(target.Id, token, out _));
        Assert.Equal("user", restored.GetGroupRole(Lounge, target.Id)); Assert.Empty(restored.GetMessages(target.Id, Lounge, null));
        Assert.DoesNotContain(restored.GetDeliveryInbox(target.Id), message => message.Id == old.Id);
        var future = Send(restored, owner.Id, Lounge, owner.Id, target.Id);
        Assert.Equal(future.Id, Assert.Single(restored.GetMessages(target.Id, Lounge, null)).Id);
        Assert.Equal(2, restored.GetMessages(owner.Id, Lounge, null).Count);
    }

    [Fact]
    public void RemovalRetainsBanAndMuteRecordsAndDoesNotClearActivityInOtherRoom()
    {
        var state = new ChatState(); var owner = User(state, "owner"); var target = User(state, "target");
        var room = state.CreateConversation(owner.Id, new("Group", [target.Id]))!;
        var separate = state.CreateConversation(target.Id, new("Separate", [owner.Id]))!;
        var token = InviteToken(state, owner.Id, room.Id);
        state.TouchActivity(target.Id, separate.Id);
        Assert.True(state.ModerateChatUser(room.Id, target.Id, "mute", null));
        Assert.True(state.ModerateChatUser(room.Id, target.Id, "ban", null));
        Assert.True(state.ModerateUser(target.Id, "mute", null));
        Assert.Equal(GroupMemberRemovalStatus.Removed, state.RemoveGroupMember(owner.Id, room.Id, target.Id));
        Assert.True(state.IsMuted(target.Id, out _)); Assert.True(state.IsChatWriteRestricted(room.Id, target.Id, out _));
        Assert.Equal(GroupInviteStatus.Forbidden, state.JoinGroupInvite(target.Id, token, out _));
        Assert.True(state.GetPresence(separate.Id, owner.Id).Single(item => item.User.Id == target.Id).IsViewingConversation);
        var snapshot = JsonNode.Parse(Snapshot(state))!;
        Assert.Contains(snapshot["ChatBannedUsers"]!.AsArray(), row => row!["ConversationId"]!.GetValue<Guid>() == room.Id && row["UserId"]!.GetValue<Guid>() == target.Id);
        Assert.Contains(snapshot["ChatMutedUsers"]!.AsArray(), row => row!["ConversationId"]!.GetValue<Guid>() == room.Id && row["UserId"]!.GetValue<Guid>() == target.Id);
    }

    [Fact]
    public void PersistenceFailureRollsBackMembershipRolePresenceAndNewlyHiddenMessages()
    {
        var state = new ChatState(); var owner = User(state, "owner"); var target = User(state, "target");
        var room = state.CreateConversation(owner.Id, new("Group", [target.Id]))!;
        var old = Send(state, owner.Id, room.Id, target.Id);
        Assert.Equal(DeleteResult.Deleted, state.DeleteMessage(target.Id, old.Id, false));
        var visible = Send(state, owner.Id, room.Id, target.Id);
        Assert.True(state.SetGroupRole(owner.Id, room.Id, target.Id, "mod")); state.TouchActivity(target.Id, room.Id);
        // Deliberately unreachable localhost fixture, not the real site DB. Inject
        // only after constructing an in-memory state so the mutation's rollback runs.
        var failing = new ChatDatabase(Options.Create(new DatabaseOptions { Provider = "MySQL",
            ConnectionString = "Server=127.0.0.1;Port=1;User ID=invalid;Database=invalid;Connection Timeout=1" }));
        typeof(ChatState).GetField("_database", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(state, failing);
        Assert.ThrowsAny<Exception>(() => state.RemoveGroupMember(owner.Id, room.Id, target.Id));
        Assert.True(state.IsMember(target.Id, room.Id)); Assert.Equal("mod", state.GetGroupRole(room.Id, target.Id));
        Assert.Equal(visible.Id, Assert.Single(state.GetMessages(target.Id, room.Id, null)).Id);
        Assert.True(state.GetPresence(room.Id, owner.Id).Single(item => item.User.Id == target.Id).IsViewingConversation);
        var json = JsonNode.Parse(Snapshot(state))!;
        Assert.DoesNotContain(json["LeftGroups"]!.AsArray(), row => row!["ConversationId"]!.GetValue<Guid>() == room.Id && row["UserId"]!.GetValue<Guid>() == target.Id);
        Assert.Contains(json["HiddenMessages"]!.AsArray(), row => row!["UserId"]!.GetValue<Guid>() == target.Id && row["MessageId"]!.GetValue<Guid>() == old.Id);
        Assert.DoesNotContain(json["HiddenMessages"]!.AsArray(), row => row!["UserId"]!.GetValue<Guid>() == target.Id && row["MessageId"]!.GetValue<Guid>() == visible.Id);
    }

    [Fact]
    public void ExplicitCallRevocationPreventsOldQueuedAudioAfterImmediateGroupRejoin()
    {
        var state = new ChatState(); var owner = User(state, "owner"); var target = User(state, "target"); var other = User(state, "other");
        using var ownerDevice = DeviceIdentity.Create(); using var targetDevice = DeviceIdentity.Create(); using var otherDevice = DeviceIdentity.Create();
        foreach (var pair in new[] { (owner, ownerDevice), (target, targetDevice), (other, otherDevice) })
            Assert.True(state.RegisterDevice(pair.Item1.Id, new("fixture", pair.Item2.ExportEncryptionPublicKey(), pair.Item2.ExportSigningPublicKey())));
        var room = state.CreateConversation(owner.Id, new("Group", [target.Id, other.Id]))!;
        var token = InviteToken(state, owner.Id, room.Id); var calls = new CallRegistry(state);
        var call = calls.Start(owner.Id, new(room.Id, [target.Id, other.Id]));
        using var a = new CallIdentity(call.Id, owner.Id, ownerDevice.SigningKey);
        using var b = new CallIdentity(call.Id, target.Id, targetDevice.SigningKey);
        using var c = new CallIdentity(call.Id, other.Id, otherDevice.SigningKey);
        calls.Join(call.Id, owner.Id, a.Join); calls.Join(call.Id, target.Id, b.Join); calls.Join(call.Id, other.Id, c.Join);
        using var ab = a.Connect(target.Id, b.Join, targetDevice.ExportSigningPublicKey());
        using var bc = b.Connect(other.Id, c.Join, otherDevice.ExportSigningPublicKey());
        calls.Send(call.Id, owner.Id, [ab.Encrypt(new byte[3200])]);
        calls.Send(call.Id, target.Id, [bc.Encrypt(new byte[3200])]);
        Assert.Equal(GroupMemberRemovalStatus.Removed, state.RemoveGroupMember(owner.Id, room.Id, target.Id));
        calls.RevokeConversationMember(room.Id, target.Id);
        Assert.Equal(GroupInviteStatus.Success, state.JoinGroupInvite(target.Id, token, out _));
        Assert.Equal(403, Assert.Throws<CallFailure>(() => calls.Receive(call.Id, target.Id)).Status);
        Assert.Equal(403, Assert.Throws<CallFailure>(() => calls.Join(call.Id, target.Id, b.Join)).Status);
        Assert.Equal(403, Assert.Throws<CallFailure>(() => calls.Send(call.Id, target.Id, [bc.Encrypt(new byte[3200])])).Status);
        Assert.Empty(calls.Receive(call.Id, other.Id));
        Assert.DoesNotContain(calls.Get(call.Id, owner.Id).Peers, peer => peer.User.Id == target.Id);
    }

    [Fact]
    public async Task HttpRemovalRequiresAuthAndCurrentAuthorityAndReturnsSafeErrors()
    {
        await using var fixture = await HttpFixture.Start();
        var path = $"api/conversations/{fixture.Room.Id}/members/{fixture.Member.Id}/remove";
        using (var response = await fixture.Http.PostAsync(path, null)) { Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode); NoStore(response); }
        fixture.As(fixture.Member);
        using (var response = await fixture.Http.PostAsync(path, null)) Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        fixture.State.SetGroupRole(fixture.Owner.Id, fixture.Room.Id, fixture.Member.Id, "mod");
        using (var response = await fixture.Http.PostAsync(path, null)) Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        fixture.As(fixture.Owner);
        using (var response = await fixture.Http.PostAsync($"api/conversations/{fixture.Room.Id}/members/{fixture.Owner.Id}/remove", null)) Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using (var response = await fixture.Http.PostAsync($"api/conversations/{fixture.Room.Id}/members/{Guid.NewGuid()}/remove", null)) Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var direct = fixture.State.GetOrCreateDirect(fixture.Owner.Id, fixture.Member.Id)!;
        using (var response = await fixture.Http.PostAsync($"api/conversations/{direct.Id}/members/{fixture.Member.Id}/remove", null)) Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var ownerDevice = DeviceIdentity.Create(); using var memberDevice = DeviceIdentity.Create();
        fixture.State.RegisterDevice(fixture.Owner.Id, new("fixture", ownerDevice.ExportEncryptionPublicKey(), ownerDevice.ExportSigningPublicKey()));
        fixture.State.RegisterDevice(fixture.Member.Id, new("fixture", memberDevice.ExportEncryptionPublicKey(), memberDevice.ExportSigningPublicKey()));
        var call = fixture.Calls.Start(fixture.Owner.Id, new(fixture.Room.Id, [fixture.Member.Id]));
        using var caller = new CallIdentity(call.Id, fixture.Owner.Id, ownerDevice.SigningKey);
        using var recipient = new CallIdentity(call.Id, fixture.Member.Id, memberDevice.SigningKey);
        fixture.Calls.Join(call.Id, fixture.Owner.Id, caller.Join); fixture.Calls.Join(call.Id, fixture.Member.Id, recipient.Join);
        using var cipher = caller.Connect(fixture.Member.Id, recipient.Join, memberDevice.ExportSigningPublicKey());
        fixture.Calls.Send(call.Id, fixture.Owner.Id, [cipher.Encrypt(new byte[3200])]);
        using (var response = await fixture.Http.PostAsync(path, null)) { Assert.Equal(HttpStatusCode.NoContent, response.StatusCode); NoStore(response); }
        fixture.As(fixture.Member);
        foreach (var resource in new[] { "messages", "members", "presence" })
        {
            using var response = await fixture.Http.GetAsync($"api/conversations/{fixture.Room.Id}/{resource}");
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
        fixture.As(fixture.Owner);
        using (var response = await fixture.Http.PostAsync(path, null)) Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(GroupInviteStatus.Success, fixture.State.JoinGroupInvite(fixture.Member.Id,
            InviteToken(fixture.State, fixture.Owner.Id, fixture.Room.Id), out _));
        // The real HTTP mapper must revoke the old call before replying, not rely
        // only on a later membership Sweep which an immediate rejoin could evade.
        Assert.Empty(fixture.Calls.List(fixture.Member.Id));
        Assert.Throws<CallFailure>(() => fixture.Calls.Receive(call.Id, fixture.Member.Id));
    }

    [Fact]
    public async Task DemotedSiteAdminCannotUseExistingSessionToRemoveGroupMembers()
    {
        await using var fixture = await HttpFixture.Start();
        var site = User(fixture.State, "site", "admin"); fixture.As(site);
        var path = $"api/conversations/{fixture.Room.Id}/members/{fixture.Owner.Id}/remove";
        fixture.State.UpsertSiteUser(site.Id, site.DisplayName, site.Email, "user");
        using var denied = await fixture.Http.PostAsync(path, null);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode); Assert.True(fixture.State.IsMember(fixture.Owner.Id, fixture.Room.Id));
        fixture.State.UpsertSiteUser(site.Id, site.DisplayName, site.Email, "admin");
        using var removed = await fixture.Http.PostAsync(path, null);
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode); Assert.False(fixture.State.IsMember(site.Id, fixture.Room.Id));
    }

    [Theory]
    [InlineData("/api/conversations/aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa/members/bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb/remove", "POST", true)]
    [InlineData("/API/CONVERSATIONS/aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa/MEMBERS/bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb/REMOVE/", "post", true)]
    [InlineData("/api/conversations/aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa/members/bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb/remove", "GET", false)]
    [InlineData("/api/conversations/not-a-guid/members/bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb/remove", "POST", false)]
    [InlineData("/api/conversations/aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa/members/not-a-guid/remove", "POST", false)]
    [InlineData("/api/conversations/aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa/members/bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb/remove/extra", "POST", false)]
    public void MemberRemovalPrivilegeRefreshMatchesOnlyTheRealRoute(string path, string method, bool expected)
    {
        var predicate = typeof(ChatAuthenticationMiddleware).GetMethod("RequiresFreshGroupAuthority", BindingFlags.Static | BindingFlags.NonPublic)!;
        Assert.Equal(expected, (bool)predicate.Invoke(null, [new PathString(path), method])!);
    }

    private static void NoStore(HttpResponseMessage response) => Assert.True(response.Headers.CacheControl?.NoStore);

    private sealed class HttpFixture(WebApplication app, ChatState state, HttpClient http) : IAsyncDisposable
    {
        internal ChatState State { get; } = state;
        internal CallRegistry Calls => app.Services.GetRequiredService<CallRegistry>();
        internal HttpClient Http { get; } = http;
        internal ChatUser Owner { get; } = User(state, "owner");
        internal ChatUser Member { get; } = User(state, "member");
        internal ConversationSummary Room { get; private set; } = null!;
        internal static async Task<HttpFixture> Start()
        {
            var state = new ChatState(); var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
            builder.Services.AddSingleton(state); builder.Services.AddSingleton<CallRegistry>();
            builder.Services.AddSingleton(new ChatDatabase(Options.Create(new DatabaseOptions())));
            builder.Services.AddRateLimiter(options => options.AddPolicy("group-invites", _ => RateLimitPartition.GetNoLimiter("fixture")));
            var app = builder.Build(); app.UseMiddleware<ChatAuthenticationMiddleware>(); app.UseRateLimiter();
            app.MapGroupMemberEndpoints(); app.MapConversationEndpoints(); await app.StartAsync();
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            var fixture = new HttpFixture(app, state, new HttpClient { BaseAddress = new Uri(address) });
            fixture.Room = state.CreateConversation(fixture.Owner.Id, new("Private", [fixture.Member.Id]))!;
            return fixture;
        }
        internal void As(ChatUser user) => Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", State.CreateSession(user.Id));
        public async ValueTask DisposeAsync() { Http.Dispose(); await app.StopAsync(); await app.DisposeAsync(); }
    }
}
