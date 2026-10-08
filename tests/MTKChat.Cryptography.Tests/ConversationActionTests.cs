using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MTKChat.Contracts;
using MTKChat.Server.Configuration;
using MTKChat.Server.Services;
using Xunit;

namespace MTKChat.Cryptography.Tests;

public sealed class ConversationActionTests
{
    private static readonly Guid Lounge = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static ChatUser User(ChatState state, string name, string role = "user") =>
        state.UpsertSiteUser(Guid.NewGuid(), name, name + "@example.test", role);

    private static StoredMessage Send(ChatState state, Guid sender, Guid room, IReadOnlyList<Guid> recipients,
        DateTimeOffset? clientAt = null, DateTimeOffset? expiresAt = null)
    {
        // These state tests use inert envelopes; signature verification has separate crypto tests.
        Assert.True(state.AddMessage(sender, new(Guid.NewGuid(), room, "text", clientAt ?? DateTimeOffset.UtcNow, expiresAt,
            recipients.Select(id => new EncryptedPayload("test", "", "", "cipher", "", "", id)).ToArray(), null), out var stored));
        return stored!;
    }

    private sealed class ControlledClock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public void EveryoneDeleteIsSenderOnlyAndStopsAtAuthoritativeFifteenMinuteBoundary()
    {
        var clock = new ControlledClock(DateTimeOffset.UtcNow);
        var state = new ChatState(timeProvider: clock); var a = User(state, "A"); var b = User(state, "B", "admin");
        var room = state.GetOrCreateDirect(a.Id, b.Id)!;
        var forged = Send(state, a.Id, room.Id, [a.Id, b.Id], clock.Now.AddDays(10));
        Assert.Equal(clock.Now.AddMinutes(15), forged.DeleteForEveryoneUntil);
        Assert.Equal(DeleteResult.Forbidden, state.DeleteMessage(b.Id, forged.Id, true));
        clock.Now += TimeSpan.FromMinutes(15);
        Assert.Equal(DeleteResult.WindowExpired, state.DeleteMessage(a.Id, forged.Id, true));
        Assert.False(state.GetMessages(b.Id, room.Id, null).Single().DeletedForEveryone);
        Assert.Equal(DeleteResult.Deleted, state.DeleteMessage(a.Id, forged.Id, false));
        Assert.Empty(state.GetMessages(a.Id, room.Id, null));
        Assert.Single(state.GetMessages(b.Id, room.Id, null));
        var allowed = Send(state, a.Id, room.Id, [a.Id, b.Id], clock.Now.AddDays(-10));
        clock.Now += TimeSpan.FromMinutes(15) - TimeSpan.FromTicks(1);
        Assert.Equal(DeleteResult.Deleted, state.DeleteMessage(a.Id, allowed.Id, true));
        Assert.True(state.GetMessages(b.Id, room.Id, null).Single(m => m.Id == allowed.Id).DeletedForEveryone);
        clock.Now += TimeSpan.FromHours(1);
        Assert.Equal(DeleteResult.Deleted, state.DeleteMessage(a.Id, allowed.Id, true)); // Safe idempotent retry.
    }

    [Fact]
    public void ClearMessagesAndRemoveChatOnlyAffectRequesterAndFutureIncomingRestoresList()
    {
        var state = new ChatState(); var a = User(state, "A"); var b = User(state, "B", "admin");
        var outside = User(state, "Outside", "admin"); var room = state.GetOrCreateDirect(a.Id, b.Id)!;
        var old = Send(state, a.Id, room.Id, [a.Id, b.Id]);
        Assert.Equal(-1, state.ClearHistory(outside.Id, room.Id, false));
        Assert.Equal(-2, state.ClearHistory(b.Id, room.Id, true));
        Assert.Equal(DeleteResult.Forbidden, state.DeleteMessage(outside.Id, old.Id, true));
        Assert.Equal(1, state.ClearHistory(b.Id, room.Id, false));
        Assert.Empty(state.GetMessages(b.Id, room.Id, null));
        Assert.Single(state.GetMessages(a.Id, room.Id, null));
        Assert.Equal(0, state.ClearHistory(b.Id, room.Id, false));
        Assert.Contains(state.GetConversations(b.Id), c => c.Id == room.Id); // Clear is not remove chat.
        Assert.True(state.RemoveConversationForMe(b.Id, room.Id));
        Assert.True(state.IsMember(b.Id, room.Id)); Assert.DoesNotContain(state.GetConversations(b.Id), c => c.Id == room.Id);
        Assert.Contains(state.GetConversations(a.Id), c => c.Id == room.Id);
        Send(state, b.Id, room.Id, [b.Id, a.Id]); // Own send is not an incoming message.
        Assert.DoesNotContain(state.GetConversations(b.Id), c => c.Id == room.Id);
        Send(state, a.Id, room.Id, [a.Id]); // No envelope addressed to B.
        Assert.DoesNotContain(state.GetConversations(b.Id), c => c.Id == room.Id);
        state.SetBlocked(b.Id, a.Id, true);
        // Direct message sending is itself blocked; neither a blocked nor invalid send restores a chat.
        Assert.False(state.AddMessage(a.Id, new(Guid.NewGuid(), room.Id, "text", DateTimeOffset.UtcNow, null,
            [new("test", "", "", "cipher", "", "", b.Id)], null), out _));
        Assert.DoesNotContain(state.GetConversations(b.Id), c => c.Id == room.Id);
        state.SetBlocked(b.Id, a.Id, false);
        var incoming = Send(state, a.Id, room.Id, [a.Id, b.Id]);
        Assert.Contains(state.GetConversations(b.Id), c => c.Id == room.Id);
        Assert.DoesNotContain(state.GetMessages(b.Id, room.Id, null), m => m.Id == old.Id);
        Assert.Contains(state.GetMessages(b.Id, room.Id, null), m => m.Id == incoming.Id);
        Assert.True(state.RemoveConversationForMe(b.Id, room.Id));
        Assert.Equal(room.Id, state.GetOrCreateDirect(b.Id, a.Id)!.Id); // Explicit reopen by initiating a conversation.
        Assert.Null(state.ReopenConversation(outside.Id, room.Id));
    }

    [Fact]
    public void GroupLeaveRevokesAccessRequiresAdminTransferAndLoungeSyncCannotReAdd()
    {
        var state = new ChatState(); var owner = User(state, "Owner"); var member = User(state, "Member");
        var outside = User(state, "Outside"); var room = state.CreateConversation(owner.Id, new("Grup", [member.Id]))!;
        JoinLounge(state, owner);
        Send(state, owner.Id, room.Id, [owner.Id, member.Id]);
        state.TouchActivity(owner.Id, room.Id);
        Assert.Equal(LeaveGroupResult.AdminTransferRequired, state.LeaveGroup(owner.Id, room.Id));
        Assert.True(state.IsMember(owner.Id, room.Id));
        Assert.Equal(LeaveGroupResult.Forbidden, state.LeaveGroup(outside.Id, room.Id));
        Assert.True(state.SetGroupRole(owner.Id, room.Id, member.Id, "admin"));
        Assert.Equal(LeaveGroupResult.Left, state.LeaveGroup(owner.Id, room.Id));
        Assert.False(state.IsMember(owner.Id, room.Id)); Assert.Equal("user", state.GetGroupRole(room.Id, owner.Id));
        Assert.Empty(state.GetMessages(owner.Id, room.Id, null)); Assert.Null(state.ReopenConversation(owner.Id, room.Id));
        Assert.False(state.GetPresence(room.Id).Single(p => p.User.Id == owner.Id).IsViewingConversation);
        Assert.Equal(LeaveGroupResult.NotAGroup, state.LeaveGroup(member.Id, state.GetOrCreateDirect(member.Id, outside.Id)!.Id));
        Assert.Equal(LeaveGroupResult.Left, state.LeaveGroup(owner.Id, Lounge));
        var accounts = new[] { new SiteAccount(1, owner.Id, "Owner", owner.Email, "user", true),
            new SiteAccount(2, member.Id, "Member", member.Email, "user", true),
            new SiteAccount(3, outside.Id, "Outside", outside.Email, "user", true) };
        state.SynchronizeSiteUsers(accounts);
        state.UpsertSiteUser(owner.Id, owner.DisplayName, owner.Email, owner.Role); // Login upsert must also honor leave.
        Assert.False(state.IsMember(owner.Id, Lounge));
        Assert.Single(state.GetMessages(member.Id, room.Id, null));
    }

    [Fact]
    public void RemovedGroupDoesNotReappearForBlockedExpiredOrDuplicateIncomingEnvelopes()
    {
        var state = new ChatState(); var a = User(state, "A"); var b = User(state, "B"); var c = User(state, "C");
        var room = state.CreateConversation(a.Id, new("Grup", [b.Id, c.Id]))!;
        var before = Send(state, a.Id, room.Id, [a.Id, b.Id, c.Id]);
        Assert.True(state.RemoveConversationForMe(b.Id, room.Id));
        Assert.Equal("user", state.GetGroupRole(room.Id, b.Id)); Assert.True(state.IsMember(b.Id, room.Id));
        Assert.True(state.AddMessage(a.Id, new(before.ClientMessageId, room.Id, "text", before.CreatedAt, null,
            before.Payloads, null), out var duplicate));
        Assert.Equal(before.Id, duplicate!.Id);
        Assert.DoesNotContain(state.GetConversations(b.Id), r => r.Id == room.Id);
        Send(state, a.Id, room.Id, [a.Id, b.Id, c.Id], expiresAt: DateTimeOffset.UtcNow.AddSeconds(-1));
        Assert.DoesNotContain(state.GetConversations(b.Id), r => r.Id == room.Id);
        state.SetBlocked(b.Id, a.Id, true);
        Send(state, a.Id, room.Id, [a.Id, b.Id, c.Id]); // Three-person groups can send to others despite B's local block.
        Assert.DoesNotContain(state.GetConversations(b.Id), r => r.Id == room.Id);
        var incoming = Send(state, c.Id, room.Id, [a.Id, b.Id, c.Id]);
        Assert.Contains(state.GetConversations(b.Id), r => r.Id == room.Id);
        Assert.Equal(incoming.Id, Assert.Single(state.GetMessages(b.Id, room.Id, null)).Id);
        Assert.Equal(3, state.GetMessages(c.Id, room.Id, null).Count); // Only expired envelopes are absent for C.
    }

    [Fact]
    public void PersonalRemovalLeaveAndDeletionDeadlinePersistWithoutGrantingLegacyWindow()
    {
        var clock = new ControlledClock(DateTimeOffset.UtcNow); var state = new ChatState(timeProvider: clock);
        var a = User(state, "A"); var b = User(state, "B"); var room = state.GetOrCreateDirect(a.Id, b.Id)!;
        JoinLounge(state, b);
        var message = Send(state, a.Id, room.Id, [a.Id, b.Id]);
        Assert.True(state.RemoveConversationForMe(b.Id, room.Id)); Assert.Equal(LeaveGroupResult.Left, state.LeaveGroup(b.Id, Lounge));
        var json = (string)typeof(ChatState).GetMethod("SerializeSnapshotUnsafe", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(state, [])!;
        var restored = new ChatState(timeProvider: clock); UserWithId(restored, a); UserWithId(restored, b);
        var restore = typeof(ChatState).GetMethod("RestoreSnapshot", BindingFlags.NonPublic | BindingFlags.Instance)!;
        restore.Invoke(restored, [json]);
        Assert.DoesNotContain(restored.GetConversations(b.Id), c => c.Id == room.Id);
        Assert.Empty(restored.GetMessages(b.Id, room.Id, null));
        restored.UpsertSiteUser(b.Id, b.DisplayName, b.Email, b.Role); Assert.False(restored.IsMember(b.Id, Lounge));
        Assert.Equal(message.DeleteForEveryoneUntil, restored.GetMessages(a.Id, room.Id, null).Single().DeleteForEveryoneUntil);
        clock.Now = message.DeleteForEveryoneUntil!.Value;
        Assert.Equal(DeleteResult.WindowExpired, restored.DeleteMessage(a.Id, message.Id, true));
        var legacy = System.Text.Json.Nodes.JsonNode.Parse(json)!;
        foreach (var node in legacy["Messages"]!.AsArray()) node!.AsObject().Remove("DeleteForEveryoneUntil");
        legacy.AsObject().Remove("HiddenConversations"); legacy.AsObject().Remove("LeftGroups");
        restore.Invoke(restored, [legacy.ToJsonString()]);
        clock.Now = DateTimeOffset.UtcNow.AddDays(-1);
        Assert.Equal(DeleteResult.WindowExpired, restored.DeleteMessage(a.Id, message.Id, true));
    }

    [Fact]
    public async Task HttpActionsEnforcePersonalScopeMembershipAndDeletionDeadline()
    {
        var clock = new ControlledClock(DateTimeOffset.UtcNow); var state = new ChatState(timeProvider: clock);
        var a = User(state, "A"); var b = User(state, "B", "admin"); var outside = User(state, "Outside", "admin");
        var room = state.CreateConversation(a.Id, new("Grup", [b.Id]))!;
        var message = Send(state, a.Id, room.Id, [a.Id, b.Id]);
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(state); builder.Services.AddSingleton(new ChatDatabase(Options.Create(new DatabaseOptions())));
        await using var app = builder.Build(); app.UseMiddleware<ChatAuthenticationMiddleware>(); app.MapConversationEndpoints();
        await app.StartAsync();
        using var http = new HttpClient { BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
        void As(ChatUser user) => http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", state.CreateSession(user.Id));
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.DeleteAsync($"api/conversations/{room.Id}")).StatusCode);
        As(outside);
        Assert.Equal(HttpStatusCode.Forbidden, (await http.DeleteAsync($"api/conversations/{room.Id}/messages")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await http.PostAsync($"api/conversations/{room.Id}/leave", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await http.DeleteAsync($"api/conversations/{room.Id}")).StatusCode);
        As(b);
        Assert.Equal(HttpStatusCode.Forbidden, (await http.DeleteAsync($"api/conversations/{room.Id}/messages?forEveryone=true")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await http.DeleteAsync($"api/messages/{message.Id}?forEveryone=true")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.DeleteAsync($"api/conversations/{room.Id}/messages")).StatusCode);
        Assert.Single(state.GetMessages(a.Id, room.Id, null)); Assert.Empty(state.GetMessages(b.Id, room.Id, null));
        Assert.Equal(HttpStatusCode.NoContent, (await http.DeleteAsync($"api/conversations/{room.Id}")).StatusCode);
        Assert.DoesNotContain((await http.GetFromJsonAsync<ConversationSummary[]>("api/conversations"))!, c => c.Id == room.Id);
        Assert.Equal(HttpStatusCode.OK, (await http.PostAsync($"api/conversations/{room.Id}/reopen", null)).StatusCode);
        As(a); clock.Now += TimeSpan.FromMinutes(15);
        using var tooLate = await http.DeleteAsync($"api/messages/{message.Id}?forEveryone=true");
        Assert.Equal(HttpStatusCode.Conflict, tooLate.StatusCode);
        Assert.Equal("delete_window_expired", (await tooLate.Content.ReadFromJsonAsync<ApiError>())!.Code);
        Assert.Equal(HttpStatusCode.NoContent, (await http.DeleteAsync($"api/messages/{message.Id}?forEveryone=false")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await http.PostAsync($"api/conversations/{room.Id}/leave", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync($"api/conversations/{room.Id}/messages")).StatusCode);
        await app.StopAsync();
    }

    private static void UserWithId(ChatState state, ChatUser user) => state.UpsertSiteUser(user.Id, user.DisplayName, user.Email, user.Role);

    private static void JoinLounge(ChatState state, ChatUser user)
    {
        // Membership is now explicit. This fixture exercises leaving/snapshot behavior,
        // not the removed implicit registration-to-Lounge behavior.
        var admin = User(state, "Invite manager", "admin");
        Assert.Equal(GroupInviteStatus.Success, state.CreateOrRotateGroupInvite(admin.Id, Lounge, out var invite));
        Assert.Equal(GroupInviteStatus.Success, state.JoinGroupInvite(user.Id, new Uri(invite!.InviteUrl).Fragment[1..], out _));
    }
}
