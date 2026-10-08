using MTKChat.Contracts;
using MTKChat.Server.Services;
using Xunit;

namespace MTKChat.Cryptography.Tests;

public sealed class ChatStateTests
{
    private static readonly Guid DemoId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid LobbyId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    [Fact]
    public void DuplicateClientMessageIsStoredOnlyOnce()
    {
        var state = NewState();
        var request = NewRequest();

        Assert.True(state.AddMessage(DemoId, request, out var first));
        Assert.True(state.AddMessage(DemoId, request, out var second));

        Assert.Equal(first!.Id, second!.Id);
        Assert.Single(state.GetMessages(DemoId, LobbyId, null));
        Assert.True(state.NewMessages.TryRead(out _));
        Assert.False(state.NewMessages.TryRead(out _));
    }

    [Fact]
    public void PersonalClearNeverRemovesPendingAgentSourceOrEveryoneHistory()
    {
        var state = NewState();
        Assert.True(state.AddMessage(DemoId, NewRequest(), out var stored));
        Assert.True(state.IsMessageActive(stored!.Id));

        Assert.Equal(-2, state.ClearHistory(DemoId, LobbyId, true));
        Assert.Single(state.GetMessages(DemoId, LobbyId, null));
        Assert.Equal(1, state.ClearHistory(DemoId, LobbyId, false));

        Assert.True(state.IsMessageActive(stored.Id));
        Assert.Empty(state.GetMessages(DemoId, LobbyId, null));
    }

    [Fact]
    public void BanInvalidatesExistingSession()
    {
        var state = NewState();
        var token = state.CreateSession(DemoId);
        Assert.Equal(DemoId, state.ResolveSession(token));

        Assert.True(state.ModerateUser(DemoId, "ban", null));

        Assert.Null(state.ResolveSession(token));
    }

    [Fact]
    public void BlockingEitherParticipantRejectsDirectMessages()
    {
        var state = NewState();
        var other = state.UpsertSiteUser(Guid.NewGuid(), "Diğer", "other@example.com", "user");
        var direct = state.CreateConversation(DemoId,
            new CreateConversationRequest("Özel sohbet", new[] { other.Id }));
        Assert.NotNull(direct);
        Assert.True(state.SetBlocked(other.Id, DemoId, true));

        var request = NewRequest() with
        {
            ConversationId = direct.Id,
            Payloads = new[] { new EncryptedPayload("test", "", "", "", "", "", other.Id) }
        };
        Assert.False(state.AddMessage(DemoId, request, out _));
    }

    [Fact]
    public void ExpiredMessageDoesNotAppearInConversationPreview()
    {
        var state = NewState();
        var request = NewRequest() with
        {
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1)
        };
        Assert.True(state.AddMessage(DemoId, request, out _));

        var preview = state.GetConversations(DemoId).Single(item => item.Id == LobbyId);
        Assert.Null(preview.LastMessageAt);
    }

    [Fact]
    public void DifferentDeviceCannotSilentlyReplaceExistingKey()
    {
        var state = NewState();
        var first = new RegisterDeviceRequest("first", "encryption-a", "signing-a");
        var second = new RegisterDeviceRequest("second", "encryption-b", "signing-b");

        Assert.True(state.RegisterDevice(DemoId, first));
        Assert.True(state.RegisterDevice(DemoId, first));
        Assert.False(state.RegisterDevice(DemoId, second));
        Assert.Equal("encryption-a", state.GetDevice(DemoId)!.EncryptionPublicKey);
    }

    [Fact]
    public void ChatMuteAndBanApplyOnlyToSelectedConversation()
    {
        var state = NewState();
        var other = state.UpsertSiteUser(Guid.NewGuid(), "Konuk", "guest@example.com", "user");
        Assert.Equal(GroupInviteStatus.Success, state.CreateOrRotateGroupInvite(DemoId, LobbyId, out var invite));
        Assert.Equal(GroupInviteStatus.Success, state.JoinGroupInvite(other.Id, new Uri(invite!.InviteUrl).Fragment[1..], out _));
        var direct = state.CreateConversation(DemoId, new CreateConversationRequest("Özel", new[] { other.Id }));
        Assert.NotNull(direct);

        Assert.True(state.ModerateChatUser(LobbyId, other.Id, "mute", 60));
        Assert.True(state.IsChatWriteRestricted(LobbyId, other.Id, out _));
        Assert.False(state.AddMessage(other.Id, NewRequest(), out _));
        Assert.True(state.AddMessage(other.Id, NewRequest() with { ConversationId = direct.Id }, out _));

        Assert.True(state.ModerateChatUser(LobbyId, other.Id, "unmute", null));
        Assert.False(state.IsChatWriteRestricted(LobbyId, other.Id, out _));
        Assert.True(state.ModerateChatUser(LobbyId, other.Id, "ban", null));
        Assert.True(state.IsChatWriteRestricted(LobbyId, other.Id, out _));
        Assert.True(state.GetChatModeration(LobbyId, other.Id)!.IsBanned);
        Assert.True(state.ModerateChatUser(LobbyId, other.Id, "unban", null));
        Assert.False(state.IsChatWriteRestricted(LobbyId, other.Id, out _));
    }

    [Fact]
    public void PresenceMarksOnlyRecentlyActiveHumanUsersOnline()
    {
        var state = NewState();
        var other = state.UpsertSiteUser(Guid.NewGuid(), "Konuk", "guest@example.com", "user");
        state.CreateSession(DemoId);

        var before = state.GetPresence(LobbyId);
        Assert.True(before.Single(item => item.User.Id == DemoId).IsOnline);
        Assert.False(before.Single(item => item.User.Id == other.Id).IsOnline);
        Assert.All(before.Where(item => item.User.IsAgent), item => Assert.False(item.IsOnline));
        Assert.False(before.Single(item => item.User.Id == DemoId).IsViewingConversation);

        state.TouchActivity(other.Id);
        Assert.True(state.GetPresence(LobbyId).Single(item => item.User.Id == other.Id).IsOnline);
        state.TouchActivity(DemoId, LobbyId);
        Assert.True(state.GetPresence(LobbyId).Single(item => item.User.Id == DemoId).IsViewingConversation);
    }

    [Fact]
    public void SiteDirectoryControlsAccountsAndRoles()
    {
        var state = new ChatState();
        Assert.DoesNotContain(state.GetPresence(LobbyId), item => !item.User.IsAgent);
        var admin = new SiteAccount(1, DemoId, "Site yöneticisi", "admin@example.test", "admin", true);
        var inactiveId = Guid.NewGuid();
        state.SynchronizeSiteUsers(new[] { admin, new SiteAccount(2, inactiveId, "Pasif", "inactive@example.test", "user", false) });
        Assert.True(state.IsAdmin(DemoId));
        Assert.Null(state.GetUser(inactiveId));
        var token = state.CreateSession(DemoId);

        state.SynchronizeSiteUsers(new[] { admin with { Role = "user" } });
        Assert.False(state.IsAdmin(DemoId));
        state.SynchronizeSiteUsers(Array.Empty<SiteAccount>());
        Assert.Null(state.GetUser(DemoId));
        Assert.Null(state.ResolveSession(token));
    }

    private static SendMessageRequest NewRequest() => new(
        Guid.NewGuid(), LobbyId, "text", DateTimeOffset.UtcNow, null,
        new[] { new EncryptedPayload("test", "", "", "", "", "", DemoId) }, null);

    private static ChatState NewState()
    {
        var state = new ChatState();
        state.UpsertSiteUser(DemoId, "Test Yönetici", "test@example.test", "admin");
        return state;
    }
}
