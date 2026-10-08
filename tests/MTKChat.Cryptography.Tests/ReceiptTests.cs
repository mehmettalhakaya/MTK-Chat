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

public sealed class ReceiptTests
{
    private static ChatUser User(ChatState state, string name) => state.UpsertSiteUser(Guid.NewGuid(), name, name + "@example.test", "user");
    private static StoredMessage Send(ChatState state, Guid sender, Guid room, Guid[] recipients,
        DateTimeOffset? expires = null, string kind = "text")
    {
        // Receipt state tests don't exercise crypto: the separate crypto/HTTP send tests cover signatures.
        var payloads = recipients.Select(id => new EncryptedPayload("test", "", "", "encrypted", "", "", id)).ToArray();
        Assert.True(state.AddMessage(sender, new(Guid.NewGuid(), room, kind, DateTimeOffset.UtcNow, expires, payloads, null), out var message));
        return message!;
    }
    private static MessageDelivery Delivery(ChatState state, Guid sender, StoredMessage message) =>
        state.GetMessages(sender, message.ConversationId, null).Single(m => m.Id == message.Id).Delivery!;

    [Fact]
    public void PresenceAndDownloadDoNotFabricateDeliveryOrReadAndAckIsMonotonic()
    {
        var state = new ChatState(); var a = User(state, "A"); var b = User(state, "B");
        var room = state.GetOrCreateDirect(a.Id, b.Id)!;
        var message = Send(state, a.Id, room.Id, [a.Id, b.Id]);
        state.TouchActivity(b.Id, room.Id);
        var downloaded = Assert.Single(state.GetDeliveryInbox(b.Id));
        Assert.Equal(b.Id, Assert.Single(downloaded.Payloads).RecipientId);
        Assert.Null(downloaded.Delivery); // Other users' receipt metadata is sender-only.
        Assert.Equal("sent", Delivery(state, a.Id, message).Status);
        Assert.Equal(1, state.GetConversations(b.Id).Single(c => c.Id == room.Id).UnreadCount);
        Assert.Equal(0, state.AcknowledgeMessages(a.Id, [message.Id], true)); // Cannot mark own send read.
        Assert.Equal(1, state.AcknowledgeMessages(b.Id, [message.Id], false));
        Assert.Empty(state.GetDeliveryInbox(b.Id));
        var deliveredAt = Assert.Single(Delivery(state, a.Id, message).Recipients).DeliveredAt;
        Assert.NotNull(deliveredAt);
        Assert.Equal("delivered", Delivery(state, a.Id, message).Status);
        Assert.Equal(1, state.GetConversations(b.Id).Single(c => c.Id == room.Id).UnreadCount);
        Assert.Equal(1, state.AcknowledgeMessages(b.Id, [message.Id, message.Id], true));
        var read = Delivery(state, a.Id, message);
        Assert.Equal("read", read.Status);
        var receipt = Assert.Single(read.Recipients);
        Assert.Equal(deliveredAt, receipt.DeliveredAt);
        Assert.True(receipt.ReadAt >= receipt.DeliveredAt);
        Assert.Equal(0, state.GetConversations(b.Id).Single(c => c.Id == room.Id).UnreadCount);
        Assert.Equal(0, state.AcknowledgeMessages(b.Id, [message.Id], false));
        Assert.Equal(0, state.AcknowledgeMessages(b.Id, [message.Id], true));
        Assert.Equal(receipt, Assert.Single(Delivery(state, a.Id, message).Recipients));
        Assert.Null(Assert.Single(state.GetMessages(b.Id, room.Id, null)).Delivery);
    }

    [Fact]
    public void GroupChecksWaitForEveryOriginalRecipientAndReadImpliesDelivery()
    {
        var state = new ChatState(); var a = User(state, "A"); var b = User(state, "B"); var c = User(state, "C");
        var group = state.CreateConversation(a.Id, new("Grup", [b.Id, c.Id]))!;
        var message = Send(state, a.Id, group.Id, [a.Id, b.Id, c.Id]);
        Assert.Equal(1, state.AcknowledgeMessages(b.Id, [message.Id], true));
        Assert.Equal("sent", Delivery(state, a.Id, message).Status);
        Assert.Equal(1, state.AcknowledgeMessages(c.Id, [message.Id], false));
        Assert.Equal("delivered", Delivery(state, a.Id, message).Status);
        Assert.Equal(1, state.AcknowledgeMessages(c.Id, [message.Id], true));
        Assert.Equal("read", Delivery(state, a.Id, message).Status);
        Assert.Equal(2, Delivery(state, a.Id, message).Recipients.Count);
        Assert.Equal(0, state.AcknowledgeMessages(User(state, "Outside").Id, [message.Id], true));
    }

    [Fact]
    public void InboxCoversUnselectedRoomsInBoundedBatchesAndSkipsNonRecipients()
    {
        var state = new ChatState(); var a = User(state, "A"); var b = User(state, "B"); var c = User(state, "C");
        var room = state.GetOrCreateDirect(a.Id, b.Id)!;
        var other = state.GetOrCreateDirect(c.Id, b.Id)!;
        for (var i = 0; i < 35; i++) Send(state, a.Id, room.Id, [a.Id, b.Id]);
        var secondRoom = Send(state, c.Id, other.Id, [c.Id, b.Id]);
        Send(state, a.Id, room.Id, [a.Id]); // Membership is not evidence of an envelope for B.
        var batch = state.GetDeliveryInbox(b.Id);
        Assert.Equal(32, batch.Count);
        state.AcknowledgeMessages(b.Id, batch.Select(m => m.Id).ToArray(), false);
        var remaining = state.GetDeliveryInbox(b.Id);
        Assert.Equal(4, remaining.Count); Assert.Contains(remaining, m => m.Id == secondRoom.Id);
        state.AcknowledgeMessages(b.Id, remaining.Select(m => m.Id).ToArray(), false);
        Assert.Empty(state.GetDeliveryInbox(b.Id));
        Assert.Equal(35, state.GetConversations(b.Id).Single(cn => cn.Id == room.Id).UnreadCount);
    }

    [Fact]
    public void HiddenBlockedDeletedAndExpiredMessagesAreNotAcknowledged()
    {
        var state = new ChatState(); var a = User(state, "A"); var b = User(state, "B");
        var room = state.GetOrCreateDirect(a.Id, b.Id)!;
        var hidden = Send(state, a.Id, room.Id, [a.Id, b.Id]);
        var deleted = Send(state, a.Id, room.Id, [a.Id, b.Id]);
        var expired = Send(state, a.Id, room.Id, [a.Id, b.Id], DateTimeOffset.UtcNow.AddSeconds(-1));
        var valid = Send(state, a.Id, room.Id, [a.Id, b.Id]);
        state.DeleteMessage(b.Id, hidden.Id, false); state.DeleteMessage(a.Id, deleted.Id, true);
        state.SetBlocked(b.Id, a.Id, true);
        Assert.Empty(state.GetDeliveryInbox(b.Id));
        Assert.Equal(0, state.AcknowledgeMessages(b.Id, [valid.Id], true));
        state.SetBlocked(b.Id, a.Id, false);
        Assert.Equal(valid.Id, Assert.Single(state.GetDeliveryInbox(b.Id)).Id);
        Assert.Equal(1, state.AcknowledgeMessages(b.Id, [hidden.Id, deleted.Id, expired.Id, Guid.NewGuid(), valid.Id], true));
        Assert.Equal("sent", Delivery(state, a.Id, hidden).Status);
        Assert.Equal("read", Delivery(state, a.Id, valid).Status);
        Assert.Equal(-2, state.ClearHistory(a.Id, room.Id, true));
        Assert.Equal("read", Delivery(state, a.Id, valid).Status);
        Assert.Equal(DeleteResult.Deleted, state.DeleteMessage(a.Id, valid.Id, true));
        Assert.Equal(0, state.AcknowledgeMessages(b.Id, [valid.Id], true));
        var receipts = typeof(ChatState).GetField("_receipts", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(state)!;
        Assert.Equal(0, (int)receipts.GetType().GetProperty("Count")!.GetValue(receipts)!);
    }

    [Fact]
    public void EmptyRecipientSetIsNeverReadAndMediaDoesNotWaitForBotsThatCannotConsumeIt()
    {
        var state = new ChatState(); var a = User(state, "A"); var b = User(state, "B");
        var bot = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var group = state.CreateConversation(a.Id, new("Grup", [b.Id, bot]))!;
        Assert.Equal("sent", Delivery(state, a.Id, Send(state, a.Id, group.Id, [a.Id])).Status);
        var voice = Send(state, a.Id, group.Id, [a.Id, b.Id, bot], kind: "audio/wav");
        state.AcknowledgeMessages(b.Id, [voice.Id], true);
        Assert.Equal("read", Delivery(state, a.Id, voice).Status);
        Assert.Equal(b.Id, Assert.Single(Delivery(state, a.Id, voice).Recipients).UserId);
        var text = Send(state, a.Id, group.Id, [a.Id, b.Id, bot]);
        state.AcknowledgeMessages(b.Id, [text.Id], true);
        Assert.Equal("sent", Delivery(state, a.Id, text).Status);
        state.AcknowledgeMessages(bot, [text.Id], true);
        Assert.Equal("read", Delivery(state, a.Id, text).Status);
    }

    [Fact]
    public void ReceiptsSurviveSnapshotRestoreAndLegacySnapshotHasNoInventedReads()
    {
        var state = new ChatState(); var a = User(state, "A"); var b = User(state, "B");
        var room = state.GetOrCreateDirect(a.Id, b.Id)!;
        var message = Send(state, a.Id, room.Id, [a.Id, b.Id]);
        state.AcknowledgeMessages(b.Id, [message.Id], true);
        var receipt = Assert.Single(Delivery(state, a.Id, message).Recipients);
        var snapshot = new { Version = 1, Conversations = new[] { new {
            Id = room.Id, Title = room.Title, Members = new[] { a.Id, b.Id }, Messages = new[] { message.Id }, Kind = "direct" } },
            Messages = new[] { message }, Devices = Array.Empty<object>(), HiddenMessages = Array.Empty<object>(),
            BannedUsers = Array.Empty<Guid>(), MutedUsers = Array.Empty<object>(), ChatMutedUsers = Array.Empty<object>(),
            ChatBannedUsers = Array.Empty<object>(), Blocks = Array.Empty<object>(),
            Receipts = new[] { new { MessageId = message.Id, Receipt = receipt } } };
        var restore = typeof(ChatState).GetMethod("RestoreSnapshot", BindingFlags.NonPublic | BindingFlags.Instance)!;
        restore.Invoke(state, [JsonSerializer.Serialize(snapshot)]);
        Assert.Equal("read", Delivery(state, a.Id, message).Status);
        Assert.Equal(receipt, Assert.Single(Delivery(state, a.Id, message).Recipients));
        var legacy = JsonSerializer.SerializeToNode(snapshot)!; legacy.AsObject().Remove("Receipts");
        restore.Invoke(state, [legacy.ToJsonString()]);
        Assert.Equal("sent", Delivery(state, a.Id, message).Status);
        Assert.Equal(message.Id, Assert.Single(state.GetDeliveryInbox(b.Id)).Id);
    }

    [Fact]
    public async Task ReceiptHttpRequiresSessionAndCannotAcknowledgeAnotherUserOrOutsideGroup()
    {
        var state = new ChatState(); var a = User(state, "A"); var b = User(state, "B"); var outside = User(state, "Outside");
        var room = state.GetOrCreateDirect(a.Id, b.Id)!;
        var message = Send(state, a.Id, room.Id, [a.Id, b.Id]);
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(state);
        builder.Services.AddSingleton(new ChatDatabase(Options.Create(new DatabaseOptions())));
        await using var app = builder.Build(); app.UseMiddleware<ChatAuthenticationMiddleware>(); app.MapConversationEndpoints();
        await app.StartAsync();
        using var http = new HttpClient { BaseAddress = new Uri(app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
        var ack = new AcknowledgeMessagesRequest([message.Id], true);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("api/messages/inbox")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.PostAsJsonAsync("api/messages/ack", ack)).StatusCode);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", state.CreateSession(outside.Id));
        Assert.Empty((await http.GetFromJsonAsync<StoredMessage[]>("api/messages/inbox"))!);
        // No ID oracle: stale/unknown/unauthorized IDs are all ignored, and never change B's status.
        Assert.Equal(HttpStatusCode.NoContent, (await http.PostAsJsonAsync("api/messages/ack", ack)).StatusCode);
        Assert.Equal("sent", Delivery(state, a.Id, message).Status);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", state.CreateSession(b.Id));
        Assert.Equal(b.Id, Assert.Single(Assert.Single((await http.GetFromJsonAsync<StoredMessage[]>("api/messages/inbox"))!).Payloads).RecipientId);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsJsonAsync("api/messages/ack", new AcknowledgeMessagesRequest([]))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsJsonAsync("api/messages/ack", new { MessageIds = (Guid[]?)null })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsJsonAsync("api/messages/ack", new AcknowledgeMessagesRequest(Enumerable.Repeat(message.Id, 101).ToArray()))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsJsonAsync("api/messages/ack", new AcknowledgeMessagesRequest([Guid.Empty]))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await http.PostAsJsonAsync("api/messages/ack", ack)).StatusCode);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", state.CreateSession(a.Id));
        var received = Assert.Single((await http.GetFromJsonAsync<StoredMessage[]>($"api/conversations/{room.Id}/messages"))!);
        Assert.Equal("read", received.Delivery!.Status);
        Assert.Equal(b.Id, Assert.Single(received.Delivery.Recipients).UserId);
        await app.StopAsync();
    }
}
