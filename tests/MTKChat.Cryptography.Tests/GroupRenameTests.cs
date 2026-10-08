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

public sealed class GroupRenameTests
{
    private static ChatUser User(ChatState state, string name, string role = "user") =>
        state.UpsertSiteUser(Guid.NewGuid(), name, name + "@rename.invalid", role);

    [Fact]
    public void GroupAdminAndSiteAdminCanRenameWithoutGrantingMembership()
    {
        var state = new ChatState(); var owner = User(state, "Owner"); var member = User(state, "Member");
        var outsider = User(state, "Outsider"); var site = User(state, "Site", "admin");
        var room = state.CreateConversation(owner.Id, new("Original", [member.Id]))!;
        Assert.Equal(RenameGroupResult.Forbidden, state.RenameGroup(member.Id, room.Id, "Denied", out _));
        Assert.True(state.SetGroupRole(owner.Id, room.Id, member.Id, "mod"));
        Assert.Equal(RenameGroupResult.Forbidden, state.RenameGroup(member.Id, room.Id, "Denied", out _));
        Assert.Equal(RenameGroupResult.Forbidden, state.RenameGroup(outsider.Id, room.Id, "Denied", out _));
        Assert.Equal(RenameGroupResult.Renamed, state.RenameGroup(owner.Id, room.Id, "  Yeni grup ✨  ", out var updated));
        Assert.Equal("Yeni grup ✨", updated!.Title);
        Assert.Equal(RenameGroupResult.Renamed, state.RenameGroup(site.Id, room.Id, "Site güncellemesi", out _));
        Assert.False(state.IsMember(site.Id, room.Id));
        Assert.DoesNotContain(state.GetConversations(site.Id), c => c.Id == room.Id);
        Assert.Equal("Site güncellemesi", state.GetConversations(member.Id).Single(c => c.Id == room.Id).Title);
        state.UpsertSiteUser(site.Id, site.DisplayName, site.Email, "user");
        Assert.Equal(RenameGroupResult.Forbidden, state.RenameGroup(site.Id, room.Id, "Revoked", out _));
        Assert.True(state.SetGroupRole(owner.Id, room.Id, member.Id, "admin"));
        Assert.Equal(RenameGroupResult.Renamed, state.RenameGroup(member.Id, room.Id, "Second admin", out _));
        Assert.True(state.SetGroupRole(owner.Id, room.Id, member.Id, "user"));
        Assert.Equal(RenameGroupResult.Forbidden, state.RenameGroup(member.Id, room.Id, "Revoked", out _));
    }

    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData("   ")] [InlineData("Line\nBreak")]
    [InlineData("Tab\tName")] [InlineData("\nName")]
    public void InvalidNamesNeverModifyGroup(string? title)
    {
        var state = new ChatState(); var a = User(state, "A"); var b = User(state, "B");
        var room = state.CreateConversation(a.Id, new("Original", [b.Id]))!;
        Assert.Equal(RenameGroupResult.InvalidTitle, state.RenameGroup(a.Id, room.Id, title, out var result));
        Assert.Null(result);
        Assert.Equal("Original", state.GetConversations(a.Id).Single(c => c.Id == room.Id).Title);
        Assert.Null(state.CreateConversation(a.Id, new(title!, [b.Id])));
    }

    [Fact]
    public void BoundaryDirectBotAndBanRulesAreEnforced()
    {
        var state = new ChatState(); var a = User(state, "A"); var b = User(state, "B"); var site = User(state, "Site", "admin");
        var room = state.CreateConversation(a.Id, new("Original", [b.Id]))!;
        Assert.Equal(RenameGroupResult.Renamed, state.RenameGroup(a.Id, room.Id, new string('X', 80), out _));
        Assert.Equal(RenameGroupResult.InvalidTitle, state.RenameGroup(a.Id, room.Id, new string('X', 81), out _));
        var direct = state.GetOrCreateDirect(a.Id, b.Id)!;
        Assert.Equal(RenameGroupResult.NotAGroup, state.RenameGroup(a.Id, direct.Id, "No", out _));
        Assert.Equal(RenameGroupResult.NotAGroup, state.RenameGroup(site.Id, direct.Id, "No", out _));
        Assert.Equal(RenameGroupResult.NotFound, state.RenameGroup(a.Id, Guid.NewGuid(), "No", out _));
        Assert.Equal(RenameGroupResult.Forbidden, state.RenameGroup(Guid.Parse("22222222-2222-2222-2222-222222222222"), room.Id, "Bot", out _));
        Assert.True(state.ModerateUser(a.Id, "ban", null));
        Assert.Equal(RenameGroupResult.Forbidden, state.RenameGroup(a.Id, room.Id, "Banned", out _));
    }

    [Fact]
    public void RenamePreservesMessagesReceiptsRolesAndRestoresFromSnapshot()
    {
        var state = new ChatState(); var a = User(state, "A"); var b = User(state, "B");
        var room = state.CreateConversation(a.Id, new("Original", [b.Id]))!;
        var payload = new EncryptedPayload("test", "test", "test", "ciphertext", "test", "test", b.Id);
        Assert.True(state.AddMessage(a.Id, new(Guid.NewGuid(), room.Id, "text", DateTimeOffset.UtcNow, null, [payload], null), out var message));
        state.AcknowledgeMessages(b.Id, [message!.Id], true);
        var before = state.GetMessages(a.Id, room.Id, null).Single();
        Assert.Equal(RenameGroupResult.Renamed, state.RenameGroup(a.Id, room.Id, "Renamed", out _));
        var after = state.GetMessages(a.Id, room.Id, null).Single();
        Assert.Equal(before.Id, after.Id); Assert.Equal(before.Payloads, after.Payloads);
        Assert.Equal(before.Delivery!.Status, after.Delivery!.Status);
        var snapshot = (string)typeof(ChatState).GetMethod("SerializeSnapshotUnsafe", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(state, null)!;
        typeof(ChatState).GetMethod("RestoreSnapshot", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(state, [snapshot]);
        Assert.Equal("Renamed", state.GetConversations(a.Id).Single(c => c.Id == room.Id).Title);
        Assert.Equal("admin", state.GetGroupRole(room.Id, a.Id));
        Assert.Equal("ciphertext", state.GetMessages(b.Id, room.Id, null).Single().Payloads.Single().Ciphertext);
        Assert.Equal(before.Delivery!.Status, state.GetMessages(a.Id, room.Id, null).Single().Delivery!.Status);
    }

    [Fact]
    public async Task HttpRenameChecksAuthenticationAuthorityAndReturnsOnlyTitle()
    {
        var state = new ChatState(); var a = User(state, "A"); var b = User(state, "B"); var site = User(state, "Site", "admin");
        var room = state.CreateConversation(a.Id, new("Original", [b.Id]))!;
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(state); builder.Services.AddSingleton(new ChatDatabase(Options.Create(new DatabaseOptions())));
        await using var app = builder.Build(); app.UseMiddleware<ChatAuthenticationMiddleware>(); app.MapConversationEndpoints();
        await app.StartAsync();
        using var http = new HttpClient { BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
        async Task<HttpResponseMessage> Rename(string title) => await http.PutAsJsonAsync($"api/conversations/{room.Id}/title", new RenameConversationRequest(title));
        using (var response = await Rename("No session")) Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", state.CreateSession(b.Id));
        using (var response = await Rename("Member")) Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", state.CreateSession(a.Id));
        using (var response = await Rename(" ")) Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using (var response = await Rename("Updated"))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(new GroupTitleResult(room.Id, "Updated"), await response.Content.ReadFromJsonAsync<GroupTitleResult>());
        }
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", state.CreateSession(site.Id));
        using (var response = await Rename("Site"))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var json = await response.Content.ReadAsStringAsync(); Assert.DoesNotContain("participants", json); Assert.DoesNotContain("payloads", json);
        }
        using (var response = await http.GetAsync($"api/conversations/{room.Id}/messages")) Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await app.StopAsync();
    }
}
