using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MTKChat.Server.Configuration;
using MTKChat.Server.Services;
using Xunit;

namespace MTKChat.Cryptography.Tests;

public sealed class InviteLandingTests
{
    [Fact]
    public async Task PublicInvitePageAndAssetsExposeNoMembershipAndHaveRestrictiveHeaders()
    {
        var state = new ChatState();
        var user = state.UpsertSiteUser(Guid.NewGuid(), "Landing QA", "landing@tests.invalid", "user");
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(state);
        builder.Services.AddSingleton(new ChatDatabase(Options.Create(new DatabaseOptions())));
        await using var app = builder.Build();
        app.UseMiddleware<ChatAuthenticationMiddleware>(); app.MapGroupInviteLanding();
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = new Uri(address) };
        using (var response = await client.GetAsync("invite"))
        {
            Assert.Equal(HttpStatusCode.Found, response.StatusCode);
            Assert.Equal("invite/", response.Headers.Location!.OriginalString);
        }
        using (var response = await client.GetAsync("invite/"))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("text/html", response.Content.Headers.ContentType!.MediaType);
            Assert.Contains("no-store", response.Headers.CacheControl!.ToString());
            Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
            Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
            var csp = response.Headers.GetValues("Content-Security-Policy").Single();
            Assert.Contains("frame-ancestors 'none'", csp); Assert.DoesNotContain("unsafe-inline", csp);
            var html = await response.Content.ReadAsStringAsync();
            Assert.Contains("method=\"post\"", html); // A missing JS file cannot put passwords into the query string.
            Assert.Contains("noindex,nofollow,noarchive", html);
            Assert.DoesNotContain("MTK Lounge", html); Assert.DoesNotContain("Landing QA", html);
            Assert.DoesNotContain("payloads", html); Assert.Empty(state.GetConversations(user.Id));
        }
        using (var response = await client.GetAsync("invite-assets/invite.js"))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var script = await response.Content.ReadAsStringAsync();
            Assert.Contains("location.hash.slice(1)", script);
            Assert.Contains("group-invites/preview", script); Assert.Contains("group-invites/join", script);
            Assert.Contains("element(\"join-button\").addEventListener(\"click\"", script);
            Assert.Contains("cache: \"no-store\"", script); Assert.Contains("credentials: \"omit\"", script);
            Assert.Contains("auth/logout", script); Assert.Contains("textContent = preview.title", script);
            Assert.DoesNotContain("localStorage", script); Assert.DoesNotContain("sessionStorage", script);
            Assert.DoesNotContain("innerHTML", script); Assert.Empty(state.GetConversations(user.Id));
        }
        using (var response = await client.GetAsync("invite-assets/invite.css"))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("text/css", response.Content.Headers.ContentType!.MediaType);
        }
        using (var response = await client.PostAsync("invite/", null)) Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        await app.StopAsync();
    }

    [Fact]
    public async Task EndingInviteSessionRevokesOnlyItsAuthenticatedBearer()
    {
        var state = new ChatState();
        var user = state.UpsertSiteUser(Guid.NewGuid(), "Session QA", "session@tests.invalid", "user");
        var browser = state.CreateSession(user.Id); var desktop = state.CreateSession(user.Id);
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(state);
        builder.Services.AddSingleton(new ChatDatabase(Options.Create(new DatabaseOptions())));
        await using var app = builder.Build(); app.UseMiddleware<ChatAuthenticationMiddleware>(); app.MapGroupInviteLanding();
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
        using (var response = await client.PostAsync("api/auth/logout", null)) Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(user.Id, state.ResolveSession(browser));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", browser);
        using (var response = await client.PostAsync("api/auth/logout", null)) Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Null(state.ResolveSession(browser)); Assert.Equal(user.Id, state.ResolveSession(desktop));
        using (var response = await client.PostAsync("api/auth/logout", null)) Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(state.GetConversations(user.Id));
        await app.StopAsync();
    }
}
