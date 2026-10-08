using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
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

public sealed class PrivacyGroupTests
{
    private static ChatUser User(ChatState s, string name, string role = "user") => s.UpsertSiteUser(Guid.NewGuid(), name, name + "@test.invalid", role);

    [Theory]
    [InlineData("groupAdmin")]
    [InlineData("mod")]
    [InlineData("siteAdmin")]
    public void ChatBanRevokesManagementOnlyInThatRoomWithoutErasingTheStoredRole(string authority)
    {
        var state = new ChatState(); var owner = User(state, "Owner"); var mod = User(state, "Mod");
        var member = User(state, "Member"); var site = User(state, "Site", "admin");
        var room = state.CreateConversation(owner.Id, new("Restricted", [mod.Id, member.Id, site.Id]))!;
        var other = state.CreateConversation(owner.Id, new("Unaffected", [mod.Id, member.Id, site.Id]))!;
        Assert.True(state.SetGroupRole(owner.Id, room.Id, mod.Id, "mod"));
        Assert.True(state.SetGroupRole(owner.Id, other.Id, mod.Id, "mod"));
        var actor = authority == "siteAdmin" ? site : authority == "mod" ? mod : owner;
        var role = authority == "mod" ? "mod" : "admin";
        Assert.Equal(role, state.GetGroupRole(room.Id, actor.Id));
        Assert.True(state.CanModerateGroupMember(actor.Id, room.Id, member.Id));
        Assert.Contains(state.GetManageableGroups(actor.Id), item => item.Id == room.Id);
        Assert.True(state.ModerateChatUser(room.Id, actor.Id, "ban", null));

        Assert.Equal(role, state.GetGroupRole(room.Id, actor.Id)); // Presentation keeps the stored role.
        Assert.DoesNotContain(state.GetManageableGroups(actor.Id), item => item.Id == room.Id);
        Assert.False(state.SetGroupRole(actor.Id, room.Id, member.Id, "admin"));
        Assert.False(state.CanModerateGroupMember(actor.Id, room.Id, member.Id));
        Assert.False(state.ModerateGroupUser(actor.Id, room.Id, member.Id, "mute", 60));
        Assert.Equal("user", state.GetGroupRole(room.Id, member.Id));
        Assert.False(state.GetChatModeration(room.Id, member.Id)!.IsMuted);
        Assert.Contains(state.GetManageableGroups(actor.Id), item => item.Id == other.Id);
        Assert.True(state.CanModerateGroupMember(actor.Id, other.Id, member.Id));

        Assert.True(state.ModerateChatUser(room.Id, actor.Id, "unban", null));
        Assert.Equal(role, state.GetGroupRole(room.Id, actor.Id));
        Assert.Contains(state.GetManageableGroups(actor.Id), item => item.Id == room.Id);
        Assert.True(state.ModerateGroupUser(actor.Id, room.Id, member.Id, "mute", 60));
        Assert.True(state.GetChatModeration(room.Id, member.Id)!.IsMuted);
    }

    [Fact]
    public async Task HttpChatBannedGroupAdminCannotListManageableGroupChangeRoleOrModerate()
    {
        var state = new ChatState(); var owner = User(state, "Owner"); var member = User(state, "Member");
        var room = state.CreateConversation(owner.Id, new("Restricted", [member.Id]))!;
        Assert.True(state.ModerateChatUser(room.Id, owner.Id, "ban", null));
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(state);
        builder.Services.AddSingleton(new ChatDatabase(Options.Create(new DatabaseOptions())));
        await using var app = builder.Build(); app.UseMiddleware<ChatAuthenticationMiddleware>(); app.MapConversationEndpoints();
        await app.StartAsync();
        using var http = new HttpClient { BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", state.CreateSession(owner.Id));
        var manageable = await http.GetFromJsonAsync<GroupManagementView[]>("api/groups/manageable");
        Assert.DoesNotContain(manageable!, item => item.Id == room.Id);
        using (var role = await http.PutAsJsonAsync($"api/conversations/{room.Id}/members/{member.Id}/role", new GroupRoleChangeRequest("mod")))
            Assert.Equal(HttpStatusCode.Forbidden, role.StatusCode);
        using (var moderation = await http.PutAsJsonAsync($"api/conversations/{room.Id}/members/{member.Id}/moderation", new ChatModerationRequest("mute", 60)))
            Assert.Equal(HttpStatusCode.Forbidden, moderation.StatusCode);
        Assert.Equal("user", state.GetGroupRole(room.Id, member.Id)); Assert.False(state.GetChatModeration(room.Id, member.Id)!.IsMuted);
        await app.StopAsync();
    }

    [Fact]
    public void LastSeenPrivacyHidesTimestampFromOthersButNotItsOwner()
    {
        var s = new ChatState(); var a = User(s, "A"); var b = User(s, "B");
        var room = s.GetOrCreateDirect(a.Id, b.Id)!;
        s.TouchActivity(b.Id, room.Id);
        Assert.NotNull(s.GetPresence(room.Id, a.Id).Single(p => p.User.Id == b.Id).LastSeenAt);
        s.SetPrivacy(b.Id, new(false, true));
        var hidden = s.GetPresence(room.Id, a.Id).Single(p => p.User.Id == b.Id);
        Assert.Null(hidden.LastSeenAt); Assert.True(hidden.IsOnline);
        Assert.NotNull(s.GetPresence(room.Id, b.Id).Single(p => p.User.Id == b.Id).LastSeenAt);
        Assert.Equal(new PrivacySettings(false, true), s.GetPrivacy(b.Id));
        Assert.False(s.SetPrivacy(Guid.NewGuid(), new()));
    }

    [Fact]
    public void PrivateReadClearsOwnUnreadWithoutBlueTicksAndNeverDisclosesOnReenableRetry()
    {
        var s = new ChatState(); var a = User(s, "A"); var b = User(s, "B");
        var room = s.GetOrCreateDirect(a.Id, b.Id)!;
        var payloads = new[] { a, b }.Select(u => new EncryptedPayload("test", "", "", "cipher", "", "", u.Id)).ToArray();
        s.AddMessage(a.Id, new(Guid.NewGuid(), room.Id, "text", DateTimeOffset.UtcNow, null, payloads, null), out var message);
        s.SetPrivacy(b.Id, new(true, false));
        Assert.Equal(1, s.AcknowledgeMessages(b.Id, [message!.Id], true));
        Assert.Equal(0, s.GetConversations(b.Id).Single(c => c.Id == room.Id).UnreadCount);
        var status = Assert.Single(s.GetMessages(a.Id, room.Id, null)).Delivery!;
        Assert.Equal("delivered", status.Status); Assert.Null(Assert.Single(status.Recipients).ReadAt);
        s.SetPrivacy(b.Id, new(true, true));
        Assert.Equal(0, s.AcknowledgeMessages(b.Id, [message.Id], true));
        Assert.Equal("delivered", Assert.Single(s.GetMessages(a.Id, room.Id, null)).Delivery!.Status);
        s.AddMessage(a.Id, new(Guid.NewGuid(), room.Id, "text", DateTimeOffset.UtcNow, null, payloads, null), out var next);
        s.AcknowledgeMessages(b.Id, [next!.Id], true);
        Assert.Equal("read", s.GetMessages(a.Id, room.Id, null).Single(m => m.Id == next.Id).Delivery!.Status);
    }

    [Fact]
    public void GroupRolesStayLocalAndSiteAdminsOverrideWithoutReadingNonMemberMessages()
    {
        var s = new ChatState(); var owner = User(s, "Owner"); var mod = User(s, "Mod"); var member = User(s, "User");
        var site = User(s, "Site", "admin"); var outside = User(s, "Outside");
        var group = s.CreateConversation(owner.Id, new("Team", [mod.Id, member.Id]))!;
        var other = s.CreateConversation(member.Id, new("Other", [mod.Id]))!;
        Assert.False(s.IsAdmin(owner.Id)); Assert.Equal("admin", s.GetGroupRole(group.Id, owner.Id));
        Assert.Equal("admin", s.GetGroupRole(group.Id, site.Id)); Assert.False(s.IsMember(site.Id, group.Id));
        Assert.True(s.SetGroupRole(owner.Id, group.Id, mod.Id, "mod"));
        Assert.Equal("user", s.GetGroupRole(other.Id, mod.Id));
        Assert.False(s.SetGroupRole(mod.Id, group.Id, member.Id, "admin"));
        Assert.False(s.SetGroupRole(outside.Id, group.Id, member.Id, "admin"));
        Assert.True(s.CanModerateGroupMember(mod.Id, group.Id, member.Id));
        Assert.False(s.CanModerateGroupMember(mod.Id, group.Id, owner.Id));
        Assert.True(s.SetGroupRole(owner.Id, group.Id, member.Id, "admin"));
        Assert.False(s.CanModerateGroupMember(mod.Id, group.Id, member.Id));
        Assert.False(s.CanModerateGroupMember(owner.Id, group.Id, member.Id));
        Assert.True(s.CanModerateGroupMember(site.Id, group.Id, owner.Id));
        Assert.True(s.SetGroupRole(site.Id, group.Id, member.Id, "user"));
        Assert.False(s.SetGroupRole(site.Id, group.Id, site.Id, "user"));
        Assert.Equal("user", s.GetUser(owner.Id)!.Role);
        Assert.Contains(s.GetManageableGroups(site.Id), g => g.Id == group.Id && g.IsSiteAdmin);
        Assert.Empty(s.GetMessages(site.Id, group.Id, null));
        s.UpsertSiteUser(site.Id, site.DisplayName, site.Email, "user");
        Assert.Equal("user", s.GetGroupRole(group.Id, site.Id)); Assert.Empty(s.GetManageableGroups(site.Id));
        var direct = s.GetOrCreateDirect(owner.Id, mod.Id)!;
        Assert.False(s.SetGroupRole(owner.Id, direct.Id, mod.Id, "admin"));
    }

    [Fact]
    public void PrivacyAndGroupRolesRestoreAlongsideLegacySnapshotFields()
    {
        var s = new ChatState(); var a = User(s, "A"); var b = User(s, "B"); var room = s.CreateConversation(a.Id, new("Team", [b.Id]))!;
        var at = DateTimeOffset.UtcNow.AddHours(-2);
        var snapshot = new { Version = 1, Conversations = new[] { new { Id = room.Id, Title = room.Title,
            Members = new[] { a.Id, b.Id }, Messages = Array.Empty<Guid>(), Kind = "group" } },
            Messages = Array.Empty<object>(), Devices = Array.Empty<object>(), HiddenMessages = Array.Empty<object>(),
            BannedUsers = Array.Empty<Guid>(), MutedUsers = Array.Empty<object>(), ChatMutedUsers = Array.Empty<object>(),
            ChatBannedUsers = Array.Empty<object>(), Blocks = Array.Empty<object>(),
            Privacy = new[] { new { UserId = b.Id, Settings = new PrivacySettings(false, false) } },
            GroupRoles = new[] { new { ConversationId = room.Id, UserId = a.Id, Role = "admin" }, new { ConversationId = room.Id, UserId = b.Id, Role = "mod" } },
            LastSeen = new[] { new { UserId = b.Id, SeenAt = at } } };
        typeof(ChatState).GetMethod("RestoreSnapshot", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(s, [JsonSerializer.Serialize(snapshot)]);
        Assert.Equal("mod", s.GetGroupRole(room.Id, b.Id)); Assert.Equal(new PrivacySettings(false, false), s.GetPrivacy(b.Id));
        Assert.Null(s.GetPresence(room.Id, a.Id).Single(p => p.User.Id == b.Id).LastSeenAt);
        Assert.Equal(at, s.GetPresence(room.Id, b.Id).Single(p => p.User.Id == b.Id).LastSeenAt);
    }

    [Fact]
    public async Task HttpGroupManagerCannotAccessGlobalAdminAndPrivacyUsesSessionOwner()
    {
        var s = new ChatState(); var owner = User(s, "Owner"); var member = User(s, "Member"); var site = User(s, "Site", "admin");
        var room = s.CreateConversation(owner.Id, new("Team", [member.Id]))!;
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(s); builder.Services.AddSingleton(new ChatDatabase(Options.Create(new DatabaseOptions())));
        builder.Services.AddRateLimiter(o => o.AddPolicy("profile-photo", _ => System.Threading.RateLimiting.RateLimitPartition.GetNoLimiter("test")));
        await using var app = builder.Build(); app.UseMiddleware<ChatAuthenticationMiddleware>(); app.UseRateLimiter();
        app.MapConversationEndpoints(); app.MapProfileEndpoints(); app.MapGet("/api/admin/access", () => Microsoft.AspNetCore.Http.Results.NoContent());
        await app.StartAsync();
        using var http = new HttpClient { BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
        void As(ChatUser u) => http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", s.CreateSession(u.Id));
        As(owner);
        Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync("api/admin/access")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await http.PutAsJsonAsync($"api/conversations/{room.Id}/members/{member.Id}/role", new GroupRoleChangeRequest("mod"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.PutAsJsonAsync($"api/profile/privacy?userId={member.Id}", new PrivacySettings(false, false))).StatusCode);
        Assert.Equal(new PrivacySettings(), s.GetPrivacy(member.Id));
        Assert.Equal(new PrivacySettings(false, false), await http.GetFromJsonAsync<PrivacySettings>("api/profile/privacy"));
        As(member);
        Assert.Equal(HttpStatusCode.Forbidden, (await http.PutAsJsonAsync($"api/conversations/{room.Id}/members/{owner.Id}/role", new GroupRoleChangeRequest("user"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync("api/admin/access")).StatusCode);
        As(site);
        Assert.Equal(HttpStatusCode.NoContent, (await http.GetAsync("api/admin/access")).StatusCode);
        Assert.Contains((await http.GetFromJsonAsync<GroupManagementView[]>("api/groups/manageable"))!, g => g.Id == room.Id);
        Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync($"api/conversations/{room.Id}/messages")).StatusCode);
        await app.StopAsync();
    }
}
