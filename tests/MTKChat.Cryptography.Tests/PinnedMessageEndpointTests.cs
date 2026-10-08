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

public sealed class PinnedMessageEndpointTests
{
    [Fact]
    public async Task AnonymousReadCreateDeleteAreUnauthorizedAndNeverCache()
    {
        await using var f = await HttpFixture.Start(); var message = f.Data.Message();
        using var get = await f.Http.GetAsync(f.Path);
        using var post = await f.Http.PostAsJsonAsync(f.Path, new PinMessageRequest(message.Id));
        using var delete = await f.Http.DeleteAsync(f.Path + "/" + message.Id);
        foreach (var response in new[] { get, post, delete })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode); NoStore(response);
            Assert.Equal("unauthorized", (await response.Content.ReadFromJsonAsync<ApiError>())!.Code);
            Assert.DoesNotContain("SYNTHETIC-CIPHERTEXT", await response.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task SharedMetadataReadAndRoleScopedWritesWorkWithoutPlaintextOrAdministratorBypass()
    {
        await using var f = await HttpFixture.Start(); var d = f.Data; var message = d.Message(); f.As(d.Owner);
        using (var response = await f.Http.PostAsJsonAsync(f.Path, new PinMessageRequest(message.Id)))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode); NoStore(response);
            var pin = (await response.Content.ReadFromJsonAsync<PinnedMessageView>())!;
            Assert.Equal(message.Id, pin.MessageId); Assert.Equal(d.Time.Now.AddDays(7), pin.ExpiresAt);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(5, json.RootElement.EnumerateObject().Count());
            Assert.DoesNotContain("ciphertext", await response.Content.ReadAsStringAsync());
            Assert.DoesNotContain("payloads", await response.Content.ReadAsStringAsync());
        }
        f.As(d.Member);
        using (var response = await f.Http.GetAsync(f.Path))
        { Assert.Equal(message.Id, Assert.Single((await response.Content.ReadFromJsonAsync<PinnedMessageView[]>())!).MessageId); NoStore(response); }
        using (var response = await f.Http.PostAsJsonAsync(f.Path, new PinMessageRequest(message.Id)))
        { Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode); NoStore(response); }
        using (var response = await f.Http.DeleteAsync(f.Path + "/" + message.Id))
        { Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode); NoStore(response); }
        foreach (var outsider in new[] { d.Outside, d.SiteAdmin })
        {
            f.As(outsider);
            using var get = await f.Http.GetAsync(f.Path);
            using var post = await f.Http.PostAsJsonAsync(f.Path, new PinMessageRequest(message.Id));
            using var delete = await f.Http.DeleteAsync(f.Path + "/" + message.Id);
            foreach (var response in new[] { get, post, delete }) { Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode); NoStore(response); }
        }
        d.State.SetGroupRole(d.Owner.Id, d.Group.Id, d.Member.Id, "mod"); f.As(d.Member);
        using (var response = await f.Http.DeleteAsync(f.Path + "/" + message.Id))
        { Assert.Equal(HttpStatusCode.NoContent, response.StatusCode); NoStore(response); }
        using (var response = await f.Http.GetAsync(f.Path)) { Assert.Empty((await response.Content.ReadFromJsonAsync<PinnedMessageView[]>())!); NoStore(response); }
    }

    [Fact]
    public async Task MalformedRequestInvalidDurationMissingIdAndCapacityAreExplicitErrors()
    {
        await using var f = await HttpFixture.Start(); var d = f.Data; f.As(d.Owner); var message = d.Message();
        foreach (var body in new[] { "{", "null", "{}", "{\"messageId\":false}", "{\"messageId\":\"bad\"}" })
        {
            using var response = await f.Http.PostAsync(f.Path, new StringContent(body, Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); NoStore(response);
        }
        using (var response = await f.Http.PostAsJsonAsync(f.Path, new PinMessageRequest(message.Id, 1)))
        { Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); NoStore(response); }
        using (var response = await f.Http.PostAsJsonAsync(f.Path, new PinMessageRequest(Guid.NewGuid())))
        { Assert.Equal(HttpStatusCode.NotFound, response.StatusCode); NoStore(response); }
        using (var response = await f.Http.PostAsync(f.Path, new StringContent("{\"messageId\":\"" + message.Id + "\"}", Encoding.UTF8, "application/json")))
        { Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.Equal(d.Time.Now.AddDays(7), (await response.Content.ReadFromJsonAsync<PinnedMessageView>())!.ExpiresAt); NoStore(response); }
        d.Pin(d.Message()); d.Pin(d.Message());
        using (var response = await f.Http.PostAsJsonAsync(f.Path, new PinMessageRequest(d.Message().Id)))
        { Assert.Equal(HttpStatusCode.Conflict, response.StatusCode); Assert.Equal("pin_limit", (await response.Content.ReadFromJsonAsync<ApiError>())!.Code); NoStore(response); }
    }

    [Fact]
    public async Task WriteRateLimitDoesNotBlockPinBannerRefresh()
    {
        await using var f = await HttpFixture.Start(); var d = f.Data; f.As(d.Owner); var message = d.Message();
        for (var index = 0; index < 20; index++)
        {
            using var response = await f.Http.PostAsJsonAsync(f.Path, new PinMessageRequest(message.Id));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode); NoStore(response);
        }
        using (var response = await f.Http.DeleteAsync(f.Path + "/" + message.Id))
        { Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode); NoStore(response); }
        for (var index = 0; index < 25; index++)
        {
            using var response = await f.Http.GetAsync(f.Path); Assert.Equal(HttpStatusCode.OK, response.StatusCode); NoStore(response);
        }
    }

    [Fact]
    public async Task MembershipRemovalAndRoleRevocationAffectAlreadyAuthenticatedSessions()
    {
        await using var f = await HttpFixture.Start(); var d = f.Data; var message = d.Message(); d.Pin(message);
        d.State.SetGroupRole(d.Owner.Id, d.Group.Id, d.Member.Id, "mod"); f.As(d.Member);
        d.State.SetGroupRole(d.Owner.Id, d.Group.Id, d.Member.Id, "user");
        using (var response = await f.Http.DeleteAsync(f.Path + "/" + message.Id))
        { Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode); NoStore(response); }
        Assert.Equal(LeaveGroupResult.Left, d.State.LeaveGroup(d.Member.Id, d.Group.Id));
        using (var response = await f.Http.GetAsync(f.Path)) { Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode); NoStore(response); }
        f.As(d.Owner); d.State.ModerateUser(d.Owner.Id, "ban", null);
        using (var response = await f.Http.GetAsync(f.Path)) { Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode); NoStore(response); }
    }

    [Theory]
    [InlineData("POST", "pins", true)] [InlineData("DELETE", "pins/11111111-1111-1111-1111-111111111111", true)]
    [InlineData("GET", "pins", false)] [InlineData("POST", "PINS", true)] [InlineData("DELETE", "PINS/11111111-1111-1111-1111-111111111111", true)]
    public void AuthorityRefreshMatchesCaseInsensitivePinWriteRoutes(string method, string suffix, bool required)
    {
        var callback = typeof(ChatAuthenticationMiddleware).GetMethod("RequiresFreshGroupAuthority", BindingFlags.Static | BindingFlags.NonPublic)!;
        Assert.Equal(required, callback.Invoke(null, [new PathString("/API/CONVERSATIONS/aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa/" + suffix), method]));
    }

    [Fact]
    public async Task AnonymousPinWriteDoesNotContactConfiguredDatabaseForRoleRefresh()
    {
        var database = new ChatDatabase(Options.Create(new DatabaseOptions
        { Provider = "MySQL", ConnectionString = "Server=127.0.0.1;Port=1;User ID=invalid;Database=invalid;Connection Timeout=1" }));
        var context = new DefaultHttpContext(); using var services = new ServiceCollection().AddOptions().BuildServiceProvider();
        context.RequestServices = services; context.Request.Method = "POST";
        context.Request.Path = "/api/conversations/aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa/pins";
        using var body = new MemoryStream(); context.Response.Body = body;
        var reached = false;
        await new ChatAuthenticationMiddleware(_ => { reached = true; return Task.CompletedTask; }).InvokeAsync(context, new ChatState(), database);
        Assert.False(reached); Assert.Equal(401, context.Response.StatusCode); Assert.Equal("no-store", context.Response.Headers.CacheControl.ToString());
    }

    private static void NoStore(HttpResponseMessage response) => Assert.True(response.Headers.CacheControl?.NoStore);

    private sealed class HttpFixture(WebApplication app, PinnedMessageTests.Fixture data, HttpClient http) : IAsyncDisposable
    {
        public PinnedMessageTests.Fixture Data { get; } = data;
        public HttpClient Http { get; } = http;
        public string Path => "api/conversations/" + Data.Group.Id + "/pins";
        public void As(ChatUser user) => Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Data.State.CreateSession(user.Id));
        public static async Task<HttpFixture> Start()
        {
            var data = new PinnedMessageTests.Fixture(); var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
            builder.Services.AddSingleton(data.State); builder.Services.AddSingleton(new ChatDatabase(Options.Create(new DatabaseOptions())));
            builder.Services.AddRateLimiter(options =>
            {
                options.RejectionStatusCode = 429;
                options.AddPolicy("message-pins", context => RateLimitPartition.GetFixedWindowLimiter(context.Items["UserId"]?.ToString() ?? "anonymous",
                    _ => new FixedWindowRateLimiterOptions { PermitLimit = 20, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
            });
            var app = builder.Build(); app.UseMiddleware<ChatAuthenticationMiddleware>(); app.UseRateLimiter(); app.MapPinEndpoints();
            await app.StartAsync();
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            return new HttpFixture(app, data, new HttpClient { BaseAddress = new Uri(address) });
        }
        public async ValueTask DisposeAsync() { Http.Dispose(); await app.StopAsync(); await app.DisposeAsync(); }
    }
}
