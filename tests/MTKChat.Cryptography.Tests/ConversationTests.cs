using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MTKChat.Contracts;
using MTKChat.Cryptography;
using MTKChat.Server.Configuration;
using MTKChat.Server.Services;
using Xunit;

namespace MTKChat.Cryptography.Tests;

public sealed class ConversationTests
{
    [Fact]
    public void DirectPairIsAtomicAndSeparateFromTwoPersonGroup()
    {
        var state = new ChatState();
        var a = Add(state, "A"); var b = Add(state, "B");
        var group = state.CreateConversation(a.Id, new("İki kişilik grup", [b.Id]))!;
        var results = new System.Collections.Concurrent.ConcurrentBag<ConversationSummary>();
        Parallel.For(0, 20, i => results.Add(state.GetOrCreateDirect(i % 2 == 0 ? a.Id : b.Id, i % 2 == 0 ? b.Id : a.Id)!));
        var id = Assert.Single(results.Select(r => r.Id).Distinct());
        Assert.NotEqual(group.Id, id);
        Assert.Equal("B", state.GetOrCreateDirect(a.Id, b.Id)!.Title);
        Assert.Equal("A", state.GetOrCreateDirect(b.Id, a.Id)!.Title);
        Assert.Null(state.GetOrCreateDirect(a.Id, a.Id));
        Assert.Null(state.GetOrCreateDirect(a.Id, Guid.Parse("22222222-2222-2222-2222-222222222222")));
        state.SetBlocked(b.Id, a.Id, true);
        Assert.Null(state.GetOrCreateDirect(a.Id, b.Id));
        state.SetBlocked(b.Id, a.Id, false);
        Assert.Equal(id, state.GetOrCreateDirect(a.Id, b.Id)!.Id);
        state.ModerateUser(b.Id, "ban", null);
        Assert.Null(state.GetOrCreateDirect(a.Id, b.Id));
    }

    [Fact]
    public async Task PrivateGroupOnlyListsForMembersAndReturnsOnlyTheirEncryptedEnvelope()
    {
        var state = new ChatState(); var a = Add(state, "A"); var b = Add(state, "B"); var c = Add(state, "C");
        var outside = Add(state, "Outside", "admin"); // Site admin still has no membership bypass.
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(state);
        builder.Services.AddSingleton(new ChatDatabase(Options.Create(new DatabaseOptions())));
        await using var app = builder.Build(); app.UseMiddleware<ChatAuthenticationMiddleware>(); app.MapConversationEndpoints();
        await app.StartAsync();
        var url = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var http = new HttpClient { BaseAddress = new Uri(url) };
        void As(ChatUser u) => http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", state.CreateSession(u.Id));
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("api/conversations")).StatusCode);
        As(a);
        using var created = await http.PostAsJsonAsync("api/conversations", new CreateConversationRequest("Arkadaşlar", [b.Id, c.Id]));
        created.EnsureSuccessStatusCode(); var group = (await created.Content.ReadFromJsonAsync<ConversationSummary>())!;
        Assert.Equal(3, group.Participants.Count); Assert.DoesNotContain(group.Participants, u => u.IsAgent);
        using var da = DeviceIdentity.Create(); using var db = DeviceIdentity.Create(); using var dc = DeviceIdentity.Create();
        var text = Encoding.UTF8.GetBytes("Yalnızca grubumuz"); var messageId = Guid.NewGuid(); var at = DateTimeOffset.UtcNow;
        EncryptedPayload To(ChatUser user, DeviceIdentity device) => MessageCryptography.Encrypt(text, messageId, group.Id, a.Id,
            user.Id, at, device.ExportEncryptionPublicKey(), da.SigningKey);
        var message = new SendMessageRequest(messageId, group.Id, "text", at, null, [To(a, da), To(b, db), To(c, dc)], null);
        Assert.True(state.AddMessage(a.Id, message, out var stored));
        foreach (var (user, device) in new[] { (a, da), (b, db), (c, dc) })
        {
            As(user);
            Assert.Contains((await http.GetFromJsonAsync<ConversationSummary[]>("api/conversations"))!, g => g.Id == group.Id);
            var received = Assert.Single((await http.GetFromJsonAsync<StoredMessage[]>($"api/conversations/{group.Id}/messages"))!);
            var envelope = Assert.Single(received.Payloads); Assert.Equal(user.Id, envelope.RecipientId);
            Assert.Equal(text, MessageCryptography.Decrypt(envelope, messageId, group.Id, a.Id, at, device.EncryptionKey, da.ExportSigningPublicKey()));
        }
        As(outside);
        Assert.DoesNotContain((await http.GetFromJsonAsync<ConversationSummary[]>("api/conversations"))!, g => g.Id == group.Id);
        foreach (var suffix in new[] { "messages", "members", "presence" })
            Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync($"api/conversations/{group.Id}/{suffix}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await http.PostAsync($"api/conversations/{group.Id}/presence", null)).StatusCode);
        Assert.False(state.AddMessage(outside.Id, message with { ClientMessageId = Guid.NewGuid() }, out _));
        Assert.False(state.AddMessage(a.Id, message with { ClientMessageId = Guid.NewGuid(), Payloads = [To(outside, dc)] }, out _));
        Assert.Equal(DeleteResult.Forbidden, state.DeleteMessage(outside.Id, stored!.Id, false));
        As(a);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsJsonAsync("api/conversations", new CreateConversationRequest("", [b.Id]))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsJsonAsync("api/conversations", new CreateConversationRequest("Grup", [Guid.NewGuid()]))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsJsonAsync("api/conversations", new CreateConversationRequest("Grup", [a.Id]))).StatusCode);
        using var direct = await http.PostAsJsonAsync("api/conversations/direct", new CreateDirectConversationRequest(b.Id));
        Assert.Equal("direct", (await direct.Content.ReadFromJsonAsync<ConversationSummary>())!.Kind);
        state.SetBlocked(b.Id, a.Id, true);
        Assert.Equal(HttpStatusCode.Forbidden, (await http.PostAsJsonAsync("api/conversations/direct", new CreateDirectConversationRequest(b.Id))).StatusCode);
        await app.StopAsync();
    }

    [Fact]
    public void OlderSnapshotDefaultsToGroupAndDirectKindSurvivesRestore()
    {
        var state = new ChatState(); var a = Add(state, "A"); var b = Add(state, "B");
        var oldGroup = Guid.NewGuid(); var direct = Guid.NewGuid();
        var snapshot = new { Version = 1, Conversations = new object[] {
            new { Id = oldGroup, Title = "Eski grup", Members = new[] { a.Id, b.Id }, Messages = Array.Empty<Guid>() },
            new { Id = direct, Title = "Özel sohbet", Members = new[] { a.Id, b.Id }, Messages = Array.Empty<Guid>(), Kind = "direct" } },
            Messages = Array.Empty<object>(), Devices = Array.Empty<object>(), HiddenMessages = Array.Empty<object>(),
            BannedUsers = Array.Empty<Guid>(), MutedUsers = Array.Empty<object>(), ChatMutedUsers = Array.Empty<object>(),
            ChatBannedUsers = Array.Empty<object>(), Blocks = Array.Empty<object>() };
        typeof(ChatState).GetMethod("RestoreSnapshot", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(state, [JsonSerializer.Serialize(snapshot)]);
        Assert.Equal("group", state.GetConversations(a.Id).Single(c => c.Id == oldGroup).Kind);
        Assert.Equal(direct, state.GetOrCreateDirect(a.Id, b.Id)!.Id);
    }
    private static ChatUser Add(ChatState state, string name, string role = "user") =>
        state.UpsertSiteUser(Guid.NewGuid(), name, name + "@example.test", role);
}
