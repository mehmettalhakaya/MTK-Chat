using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MTKChat.Contracts;
using MTKChat.Server.Configuration;
using MTKChat.Server.Services;
using Xunit;

namespace MTKChat.Cryptography.Tests;

public sealed class ProfileNameTests
{
    private static readonly Guid Gemini = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static ChatUser User(ChatState state, string username = "account", string role = "user") =>
        state.UpsertSiteUser(Guid.NewGuid(), username, username + "@profile-name.invalid", role);
    private static string Snapshot(ChatState state) => (string)typeof(ChatState)
        .GetMethod("SerializeSnapshotUnsafe", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(state, [])!;
    private static void Restore(ChatState state, string json) => typeof(ChatState)
        .GetMethod("RestoreSnapshot", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(state, [json]);

    [Theory]
    [InlineData(null, "Kaya")]
    [InlineData("", "Kaya")]
    [InlineData("   ", "Kaya")]
    [InlineData("Talha", null)]
    [InlineData("Talha", "")]
    [InlineData("Talha", "   ")]
    [InlineData("Name123", "Kaya")]
    [InlineData("<script>", "Kaya")]
    [InlineData("Ali\nVeli", "Kaya")]
    [InlineData("Ali\tVeli", "Kaya")]
    [InlineData("Ali\u200B", "Kaya")]
    [InlineData("Ali\u202E", "Kaya")]
    [InlineData("Ali", "😀")]
    [InlineData("-Ali", "Kaya")]
    [InlineData("Ali", "Kaya-")]
    [InlineData("\u0301Ali", "Kaya")]
    public void InvalidOrMissingNamesNeverMutateProfile(string? first, string? last)
    {
        var state = new ChatState(); var user = User(state); var before = Snapshot(state);
        Assert.Equal(ProfileNameChangeStatus.InvalidName, state.SetProfileName(user.Id, first, last, out var result));
        Assert.Null(result); Assert.Equal(user, state.GetUser(user.Id)); Assert.Equal(before, Snapshot(state));
    }

    [Theory]
    [InlineData("  Mehmet   Talha ", "  Kaya ", "Mehmet Talha", "Kaya")]
    [InlineData("Jose\u0301", "O’Connor", "José", "O’Connor")]
    [InlineData("Anne-Marie", "D'Arcy", "Anne-Marie", "D'Arcy")]
    [InlineData("李", "王", "李", "王")]
    [InlineData("أحمد", "بن علي", "أحمد", "بن علي")]
    [InlineData("Ada\u00A0Lovelace", "Byron", "Ada Lovelace", "Byron")]
    public void UnicodeAndMultiwordNamesAreCanonicalWithoutRenamingSiteIdentity(string first, string last, string expectedFirst, string expectedLast)
    {
        var state = new ChatState(); var user = User(state, "unchanged_login", "admin");
        Assert.Equal(ProfileNameChangeStatus.Success, state.SetProfileName(user.Id, first, last, out var result));
        Assert.Equal(expectedFirst, result!.FirstName); Assert.Equal(expectedLast, result.LastName);
        Assert.Equal(user.Id, result.Id); Assert.Equal(user.DisplayName, result.DisplayName); Assert.Equal(user.Email, result.Email);
        Assert.Equal(user.Role, result.Role); Assert.Equal(user.PhotoVersion, result.PhotoVersion); Assert.False(result.IsAgent);
        Assert.Equal(result, state.GetUser(user.Id));
    }

    [Fact]
    public void NamesHaveEightyUnicodeScalarLimitAndRejectMalformedUtf16()
    {
        var state = new ChatState(); var user = User(state);
        var astralLetter = char.ConvertFromUtf32(0x10400);
        var eighty = string.Concat(Enumerable.Repeat(astralLetter, 80));
        Assert.Equal(ProfileNameChangeStatus.Success, state.SetProfileName(user.Id, eighty, "Kaya", out var saved));
        Assert.Equal(eighty, saved!.FirstName);
        var before = Snapshot(state);
        foreach (var first in new[] { eighty + astralLetter, new string('A', 81), new string('A', 513), "Ali\uD800" })
            Assert.Equal(ProfileNameChangeStatus.InvalidName, state.SetProfileName(user.Id, first, "Kaya", out _));
        Assert.Equal(before, Snapshot(state));
    }

    [Fact]
    public void BotsUnknownAndGloballyBannedAccountsCannotEditRealNames()
    {
        var state = new ChatState(); var user = User(state);
        Assert.Equal(ProfileNameChangeStatus.Forbidden, state.SetProfileName(Gemini, "Gemini", "Bot", out _));
        Assert.Equal(ProfileNameChangeStatus.Forbidden, state.SetProfileName(Guid.NewGuid(), "Some", "Name", out _));
        Assert.True(state.ModerateUser(user.Id, "ban", null));
        Assert.Equal(ProfileNameChangeStatus.Forbidden, state.SetProfileName(user.Id, "Some", "Name", out _));
        Assert.Null(state.GetUser(user.Id)!.FirstName);
    }

    [Fact]
    public void SiteUpsertAndSynchronizationPreserveNamesButRefreshUsernameEmailAndRole()
    {
        var state = new ChatState(); var user = User(state);
        state.SetProfileName(user.Id, "Mehmet Talha", "Kaya", out _);
        var changed = state.UpsertSiteUser(user.Id, "new_login", "new@profile-name.invalid", "admin");
        Assert.Equal("Mehmet Talha", changed.FirstName); Assert.Equal("Kaya", changed.LastName);
        state.SynchronizeSiteUsers([new SiteAccount(1, user.Id, "site_login", "site@profile-name.invalid", "user", true)]);
        var synced = state.GetUser(user.Id)!;
        Assert.Equal("Mehmet Talha", synced.FirstName); Assert.Equal("Kaya", synced.LastName);
        Assert.Equal("site_login", synced.DisplayName); Assert.Equal("site@profile-name.invalid", synced.Email); Assert.Equal("user", synced.Role);
    }

    [Fact]
    public void SnapshotRoundTripRestoresNamesBeforeSiteUsersAndOldSnapshotsRemainNameless()
    {
        var state = new ChatState(); var user = User(state); state.SetProfileName(user.Id, "First", "Last", out _);
        var snapshot = Snapshot(state); var restored = new ChatState(); Restore(restored, snapshot);
        restored.SynchronizeSiteUsers([new SiteAccount(1, user.Id, user.DisplayName, user.Email, user.Role, true)]);
        Assert.Equal("First", restored.GetUser(user.Id)!.FirstName); Assert.Equal("Last", restored.GetUser(user.Id)!.LastName);
        var legacy = JsonNode.Parse(snapshot)!.AsObject(); legacy.Remove("ProfileNames");
        Restore(restored, legacy.ToJsonString());
        restored.SynchronizeSiteUsers([new SiteAccount(1, user.Id, user.DisplayName, user.Email, user.Role, true)]);
        Assert.Null(restored.GetUser(user.Id)!.FirstName); Assert.Null(restored.GetUser(user.Id)!.LastName);
        var oldUser = JsonSerializer.Deserialize<ChatUser>("{\"Id\":\"" + user.Id + "\",\"DisplayName\":\"login\",\"Email\":\"local@invalid\",\"IsAgent\":false,\"AvatarColor\":null}")!;
        Assert.Null(oldUser.FirstName); Assert.Null(oldUser.LastName); Assert.Equal("user", oldUser.Role);
    }

    [Fact]
    public void InactiveAndDeletedSiteAccountsLoseTheirNamesIncludingRestoredOrphanEntries()
    {
        var state = new ChatState(); var active = User(state, "active"); var inactive = User(state, "inactive"); var deleted = User(state, "deleted");
        foreach (var user in new[] { active, inactive, deleted }) state.SetProfileName(user.Id, "First", "Last", out _);
        var restored = new ChatState(); Restore(restored, Snapshot(state));
        restored.SynchronizeSiteUsers([new SiteAccount(1, active.Id, active.DisplayName, active.Email, active.Role, true),
            new SiteAccount(2, inactive.Id, inactive.DisplayName, inactive.Email, inactive.Role, false)]);
        Assert.Equal("First", restored.GetUser(active.Id)!.FirstName); Assert.Null(restored.GetUser(inactive.Id)); Assert.Null(restored.GetUser(deleted.Id));
        var entries = JsonNode.Parse(Snapshot(restored))!["ProfileNames"]!.AsArray();
        Assert.Single(entries); Assert.Equal(active.Id, entries[0]!["UserId"]!.GetValue<Guid>());
        Assert.Null(restored.UpsertSiteUser(inactive.Id, inactive.DisplayName, inactive.Email, inactive.Role).FirstName);
        Assert.Null(restored.UpsertSiteUser(deleted.Id, deleted.DisplayName, deleted.Email, deleted.Role).LastName);
        // Cleanup also works for already-loaded users, not just startup orphans.
        state.SynchronizeSiteUsers([new SiteAccount(1, active.Id, active.DisplayName, active.Email, active.Role, true)]);
        Assert.Single(JsonNode.Parse(Snapshot(state))!["ProfileNames"]!.AsArray());
    }

    [Fact]
    public void InvalidAndAgentSnapshotProfilesAreIgnored()
    {
        var state = new ChatState(); var user = User(state); var json = JsonNode.Parse(Snapshot(state))!;
        json["ProfileNames"] = new JsonArray(
            new JsonObject { ["UserId"] = Gemini, ["FirstName"] = "Fake", ["LastName"] = "Bot" },
            new JsonObject { ["UserId"] = user.Id, ["FirstName"] = "Fake<script>", ["LastName"] = "Last" },
            new JsonObject { ["UserId"] = Guid.Empty, ["FirstName"] = "Fake", ["LastName"] = "Last" });
        Restore(state, json.ToJsonString());
        Assert.Null(state.GetUser(Gemini)!.FirstName); Assert.Null(state.GetUser(user.Id)!.FirstName);
        Assert.Empty(JsonNode.Parse(Snapshot(state))!["ProfileNames"]!.AsArray());
    }

    [Fact]
    public void PersistenceFailureRollsBackBothNewAndExistingProfileNames()
    {
        foreach (var hasPrevious in new[] { false, true })
        {
            var state = new ChatState(); var user = User(state);
            if (hasPrevious) state.SetProfileName(user.Id, "Old", "Name", out _);
            var before = Snapshot(state); var visible = state.GetUser(user.Id);
            // An unconfigured database fails before connecting; no external DB or
            // production credentials are needed to exercise transactional rollback.
            typeof(ChatState).GetField("_database", BindingFlags.NonPublic | BindingFlags.Instance)!
                .SetValue(state, new ChatDatabase(Options.Create(new DatabaseOptions())));
            Assert.Throws<InvalidOperationException>(() => state.SetProfileName(user.Id, "New", "Name", out _));
            Assert.Equal(before, Snapshot(state)); Assert.Equal(visible, state.GetUser(user.Id));
        }
    }

    [Fact]
    public async Task HttpChangesOnlyAuthenticatedOwnerAndIgnoresInjectedIdentityFields()
    {
        await using var fixture = await Fixture.Start(); fixture.As(fixture.Owner);
        using var response = await fixture.Http.PutAsJsonAsync("api/profile/name", new
        {
            firstName = "Mehmet Talha", lastName = "Kaya", userId = fixture.Other.Id, role = "admin", email = "attacker@invalid"
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); NoStore(response);
        var result = await response.Content.ReadFromJsonAsync<ChatUser>();
        Assert.Equal(fixture.Owner.Id, result!.Id); Assert.Equal("Mehmet Talha", result.FirstName); Assert.Equal("Kaya", result.LastName);
        Assert.Equal(fixture.Owner.DisplayName, result.DisplayName); Assert.Equal(fixture.Owner.Email, result.Email); Assert.Equal("user", result.Role);
        Assert.Null(fixture.State.GetUser(fixture.Other.Id)!.FirstName); Assert.Null(fixture.State.GetUser(fixture.Other.Id)!.LastName);
        using var get = await fixture.Http.GetAsync("api/profile"); NoStore(get);
        Assert.Equal(result, await get.Content.ReadFromJsonAsync<ChatUser>());
    }

    [Fact]
    public async Task HttpRejectsAnonymousMalformedMissingBannedAndBotRequestsWithoutMutation()
    {
        await using var fixture = await Fixture.Start();
        using (var response = await fixture.Http.PutAsJsonAsync("api/profile/name", new ProfileNameChangeRequest("Some", "Name")))
        { Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode); NoStore(response); }
        fixture.As(fixture.Owner);
        using (var malformed = new StringContent("{", Encoding.UTF8, "application/json"))
        using (var response = await fixture.Http.PutAsync("api/profile/name", malformed))
        { Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); NoStore(response); }
        foreach (var request in new object[] { new { firstName = "First" }, new { lastName = "Last" }, new { firstName = "", lastName = "Last" }, new { firstName = "First", lastName = "<script>" } })
        {
            using var response = await fixture.Http.PutAsJsonAsync("api/profile/name", request);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); NoStore(response);
        }
        Assert.Null(fixture.State.GetUser(fixture.Owner.Id)!.FirstName);
        fixture.As(fixture.State.GetUser(Gemini)!);
        using (var response = await fixture.Http.PutAsJsonAsync("api/profile/name", new ProfileNameChangeRequest("Some", "Name")))
        { Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode); NoStore(response); }
        fixture.As(fixture.Owner); fixture.State.ModerateUser(fixture.Owner.Id, "ban", null);
        using (var response = await fixture.Http.PutAsJsonAsync("api/profile/name", new ProfileNameChangeRequest("Some", "Name")))
        { Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode); NoStore(response); }
        Assert.Null(fixture.State.GetUser(fixture.Owner.Id)!.FirstName);
    }

    [Fact]
    public async Task HttpRateLimitIsPerOwnerAcrossSessionsAndDenialsAreNoStore()
    {
        await using var fixture = await Fixture.Start(); fixture.As(fixture.Owner);
        for (var index = 0; index < 10; index++)
        {
            using var response = await fixture.Http.PutAsJsonAsync("api/profile/name", new ProfileNameChangeRequest("Some", "Name"));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode); NoStore(response);
        }
        fixture.As(fixture.Owner);
        using (var response = await fixture.Http.PutAsJsonAsync("api/profile/name", new ProfileNameChangeRequest("Other", "Name")))
        { Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode); NoStore(response); }
        Assert.Equal("Some", fixture.State.GetUser(fixture.Owner.Id)!.FirstName);
        fixture.As(fixture.Other);
        using var other = await fixture.Http.PutAsJsonAsync("api/profile/name", new ProfileNameChangeRequest("Other", "Name"));
        Assert.Equal(HttpStatusCode.OK, other.StatusCode); NoStore(other);
    }

    private static void NoStore(HttpResponseMessage response) => Assert.True(response.Headers.CacheControl?.NoStore == true);
    private sealed class Fixture(WebApplication app, ChatState state, HttpClient http) : IAsyncDisposable
    {
        internal ChatState State { get; } = state;
        internal HttpClient Http { get; } = http;
        internal ChatUser Owner { get; } = User(state, "owner");
        internal ChatUser Other { get; } = User(state, "other");
        internal static async Task<Fixture> Start()
        {
            var state = new ChatState(); var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
            builder.Services.AddSingleton(state); builder.Services.AddSingleton(new ChatDatabase(Options.Create(new DatabaseOptions())));
            builder.Services.AddRateLimiter(options =>
            {
                options.RejectionStatusCode = 429;
                options.AddPolicy("profile-name", context => RateLimitPartition.GetFixedWindowLimiter(
                    context.Items["UserId"]?.ToString() ?? "anonymous", _ => new FixedWindowRateLimiterOptions
                    { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
                options.AddPolicy("profile-photo", _ => RateLimitPartition.GetNoLimiter("fixture"));
            });
            var app = builder.Build(); app.UseMiddleware<ChatAuthenticationMiddleware>(); app.UseRateLimiter();
            app.MapProfileEndpoints(); await app.StartAsync();
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            return new Fixture(app, state, new HttpClient { BaseAddress = new Uri(address) });
        }
        internal void As(ChatUser user) => Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", State.CreateSession(user.Id));
        public async ValueTask DisposeAsync() { Http.Dispose(); await app.StopAsync(); await app.DisposeAsync(); }
    }
}
