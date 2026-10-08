using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MTKChat.Contracts;
using MTKChat.Cryptography;
using MTKChat.Server.Configuration;
using MTKChat.Server.Services;
using Xunit;

namespace MTKChat.Cryptography.Tests;

public sealed class CallTests
{
    [Fact]
    public void SessionKeysAuthenticatePeersRejectForgeryReplayAndWrongSession()
    {
        var call = Guid.NewGuid(); var a = Guid.NewGuid(); var b = Guid.NewGuid();
        using var da = DeviceIdentity.Create(); using var db = DeviceIdentity.Create();
        using var ia = new CallIdentity(call, a, da.SigningKey); using var ib = new CallIdentity(call, b, db.SigningKey);
        using var ab = ia.Connect(b, ib.Join, db.ExportSigningPublicKey());
        using var ba = ib.Connect(a, ia.Join, da.ExportSigningPublicKey());
        Assert.Equal(ab.VerificationCode, ba.VerificationCode);
        var pcm = RandomNumberGenerator.GetBytes(3200);
        var frame = ab.Encrypt(pcm);
        Assert.ThrowsAny<CryptographicException>(() => ba.Decrypt(frame with { Tag = Convert.ToBase64String(new byte[16]) }));
        Assert.Equal(pcm, ba.Decrypt(frame));
        Assert.Throws<CryptographicException>(() => ba.Decrypt(frame));
        var next = ab.Encrypt(pcm);
        Assert.Throws<CryptographicException>(() => ba.Decrypt(next with { RecipientSession = Guid.NewGuid() }));
        Assert.Equal(pcm, ba.Decrypt(next));
        Assert.Throws<CryptographicException>(() => CallIdentity.Verify(Guid.NewGuid(), b, ib.Join, db.ExportSigningPublicKey()));
        Assert.Throws<CryptographicException>(() => ia.Connect(b, ib.Join, da.ExportSigningPublicKey()));
        using var rejoin = new CallIdentity(call, b, db.SigningKey);
        using var newCipher = rejoin.Connect(a, ia.Join, da.ExportSigningPublicKey());
        Assert.Throws<CryptographicException>(() => newCipher.Decrypt(ab.Encrypt(pcm)));
    }

    [Fact]
    public async Task ThreePeopleExchangeOnlyRecipientCiphertextThroughRealHttpRoutes()
    {
        var state = new ChatState();
        using var da = DeviceIdentity.Create(); using var db = DeviceIdentity.Create(); using var dc = DeviceIdentity.Create();
        var a = Add(state, da, "Alice"); var b = Add(state, db, "Bob"); var c = Add(state, dc, "Cem");
        var outsider = state.UpsertSiteUser(Guid.NewGuid(), "Outside", "outside@example.test", "user");
        var group = state.CreateConversation(a.Id, new CreateConversationRequest("Test", [b.Id, c.Id]))!;
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(state); builder.Services.AddSingleton<CallRegistry>();
        builder.Services.AddSingleton(new ChatDatabase(Options.Create(new DatabaseOptions())));
        await using var app = builder.Build();
        app.UseMiddleware<ChatAuthenticationMiddleware>(); app.MapCallEndpoints();
        app.MapGet("/api/admin/probe", () => Results.Ok("admin"));
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var http = new HttpClient { BaseAddress = new Uri(address) };
        void As(ChatUser user) => http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", state.CreateSession(user.Id));
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("api/calls")).StatusCode);
        As(a);
        Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync("api/admin/probe?role=admin")).StatusCode);
        state.UpsertSiteUser(a.Id, a.DisplayName, a.Email, "admin");
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("api/admin/probe")).StatusCode);
        state.UpsertSiteUser(a.Id, a.DisplayName, a.Email, "user");
        Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync("api/admin/probe")).StatusCode);
        using var started = await http.PostAsJsonAsync("api/calls", new StartCallRequest(group.Id, [b.Id, c.Id]));
        Assert.Equal(HttpStatusCode.OK, started.StatusCode);
        var call = (await started.Content.ReadFromJsonAsync<CallView>())!;
        using var ia = new CallIdentity(call.Id, a.Id, da.SigningKey);
        using var ib = new CallIdentity(call.Id, b.Id, db.SigningKey);
        using var ic = new CallIdentity(call.Id, c.Id, dc.SigningKey);
        Assert.Equal(HttpStatusCode.OK, (await http.PostAsJsonAsync($"api/calls/{call.Id}/join", ia.Join)).StatusCode);
        // Lost successful Join responses must not turn a retry into "already in call".
        using var joinedAgain = await http.PostAsJsonAsync($"api/calls/{call.Id}/join", ia.Join);
        Assert.Equal(HttpStatusCode.OK, joinedAgain.StatusCode);
        Assert.Equal("joined", (await joinedAgain.Content.ReadFromJsonAsync<CallView>())!
            .Invitations!.Single(i => i.UserId == a.Id).State);
        As(outsider);
        Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync($"api/calls/{call.Id}")).StatusCode);
        As(b);
        Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync($"api/calls/{call.Id}/frames")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsJsonAsync($"api/calls/{call.Id}/join", ia.Join)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.PostAsJsonAsync($"api/calls/{call.Id}/join", ib.Join)).StatusCode);
        As(c);
        Assert.Equal(HttpStatusCode.OK, (await http.PostAsJsonAsync($"api/calls/{call.Id}/join", ic.Join)).StatusCode);
        using var ab = ia.Connect(b.Id, ib.Join, db.ExportSigningPublicKey());
        using var ac = ia.Connect(c.Id, ic.Join, dc.ExportSigningPublicKey());
        using var ba = ib.Connect(a.Id, ia.Join, da.ExportSigningPublicKey());
        using var ca = ic.Connect(a.Id, ia.Join, da.ExportSigningPublicKey());
        var pcm = RandomNumberGenerator.GetBytes(3200);
        As(a);
        Assert.Equal(HttpStatusCode.NoContent, (await http.PostAsJsonAsync($"api/calls/{call.Id}/frames", new[] { ab.Encrypt(pcm), ac.Encrypt(pcm) })).StatusCode);
        As(b);
        var toB = await http.GetFromJsonAsync<CallFrame[]>($"api/calls/{call.Id}/frames");
        Assert.Equal(pcm, ba.Decrypt(Assert.Single(toB!)));
        As(c);
        var toC = await http.GetFromJsonAsync<CallFrame[]>($"api/calls/{call.Id}/frames");
        Assert.Equal(pcm, ca.Decrypt(Assert.Single(toC!)));
        Assert.NotEqual(toB![0].Ciphertext, toC![0].Ciphertext);
        Assert.Empty(state.GetMessages(a.Id, group.Id, null));
        As(a);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsJsonAsync($"api/calls/{call.Id}/frames", new[] { ab.Encrypt(pcm) with { SenderId = c.Id } })).StatusCode);
        await http.PutAsJsonAsync($"api/calls/{call.Id}/mute", new CallMute(true));
        await http.PostAsJsonAsync($"api/calls/{call.Id}/frames", new[] { ab.Encrypt(pcm) });
        As(b); Assert.Empty((await http.GetFromJsonAsync<CallFrame[]>($"api/calls/{call.Id}/frames"))!);
        state.ModerateChatUser(group.Id, b.Id, "mute", 5);
        Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync($"api/calls/{call.Id}/frames")).StatusCode);
        await app.StopAsync();
    }

    [Fact]
    public void CallsRespectBlocksIdleExpiryAndRingTimeout()
    {
        var state = new ChatState(); var clock = new TestClock(); var calls = new CallRegistry(state, clock);
        using var da = DeviceIdentity.Create(); using var db = DeviceIdentity.Create();
        var a = Add(state, da, "A"); var b = Add(state, db, "B");
        var group = state.CreateConversation(a.Id, new CreateConversationRequest("Direct", [b.Id]))!;
        state.SetBlocked(a.Id, b.Id, true);
        Assert.Throws<CallFailure>(() => calls.Start(a.Id, new(group.Id, [b.Id])));
        state.SetBlocked(a.Id, b.Id, false);
        var call = calls.Start(a.Id, new(group.Id, [b.Id]));
        using var ia = new CallIdentity(call.Id, a.Id, da.SigningKey);
        calls.Join(call.Id, a.Id, ia.Join);
        using var ib = new CallIdentity(call.Id, b.Id, db.SigningKey);
        calls.Join(call.Id, b.Id, ib.Join);
        using var cipher = ia.Connect(b.Id, ib.Join, db.ExportSigningPublicKey());
        for (var i = 0; i < 2; i++) calls.Send(call.Id, a.Id, Enumerable.Range(0, 20).Select(_ => cipher.Encrypt(new byte[3200])).ToArray());
        var queued = calls.Receive(call.Id, b.Id);
        Assert.Equal(30, queued.Count);
        Assert.Equal(10, queued[0].Sequence);
        clock.Now += TimeSpan.FromSeconds(15);
        calls.Receive(call.Id, a.Id); // Caller is still connected when the other peer disappears.
        clock.Now += TimeSpan.FromSeconds(6);
        Assert.Empty(calls.List(b.Id));
        Assert.Empty(calls.List(a.Id));
        var second = calls.Start(a.Id, new(group.Id, [b.Id]));
        clock.Now += TimeSpan.FromSeconds(61);
        Assert.Empty(calls.List(a.Id));
    }
    private static ChatUser Add(ChatState state, DeviceIdentity device, string name)
    {
        var user = state.UpsertSiteUser(Guid.NewGuid(), name, name + "@example.test", "user");
        state.RegisterDevice(user.Id, new("test", device.ExportEncryptionPublicKey(), device.ExportSigningPublicKey()));
        return user;
    }
    private sealed class TestClock : TimeProvider
    {
        internal DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
