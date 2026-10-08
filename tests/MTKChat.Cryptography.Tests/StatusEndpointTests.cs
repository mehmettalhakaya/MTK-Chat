using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MTKChat.Contracts;
using MTKChat.Server.Configuration;
using MTKChat.Server.Services;
using Xunit;

namespace MTKChat.Cryptography.Tests;

public sealed class StatusEndpointTests
{
    [Fact]
    public async Task PublishFeedReadDeleteAreOwnerOrAudienceScopedAndNeverCache()
    {
        await using var fixture = await HttpFixture.Start(); var data = fixture.Data;
        fixture.As(data.Author); var sent = await fixture.Publish(data.Request());
        Assert.Equal(data.Author.Id, sent.Payload.RecipientId);
        fixture.As(data.Member);
        using (var response = await fixture.Http.GetAsync("api/statuses"))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode); NoStore(response);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(6, json.RootElement[0].EnumerateObject().Count());
            Assert.False(json.RootElement[0].TryGetProperty("ciphertext", out _));
            Assert.Equal(sent.Id, Assert.Single((await response.Content.ReadFromJsonAsync<StatusSummary[]>())!).Id);
        }
        using (var response = await fixture.Http.GetAsync("api/statuses/" + sent.Id))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode); NoStore(response);
            var status = (await response.Content.ReadFromJsonAsync<StoredStatus>())!;
            Assert.Equal(data.Member.Id, status.Payload.RecipientId); Assert.Equal(sent.Ciphertext, status.Ciphertext);
            Assert.Equal("SYNTHETIC PRIVATE STATUS", Encoding.UTF8.GetString(StatusCryptography.Decrypt(status,
                data.Member.Id, data.Identities[data.Member.Id].EncryptionKey, data.State.GetDevice(data.Author.Id)!.SigningPublicKey)));
        }
        foreach (var outsider in new[] { data.Outside, data.Admin })
        {
            fixture.As(outsider);
            using var feed = await fixture.Http.GetAsync("api/statuses"); NoStore(feed);
            Assert.Empty((await feed.Content.ReadFromJsonAsync<StatusSummary[]>())!);
            using var detail = await fixture.Http.GetAsync("api/statuses/" + sent.Id); NoStore(detail);
            using var delete = await fixture.Http.DeleteAsync("api/statuses/" + sent.Id); NoStore(delete);
            Assert.Equal(HttpStatusCode.NotFound, detail.StatusCode); Assert.Equal(HttpStatusCode.NotFound, delete.StatusCode);
        }
        fixture.As(data.Author);
        using (var response = await fixture.Http.DeleteAsync("api/statuses/" + sent.Id))
        { Assert.Equal(HttpStatusCode.NoContent, response.StatusCode); NoStore(response); }
        fixture.As(data.Member);
        using (var response = await fixture.Http.GetAsync("api/statuses/" + sent.Id))
        { Assert.Equal(HttpStatusCode.NotFound, response.StatusCode); NoStore(response); }
    }

    [Fact]
    public async Task AnonymousStatusEndpointsReturnUnauthorizedWithoutBodyOrAudienceLeaks()
    {
        await using var fixture = await HttpFixture.Start(); var sent = fixture.Data.Add();
        using var feed = await fixture.Http.GetAsync("api/statuses");
        using var detail = await fixture.Http.GetAsync("api/statuses/" + sent.Id);
        using var create = await fixture.Http.PostAsJsonAsync("api/statuses", fixture.Data.Request());
        using var delete = await fixture.Http.DeleteAsync("api/statuses/" + sent.Id);
        foreach (var response in new[] { feed, detail, create, delete })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode); NoStore(response);
            Assert.Equal("unauthorized", (await response.Content.ReadFromJsonAsync<ApiError>())!.Code);
            Assert.DoesNotContain(sent.Ciphertext, await response.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task InvalidMalformedNullAndOversizedJsonCannotMutateStatusCollection()
    {
        await using var fixture = await HttpFixture.Start(); fixture.As(fixture.Data.Author);
        foreach (var body in new[] { "{", "null", "{}", "{\"payloads\":null}", "{\"ciphertext\":123}" })
        {
            using var response = await fixture.Http.PostAsync("api/statuses", new StringContent(body, Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); NoStore(response);
        }
        using (var response = await fixture.Http.PostAsJsonAsync("api/statuses", fixture.Data.Request() with { Kind = "voice" }))
        { Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); NoStore(response); }
        using (var response = await fixture.Http.PostAsync("api/statuses", new StringContent(new string('x', 1_400_001), Encoding.UTF8, "application/json")))
        { Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode); NoStore(response); }
        Assert.Empty(fixture.Data.State.GetStatuses(fixture.Data.Author.Id));
    }

    [Fact]
    public async Task WriteRateLimitDoesNotPreventFeedOrDetailRefresh()
    {
        await using var fixture = await HttpFixture.Start(); fixture.As(fixture.Data.Author);
        var request = fixture.Data.Request(); var sent = await fixture.Publish(request);
        for (var index = 0; index < 14; index++)
        {
            using var response = await fixture.Http.PostAsJsonAsync("api/statuses", request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode); NoStore(response);
        }
        using (var response = await fixture.Http.DeleteAsync("api/statuses/" + sent.Id))
        { Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode); NoStore(response); }
        for (var index = 0; index < 20; index++)
        {
            using var response = await fixture.Http.GetAsync("api/statuses/" + sent.Id);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode); NoStore(response);
        }
        using var feed = await fixture.Http.GetAsync("api/statuses"); Assert.Equal(HttpStatusCode.OK, feed.StatusCode); NoStore(feed);
    }

    [Fact]
    public async Task BlockedOrBannedStatusRelationsAreRejectedAtHttpBoundaryAndExpiryIsInvisible()
    {
        await using var fixture = await HttpFixture.Start(); var data = fixture.Data; fixture.As(data.Author);
        var sent = await fixture.Publish(data.Request());
        Assert.True(data.State.SetBlocked(data.Member.Id, data.Author.Id, true));
        using (var response = await fixture.Http.PostAsJsonAsync("api/statuses", data.Request()))
        { Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode); NoStore(response); }
        fixture.As(data.Member);
        using (var response = await fixture.Http.GetAsync("api/statuses/" + sent.Id))
        { Assert.Equal(HttpStatusCode.NotFound, response.StatusCode); NoStore(response); }
        Assert.True(data.State.SetBlocked(data.Member.Id, data.Author.Id, false));
        data.Time.Now = sent.ExpiresAt;
        using (var response = await fixture.Http.GetAsync("api/statuses"))
        { Assert.Empty((await response.Content.ReadFromJsonAsync<StatusSummary[]>())!); NoStore(response); }
        Assert.True(data.State.ModerateUser(data.Member.Id, "ban", null));
        using (var response = await fixture.Http.GetAsync("api/statuses"))
        { Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode); NoStore(response); }
    }

    private static void NoStore(HttpResponseMessage response) => Assert.True(response.Headers.CacheControl?.NoStore);

    private sealed class HttpFixture(WebApplication app, StatusTests.Fixture data, HttpClient http) : IAsyncDisposable
    {
        public StatusTests.Fixture Data { get; } = data;
        public HttpClient Http { get; } = http;
        public static async Task<HttpFixture> Start()
        {
            var data = new StatusTests.Fixture(); var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
            builder.Services.AddSingleton(data.State);
            builder.Services.AddSingleton(new ChatDatabase(Options.Create(new DatabaseOptions())));
            builder.Services.AddRateLimiter(options =>
            {
                options.RejectionStatusCode = 429;
                options.AddPolicy("statuses", context => RateLimitPartition.GetFixedWindowLimiter(
                    context.Items["UserId"]?.ToString() ?? "anonymous", _ => new FixedWindowRateLimiterOptions
                    { PermitLimit = 15, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
            });
            var app = builder.Build(); app.UseMiddleware<ChatAuthenticationMiddleware>(); app.UseRateLimiter(); app.MapStatusEndpoints();
            await app.StartAsync();
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            return new HttpFixture(app, data, new HttpClient { BaseAddress = new Uri(address) });
        }
        public void As(ChatUser user) => Http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", Data.State.CreateSession(user.Id));
        public async Task<StoredStatus> Publish(SendStatusRequest request)
        {
            using var response = await Http.PostAsJsonAsync("api/statuses", request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode); NoStore(response);
            return (await response.Content.ReadFromJsonAsync<StoredStatus>())!;
        }
        public async ValueTask DisposeAsync()
        {
            Http.Dispose(); Data.Dispose(); await app.StopAsync(); await app.DisposeAsync();
        }
    }
}
