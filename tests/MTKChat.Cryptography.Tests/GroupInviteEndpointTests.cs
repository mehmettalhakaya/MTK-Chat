using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Text;
using System.Text.Json;
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
using MTKChat.Server.Configuration;
using MTKChat.Server.Services;
using Xunit;

namespace MTKChat.Cryptography.Tests;

public sealed class GroupInviteEndpointTests
{
    [Fact]
    public async Task ManagedInvitesListWithoutRotatingUpdateAndDeleteOnlySelectedEntry()
    {
        await using var fixture = await Fixture.Start();
        fixture.As(fixture.Owner);
        var path = $"api/conversations/{fixture.Room.Id}/invites";
        async Task<GroupInviteEntry> Create(int duration)
        {
            using var response = await fixture.Http.PostAsJsonAsync(path, new CreateGroupInviteRequest(duration));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoStore);
            return (await response.Content.ReadFromJsonAsync<GroupInviteEntry>())!;
        }
        var first = await Create(60); var second = await Create(1440);
        for (var index = 0; index < 2; index++)
        {
            using var response = await fixture.Http.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.True(response.Headers.CacheControl?.NoStore);
            var entries = (await response.Content.ReadFromJsonAsync<GroupInviteEntry[]>())!;
            Assert.Equal(2, entries.Length);
            Assert.Equal(first, entries.Single(item => item.Id == first.Id));
            Assert.Equal(second, entries.Single(item => item.Id == second.Id));
        }
        using (var response = await fixture.Http.PutAsJsonAsync(path + "/" + first.Id, new ChangeGroupInviteDurationRequest(2880)))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var changed = (await response.Content.ReadFromJsonAsync<GroupInviteEntry>())!;
            Assert.Equal(first.InviteUrl, changed.InviteUrl); Assert.Equal(first.CreatedAt, changed.CreatedAt);
            Assert.True(changed.ExpiresAt > first.ExpiresAt);
        }
        using (var response = await fixture.Http.DeleteAsync(path + "/" + first.Id))
        { Assert.Equal(HttpStatusCode.NoContent, response.StatusCode); Assert.True(response.Headers.CacheControl?.NoStore); }
        using (var response = await fixture.Http.GetAsync(path))
            Assert.Equal(second, Assert.Single((await response.Content.ReadFromJsonAsync<GroupInviteEntry[]>())!));
        fixture.As(fixture.Outside);
        using (var response = await fixture.Token("join", new Uri(first.InviteUrl!).Fragment[1..])) Assert.Equal(HttpStatusCode.Gone, response.StatusCode);
        using (var response = await fixture.Token("preview", new Uri(second.InviteUrl!).Fragment[1..])) Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(fixture.State.IsMember(fixture.Outside.Id, fixture.Room.Id));
    }

    [Fact]
    public async Task UnlimitedInviteIsExplicitListablePreviewableJoinableAndRevocableAtHttpBoundary()
    {
        var time = new InviteClock(); await using var fixture = await Fixture.Start(time); fixture.As(fixture.Owner);
        var path = $"api/conversations/{fixture.Room.Id}/invites";
        GroupInviteEntry unlimited;
        using (var response = await fixture.Http.PostAsJsonAsync(path, new CreateGroupInviteRequest(0, NeverExpires: true)))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.True(response.Headers.CacheControl?.NoStore);
            unlimited = (await response.Content.ReadFromJsonAsync<GroupInviteEntry>())!;
            Assert.True(unlimited.NeverExpires); Assert.Equal(DateTimeOffset.MaxValue, unlimited.ExpiresAt);
        }
        GroupInviteEntry finite;
        using (var response = await fixture.Http.PostAsJsonAsync(path, new CreateGroupInviteRequest(60)))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode); finite = (await response.Content.ReadFromJsonAsync<GroupInviteEntry>())!;
            Assert.False(finite.NeverExpires);
        }
        time.Now = DateTimeOffset.MaxValue;
        using (var response = await fixture.Http.GetAsync(path))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.True(response.Headers.CacheControl?.NoStore);
            var listed = (await response.Content.ReadFromJsonAsync<GroupInviteEntry[]>())!;
            Assert.Equal(unlimited, listed.Single(item => item.Id == unlimited.Id)); Assert.Equal(finite, listed.Single(item => item.Id == finite.Id));
        }
        fixture.As(fixture.Outside); var token = new Uri(unlimited.InviteUrl!).Fragment[1..];
        using (var response = await fixture.Token("preview", token))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.True(response.Headers.CacheControl?.NoStore);
            var preview = (await response.Content.ReadFromJsonAsync<GroupInvitePreview>())!;
            Assert.True(preview.NeverExpires); Assert.Equal(DateTimeOffset.MaxValue, preview.ExpiresAt); Assert.False(preview.AlreadyMember);
        }
        Assert.False(fixture.State.IsMember(fixture.Outside.Id, fixture.Room.Id));
        using (var response = await fixture.Token("preview", new Uri(finite.InviteUrl!).Fragment[1..])) Assert.Equal(HttpStatusCode.Gone, response.StatusCode);
        using (var response = await fixture.Token("join", token))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.True(response.Headers.CacheControl?.NoStore);
            var room = (await response.Content.ReadFromJsonAsync<ConversationSummary>())!; Assert.Equal(fixture.Room.Id, room.Id);
        }
        Assert.Equal("user", fixture.State.GetGroupRole(fixture.Room.Id, fixture.Outside.Id)); Assert.False(fixture.State.IsAdmin(fixture.Outside.Id));
        fixture.As(fixture.Owner);
        using (var response = await fixture.Http.DeleteAsync(path + "/" + unlimited.Id)) Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        fixture.As(fixture.Outside);
        using (var response = await fixture.Token("join", token)) Assert.Equal(HttpStatusCode.Gone, response.StatusCode);
        Assert.True(fixture.State.IsMember(fixture.Outside.Id, fixture.Room.Id)); // Revocation does not remove an admitted member.
    }

    [Fact]
    public async Task HttpDurationSwitchesKeepExistingLinkAndRestoreFiniteExpiry()
    {
        var time = new InviteClock(); await using var fixture = await Fixture.Start(time); fixture.As(fixture.Owner);
        var path = $"api/conversations/{fixture.Room.Id}/invites";
        Assert.Equal(GroupInviteStatus.Success, fixture.State.CreateGroupInvite(fixture.Owner.Id, fixture.Room.Id, 5, out var original));
        time.Now = original!.ExpiresAt;
        using (var response = await fixture.Http.PutAsJsonAsync(path + "/" + original.Id, new ChangeGroupInviteDurationRequest(0, NeverExpires: true)))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.True(response.Headers.CacheControl?.NoStore);
            var changed = (await response.Content.ReadFromJsonAsync<GroupInviteEntry>())!;
            Assert.True(changed.NeverExpires); Assert.Equal(DateTimeOffset.MaxValue, changed.ExpiresAt);
            Assert.Equal(original.Id, changed.Id); Assert.Equal(original.InviteUrl, changed.InviteUrl); Assert.Equal(original.CreatedAt, changed.CreatedAt);
        }
        time.Now = time.Now.AddYears(100);
        using (var response = await fixture.Http.PutAsJsonAsync(path + "/" + original.Id, new ChangeGroupInviteDurationRequest(60)))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.True(response.Headers.CacheControl?.NoStore);
            var changed = (await response.Content.ReadFromJsonAsync<GroupInviteEntry>())!;
            Assert.False(changed.NeverExpires); Assert.Equal(time.Now.AddHours(1), changed.ExpiresAt);
            Assert.Equal(original.Id, changed.Id); Assert.Equal(original.InviteUrl, changed.InviteUrl); Assert.Equal(original.CreatedAt, changed.CreatedAt);
            time.Now = changed.ExpiresAt;
        }
        fixture.As(fixture.Outside);
        using (var response = await fixture.Token("preview", new Uri(original.InviteUrl!).Fragment[1..])) Assert.Equal(HttpStatusCode.Gone, response.StatusCode);
        Assert.False(fixture.State.IsMember(fixture.Outside.Id, fixture.Room.Id));
    }

    [Fact]
    public async Task UnlimitedHttpValidationRejectsInvalidMinuteValuesAndNonBooleanFlagWithoutMutating()
    {
        await using var fixture = await Fixture.Start(); fixture.As(fixture.Owner);
        var path = $"api/conversations/{fixture.Room.Id}/invites";
        Assert.Equal(GroupInviteStatus.Success, fixture.State.CreateGroupInvite(fixture.Owner.Id, fixture.Room.Id, 60, out var original));
        foreach (var duration in new[] { -1, 1, 4, 525601, int.MinValue, int.MaxValue })
        {
            using var create = await fixture.Http.PostAsJsonAsync(path, new CreateGroupInviteRequest(duration, NeverExpires: true));
            using var change = await fixture.Http.PutAsJsonAsync(path + "/" + original!.Id, new ChangeGroupInviteDurationRequest(duration, NeverExpires: true));
            Assert.Equal(HttpStatusCode.BadRequest, create.StatusCode); Assert.Equal(HttpStatusCode.BadRequest, change.StatusCode);
            Assert.True(create.Headers.CacheControl?.NoStore); Assert.True(change.Headers.CacheControl?.NoStore);
            Assert.Equal("invalid_invite_duration", (await create.Content.ReadFromJsonAsync<ApiError>())!.Code);
        }
        foreach (var flag in new[] { "\"true\"", "null", "1" })
        foreach (var method in new[] { HttpMethod.Post, HttpMethod.Put })
        {
            using var request = new HttpRequestMessage(method, method == HttpMethod.Post ? path : path + "/" + original!.Id)
            { Content = new StringContent("{\"durationMinutes\":0,\"neverExpires\":" + flag + "}", Encoding.UTF8, "application/json") };
            using var response = await fixture.Http.SendAsync(request);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); Assert.True(response.Headers.CacheControl?.NoStore);
        }
        Assert.Equal(GroupInviteStatus.Success, fixture.State.ListGroupInvites(fixture.Owner.Id, fixture.Room.Id, out var entries));
        Assert.Equal(original, Assert.Single(entries!));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ListCreateEditAndDeleteDoNotGrantOrdinaryUsersOrModeratorsAdminAuthority(bool unlimited)
    {
        await using var fixture = await Fixture.Start();
        Assert.Equal(GroupInviteStatus.Success, fixture.State.CreateGroupInvite(fixture.Owner.Id, fixture.Room.Id, 60, out var entry));
        var path = $"api/conversations/{fixture.Room.Id}/invites";
        async Task Check(HttpStatusCode expected)
        {
            using var list = await fixture.Http.GetAsync(path);
            using var create = await fixture.Http.PostAsJsonAsync(path, new CreateGroupInviteRequest(unlimited ? 0 : 60, unlimited));
            using var edit = await fixture.Http.PutAsJsonAsync(path + "/" + entry!.Id, new ChangeGroupInviteDurationRequest(unlimited ? 0 : 60, unlimited));
            using var delete = await fixture.Http.DeleteAsync(path + "/" + entry.Id);
            foreach (var response in new[] { list, create, edit, delete })
            { Assert.Equal(expected, response.StatusCode); Assert.True(response.Headers.CacheControl?.NoStore); }
        }
        await Check(HttpStatusCode.Unauthorized);
        fixture.As(fixture.Member); await Check(HttpStatusCode.Forbidden);
        Assert.True(fixture.State.SetGroupRole(fixture.Owner.Id, fixture.Room.Id, fixture.Member.Id, "mod"));
        await Check(HttpStatusCode.Forbidden);
        fixture.As(fixture.Outside); await Check(HttpStatusCode.Forbidden);
        fixture.As(fixture.SiteAdmin);
        using (var list = await fixture.Http.GetAsync(path)) Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        fixture.State.UpsertSiteUser(fixture.SiteAdmin.Id, fixture.SiteAdmin.DisplayName, fixture.SiteAdmin.Email, "user");
        await Check(HttpStatusCode.Forbidden);
        Assert.Equal(GroupInviteStatus.Success, fixture.State.ListGroupInvites(fixture.Owner.Id, fixture.Room.Id, out var entries));
        Assert.Single(entries!);
    }

    [Fact]
    public async Task ManagementRejectsInvalidDurationsMalformedBodiesAndForeignIds()
    {
        await using var fixture = await Fixture.Start(); fixture.As(fixture.Owner);
        var path = $"api/conversations/{fixture.Room.Id}/invites";
        Assert.Equal(GroupInviteStatus.Success, fixture.State.CreateGroupInvite(fixture.Owner.Id, fixture.Room.Id, 60, out var entry));
        foreach (var duration in new[] { -1, 0, 4, 525601 })
        {
            using var create = await fixture.Http.PostAsJsonAsync(path, new CreateGroupInviteRequest(duration));
            using var edit = await fixture.Http.PutAsJsonAsync(path + "/" + entry!.Id, new ChangeGroupInviteDurationRequest(duration));
            Assert.Equal(HttpStatusCode.BadRequest, create.StatusCode); Assert.Equal(HttpStatusCode.BadRequest, edit.StatusCode);
            Assert.True(create.Headers.CacheControl?.NoStore); Assert.True(edit.Headers.CacheControl?.NoStore);
        }
        foreach (var payload in new[] { "null", "{", "{\"durationMinutes\":\"forever\"}" })
        {
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var response = await fixture.Http.PostAsync(path, content);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); Assert.True(response.Headers.CacheControl?.NoStore);
        }
        var other = fixture.State.CreateConversation(fixture.Owner.Id, new("Other", [fixture.Member.Id]))!;
        var foreign = $"api/conversations/{other.Id}/invites/{entry!.Id}";
        using (var response = await fixture.Http.PutAsJsonAsync(foreign, new ChangeGroupInviteDurationRequest(60))) Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        using (var response = await fixture.Http.DeleteAsync(foreign)) Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(GroupInviteStatus.Success, fixture.State.ListGroupInvites(fixture.Owner.Id, fixture.Room.Id, out var entries));
        Assert.Equal(entry, Assert.Single(entries!));
    }

    [Fact]
    public async Task ManagementRequiresAuthenticationAndFreshGroupAdminAuthority()
    {
        await using var fixture = await Fixture.Start();
        var path = $"api/conversations/{fixture.Room.Id}/invite";
        using (var response = await fixture.Http.PostAsync(path, null)) Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        using (var response = await fixture.Http.DeleteAsync(path)) Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        foreach (var operation in new[] { "preview", "join" })
        {
            using var response = await fixture.Token(operation, new string('A', 43));
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        fixture.As(fixture.Member);
        using (var response = await fixture.Http.PostAsync(path, null)) Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.True(fixture.State.SetGroupRole(fixture.Owner.Id, fixture.Room.Id, fixture.Member.Id, "mod"));
        using (var response = await fixture.Http.DeleteAsync(path)) Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        fixture.As(fixture.Outside);
        using (var response = await fixture.Http.PostAsync(path, null)) Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        fixture.As(fixture.Owner);
        fixture.Http.DefaultRequestHeaders.Host = "attacker.invalid";
        using (var response = await fixture.Http.PostAsync(path, null))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var invite = (await response.Content.ReadFromJsonAsync<GroupInviteResult>())!;
            Assert.Equal(fixture.Room.Id, invite.ConversationId);
            Assert.Equal("https://mtkaya.me/chat/invite/", new Uri(invite.InviteUrl).GetLeftPart(UriPartial.Path));
            Assert.Equal(43, new Uri(invite.InviteUrl).Fragment[1..].Length);
            Assert.True(invite.ExpiresAt > DateTimeOffset.UtcNow.AddDays(6));
        }
        fixture.Http.DefaultRequestHeaders.Host = null;

        fixture.As(fixture.SiteAdmin);
        using (var response = await fixture.Http.PostAsync(path, null)) Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(fixture.State.IsMember(fixture.SiteAdmin.Id, fixture.Room.Id));
        using (var response = await fixture.Http.GetAsync($"api/conversations/{fixture.Room.Id}/messages")) Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        fixture.State.UpsertSiteUser(fixture.SiteAdmin.Id, fixture.SiteAdmin.DisplayName, fixture.SiteAdmin.Email, "user");
        // The same session does not preserve a revoked site's admin capability.
        using (var response = await fixture.Http.DeleteAsync(path)) Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        fixture.As(fixture.Owner);
        var direct = fixture.State.GetOrCreateDirect(fixture.Owner.Id, fixture.Member.Id)!;
        using (var response = await fixture.Http.PostAsync($"api/conversations/{direct.Id}/invite", null)) Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using (var response = await fixture.Http.DeleteAsync($"api/conversations/{Guid.NewGuid()}/invite")) Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task PreviewCannotJoinOrExposePrivateDataAndExplicitJoinCannotGrantRolesOrHistory()
    {
        await using var fixture = await Fixture.Start();
        var token = fixture.CreateToken();
        var payload = new EncryptedPayload("test", "test", "test", "PRIVATE-CIPHERTEXT", "test", "test", fixture.Member.Id);
        Assert.True(fixture.State.AddMessage(fixture.Owner.Id,
            new(Guid.NewGuid(), fixture.Room.Id, "text", DateTimeOffset.UtcNow, null, [payload], null), out _));
        fixture.As(fixture.Outside);
        using (var response = await fixture.Token("preview", token))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var json = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(json);
            Assert.Equal(new[] { "alreadyMember", "conversationId", "expiresAt", "neverExpires", "title" },
                document.RootElement.EnumerateObject().Select(property => property.Name).Order().ToArray());
            Assert.False((await response.Content.ReadFromJsonAsync<GroupInvitePreview>())!.AlreadyMember);
            Assert.DoesNotContain("PRIVATE-CIPHERTEXT", json);
        }
        Assert.False(fixture.State.IsMember(fixture.Outside.Id, fixture.Room.Id));
        foreach (var resource in new[] { "messages", "members", "presence" })
        {
            using var response = await fixture.Http.GetAsync($"api/conversations/{fixture.Room.Id}/{resource}");
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
        using (var content = new StringContent(JsonSerializer.Serialize(new { token, role = "admin", groupRole = "admin" }), Encoding.UTF8, "application/json"))
        using (var response = await fixture.Http.PostAsync("api/group-invites/join", content))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(fixture.Room.Id, (await response.Content.ReadFromJsonAsync<ConversationSummary>())!.Id);
        }
        Assert.True(fixture.State.IsMember(fixture.Outside.Id, fixture.Room.Id));
        Assert.Equal("user", fixture.State.GetGroupRole(fixture.Room.Id, fixture.Outside.Id));
        Assert.False(fixture.State.IsAdmin(fixture.Outside.Id));
        using (var response = await fixture.Http.GetAsync($"api/conversations/{fixture.Room.Id}/messages"))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Empty((await response.Content.ReadFromJsonAsync<StoredMessage[]>())!);
        }
        using (var response = await fixture.Token("preview", token))
            Assert.True((await response.Content.ReadFromJsonAsync<GroupInvitePreview>())!.AlreadyMember);
        var before = fixture.State.GetMembers(fixture.Room.Id).Count;
        using (var response = await fixture.Token("join", token)) Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(before, fixture.State.GetMembers(fixture.Room.Id).Count);
        fixture.As(fixture.Member);
        Assert.Single(fixture.State.GetMessages(fixture.Member.Id, fixture.Room.Id, null));
    }

    [Fact]
    public async Task MissingNullMalformedAndOversizedTokensReturnBadRequestWithoutJoining()
    {
        await using var fixture = await Fixture.Start();
        fixture.As(fixture.Outside);
        var bodies = new[] { "null", "{}", "{\"token\":null}", "{\"token\":\"\"}", "{\"token\":17}", "{",
            JsonSerializer.Serialize(new { token = new string('A', 42) }),
            JsonSerializer.Serialize(new { token = new string('A', 42) + "/" }),
            JsonSerializer.Serialize(new { token = new string('A', 4096) }) };
        foreach (var body in bodies)
        foreach (var operation in new[] { "preview", "join" })
        {
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            using var response = await fixture.Http.PostAsync($"api/group-invites/{operation}", content);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        Assert.False(fixture.State.IsMember(fixture.Outside.Id, fixture.Room.Id));
    }

    [Fact]
    public async Task UnknownRotatedRevokedAndExpiredCapabilitiesCannotJoin()
    {
        var time = new InviteClock();
        await using var fixture = await Fixture.Start(time);
        var old = fixture.CreateToken();
        var current = fixture.CreateToken();
        fixture.As(fixture.Outside);
        foreach (var token in new[] { new string('A', 43), old })
        foreach (var operation in new[] { "preview", "join" })
        {
            using var response = await fixture.Token(operation, token);
            Assert.Equal(HttpStatusCode.Gone, response.StatusCode);
        }
        fixture.As(fixture.Owner);
        using (var response = await fixture.Http.DeleteAsync($"api/conversations/{fixture.Room.Id}/invite")) Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        fixture.As(fixture.Outside);
        using (var response = await fixture.Token("join", current)) Assert.Equal(HttpStatusCode.Gone, response.StatusCode);
        var expires = fixture.CreateToken();
        time.Now = time.Now.AddDays(7);
        using (var response = await fixture.Token("preview", expires)) Assert.Equal(HttpStatusCode.Gone, response.StatusCode);
        using (var response = await fixture.Token("join", expires)) Assert.Equal(HttpStatusCode.Gone, response.StatusCode);
        Assert.False(fixture.State.IsMember(fixture.Outside.Id, fixture.Room.Id));
    }

    [Fact]
    public async Task BanAndCapacityLimitsHoldAtHttpBoundary()
    {
        await using var fixture = await Fixture.Start();
        var token = fixture.CreateToken();
        // Group bans survive leaving and cannot be bypassed using a still-valid link.
        Assert.True(fixture.State.ModerateChatUser(fixture.Room.Id, fixture.Member.Id, "ban", null));
        Assert.Equal(LeaveGroupResult.Left, fixture.State.LeaveGroup(fixture.Member.Id, fixture.Room.Id));
        fixture.As(fixture.Member);
        using (var response = await fixture.Token("preview", token)) Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using (var response = await fixture.Token("join", token)) Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        fixture.As(fixture.Outside);
        Assert.True(fixture.State.ModerateUser(fixture.Outside.Id, "ban", null));
        using (var response = await fixture.Token("join", token)) Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        var members = Enumerable.Range(0, 49).Select(index => User(fixture.State, "full-" + index)).ToArray();
        var full = fixture.State.CreateConversation(fixture.Owner.Id, new("Full", members.Select(user => user.Id).ToArray()))!;
        Assert.Equal(GroupInviteStatus.Success, fixture.State.CreateOrRotateGroupInvite(fixture.Owner.Id, full.Id, out var invitation));
        var entrant = User(fixture.State, "entrant");
        fixture.As(entrant);
        using (var response = await fixture.Token("join", new Uri(invitation!.InviteUrl).Fragment[1..])) Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.False(fixture.State.IsMember(entrant.Id, full.Id));
    }

    [Fact]
    public async Task InviteRateLimitIsPerUserAcrossSessionsAndPreviewAndJoinShareIt()
    {
        await using var fixture = await Fixture.Start();
        var token = fixture.CreateToken();
        fixture.As(fixture.Outside);
        for (var index = 0; index < 20; index++)
        {
            using var response = await fixture.Token("preview", token);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        fixture.As(fixture.Outside); // Reauthenticating does not open another rate bucket.
        using (var response = await fixture.Token("join", token)) Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.False(fixture.State.IsMember(fixture.Outside.Id, fixture.Room.Id));
        fixture.As(fixture.Member);
        using (var response = await fixture.Token("preview", token)) Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("POST", "/api/conversations/aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa/invite", true)]
    [InlineData("DELETE", "/api/conversations/aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa/invite/", true)]
    [InlineData("POST", "/API/CONVERSATIONS/aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa/INVITE", true)]
    [InlineData("GET", "/api/conversations/aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa/invite", false)]
    [InlineData("POST", "/api/group-invites/preview", false)]
    [InlineData("POST", "/api/group-invites/join", false)]
    [InlineData("POST", "/api/conversations/not-a-guid/invite", false)]
    [InlineData("PUT", "/api/conversations/aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa/title", true)]
    [InlineData("GET", "/API/CONVERSATIONS/aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa/INVITES", true)]
    [InlineData("POST", "/api/conversations/aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa/invites", true)]
    [InlineData("PUT", "/api/conversations/aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa/invites/bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb", true)]
    [InlineData("DELETE", "/api/conversations/aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa/invites/bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb/", true)]
    [InlineData("DELETE", "/api/conversations/aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa/invites/not-guid", false)]
    [InlineData("POST", "/API/CONVERSATIONS/aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa/MEMBERS/bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb/REMOVE", true)]
    [InlineData("POST", "/api/conversations/aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa/members/not-guid/remove", false)]
    public void PrivilegedInviteManagementRefreshesSiteRolesBeforeAuthorization(string method, string path, bool expected)
    {
        var probe = typeof(ChatAuthenticationMiddleware).GetMethod("RequiresFreshGroupAuthority", BindingFlags.Static | BindingFlags.NonPublic)!;
        Assert.Equal(expected, (bool)probe.Invoke(null, [new PathString(path), method])!);
    }

    [Fact]
    public async Task EveryInviteResponseIsNoStoreIncludingEarlyUnauthorizedMalformedAndRateLimitedErrors()
    {
        await using var fixture = await Fixture.Start();
        var token = fixture.CreateToken();
        var path = $"api/conversations/{fixture.Room.Id}/invite";
        static void NoStore(HttpResponseMessage response) => Assert.True(response.Headers.CacheControl?.NoStore == true);
        using (var response = await fixture.Http.PostAsync(path, null)) { Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode); NoStore(response); }
        using (var response = await fixture.Http.DeleteAsync(path)) { Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode); NoStore(response); }
        foreach (var operation in new[] { "preview", "join" })
        {
            using var response = await fixture.Token(operation, token);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode); NoStore(response);
        }
        fixture.As(fixture.Outside);
        using (var response = await fixture.Http.PostAsync(path, null)) { Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode); NoStore(response); }
        using (var response = await fixture.Http.DeleteAsync(path)) { Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode); NoStore(response); }
        foreach (var operation in new[] { "preview", "join" })
        {
            using var malformed = new StringContent("{", Encoding.UTF8, "application/json");
            using var response = await fixture.Http.PostAsync("api/group-invites/" + operation, malformed);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); NoStore(response);
        }
        using (var response = await fixture.Token("preview", token)) { Assert.Equal(HttpStatusCode.OK, response.StatusCode); NoStore(response); }
        using (var response = await fixture.Token("join", token)) { Assert.Equal(HttpStatusCode.OK, response.StatusCode); NoStore(response); }
        fixture.As(fixture.Owner);
        using (var response = await fixture.Http.PostAsync(path, null)) { Assert.Equal(HttpStatusCode.OK, response.StatusCode); NoStore(response); }
        using (var response = await fixture.Http.DeleteAsync(path)) { Assert.Equal(HttpStatusCode.NoContent, response.StatusCode); NoStore(response); }
        for (var index = 0; index < 18; index++)
        {
            using var response = await fixture.Token("preview", token);
            Assert.Equal(HttpStatusCode.Gone, response.StatusCode); NoStore(response);
        }
        using (var response = await fixture.Token("join", token)) { Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode); NoStore(response); }
    }

    [Fact]
    public async Task UnauthenticatedInviteCannotTriggerConfiguredDatabaseSynchronization()
    {
        var database = new ChatDatabase(Options.Create(new DatabaseOptions
        {
            Provider = "MySQL", ConnectionString = "Server=127.0.0.1;Port=1;User ID=invalid;Database=invalid;Connection Timeout=1"
        }));
        Assert.True(database.IsConfigured);
        var context = new DefaultHttpContext();
        using var services = new ServiceCollection().AddOptions().BuildServiceProvider();
        context.RequestServices = services;
        context.Request.Method = "POST";
        context.Request.Path = "/api/conversations/aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa/invite";
        using var body = new MemoryStream();
        context.Response.Body = body;
        // Deliberately unreachable fake DB: the unauthenticated branch must return
        // before reading accounts. No real site or external database is contacted.
        var reached = false;
        var middleware = new ChatAuthenticationMiddleware(_ => { reached = true; return Task.CompletedTask; });
        await middleware.InvokeAsync(context, new ChatState(), database);
        Assert.Equal(401, context.Response.StatusCode);
        Assert.False(reached);
    }

    private static ChatUser User(ChatState state, string name, string role = "user") =>
        state.UpsertSiteUser(Guid.NewGuid(), name, name + "@invite-http.invalid", role);

    private sealed class InviteClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Fixture(WebApplication app, ChatState state, HttpClient http) : IAsyncDisposable
    {
        public ChatState State { get; } = state;
        public HttpClient Http { get; } = http;
        public ChatUser Owner { get; } = User(state, "owner");
        public ChatUser Member { get; } = User(state, "member");
        public ChatUser Outside { get; } = User(state, "outside");
        public ChatUser SiteAdmin { get; } = User(state, "site", "admin");
        public ConversationSummary Room { get; private set; } = null!;

        public static async Task<Fixture> Start(TimeProvider? time = null)
        {
            var state = new ChatState(timeProvider: time);
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            builder.Services.AddSingleton(state);
            builder.Services.AddSingleton(new ChatDatabase(Options.Create(new DatabaseOptions())));
            builder.Services.AddRateLimiter(options =>
            {
                options.RejectionStatusCode = 429;
                options.AddPolicy("group-invites", context => RateLimitPartition.GetFixedWindowLimiter(
                    context.Items["UserId"]?.ToString() ?? "anonymous", _ => new FixedWindowRateLimiterOptions
                    { PermitLimit = 20, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
            });
            var app = builder.Build();
            app.UseMiddleware<ChatAuthenticationMiddleware>();
            app.UseRateLimiter();
            app.MapGroupInviteEndpoints();
            app.MapConversationEndpoints();
            await app.StartAsync();
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            var result = new Fixture(app, state, new HttpClient { BaseAddress = new Uri(address) });
            result.Room = state.CreateConversation(result.Owner.Id, new("Private group", [result.Member.Id]))!;
            return result;
        }

        public void As(ChatUser user) => Http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", State.CreateSession(user.Id));

        public string CreateToken()
        {
            Assert.Equal(GroupInviteStatus.Success, State.CreateOrRotateGroupInvite(Owner.Id, Room.Id, out var invitation));
            return new Uri(invitation!.InviteUrl).Fragment[1..];
        }

        public Task<HttpResponseMessage> Token(string operation, string token) =>
            Http.PostAsJsonAsync("api/group-invites/" + operation, new GroupInviteTokenRequest(token));

        public async ValueTask DisposeAsync()
        {
            Http.Dispose();
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
