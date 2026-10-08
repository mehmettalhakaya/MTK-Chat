using System.Net.Http.Headers;
using System.Net.Http.Json;
using MTKChat.Contracts;

namespace MTKChat.Desktop;

internal sealed partial class ChatApiClient : IDisposable
{
    private readonly HttpClient _http;

    public ChatApiClient(string baseAddress) : this(baseAddress, new SocketsHttpHandler
    {
        ConnectTimeout = TimeSpan.FromSeconds(10),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    })
    { }

    internal ChatApiClient(string baseAddress, HttpMessageHandler handler, ChatRequestPolicy? policy = null)
    {
        _http = new HttpClient(new ChatTransportHandler(handler, policy ?? new ChatRequestPolicy()))
        {
            BaseAddress = new Uri(baseAddress),
            // ChatTransportHandler enforces a bounded operation-specific total deadline.
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    public async Task<LoginResponse> LoginAsync(string email, string password, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsJsonAsync("api/auth/login", new LoginRequest(email, password), cancellationToken);
        var result = await ReadAsync<LoginResponse>(response, cancellationToken);
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", result.AccessToken);
        return result;
    }

    public Task RegisterDeviceAsync(RegisterDeviceRequest request, CancellationToken cancellationToken = default) =>
        SendWithoutResultAsync(HttpMethod.Post, "api/devices", request, cancellationToken);

    public Task<IReadOnlyList<ChatUser>> GetUsersAsync(CancellationToken cancellationToken = default) =>
        GetAsync<IReadOnlyList<ChatUser>>("api/users", cancellationToken);

    public Task<ChatUser> GetProfileAsync() => GetAsync<ChatUser>("api/profile", default);
    public async Task<ChatUser> SaveProfileNameAsync(string firstName, string lastName, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PutAsJsonAsync("api/profile/name",
            new ProfileNameChangeRequest(firstName, lastName), cancellationToken);
        return await ReadAsync<ChatUser>(response, cancellationToken);
    }
    public Task<PrivacySettings> GetPrivacyAsync(CancellationToken cancellationToken = default) => GetAsync<PrivacySettings>("api/profile/privacy", cancellationToken);
    public Task SavePrivacyAsync(PrivacySettings settings, CancellationToken cancellationToken = default) => SendWithoutResultAsync(HttpMethod.Put, "api/profile/privacy", settings, cancellationToken);
    public Task<IReadOnlyList<GroupManagementView>> GetManageableGroupsAsync(CancellationToken cancellationToken = default) =>
        GetAsync<IReadOnlyList<GroupManagementView>>("api/groups/manageable", cancellationToken);
    public Task SetGroupRoleAsync(Guid room, Guid target, string role, CancellationToken cancellationToken = default) =>
        SendWithoutResultAsync(HttpMethod.Put, $"api/conversations/{room}/members/{target}/role", new GroupRoleChangeRequest(role), cancellationToken);
    public Task ModerateGroupAsync(Guid room, Guid target, string action, int? minutes = null, CancellationToken cancellationToken = default) =>
        SendWithoutResultAsync(HttpMethod.Put, $"api/conversations/{room}/members/{target}/moderation", new ChatModerationRequest(action, minutes), cancellationToken);
    public async Task RemoveGroupMemberAsync(Guid room, Guid target, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsync($"api/conversations/{room}/members/{target}/remove", null, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }
    public async Task<FileUploadResult> UploadFileAsync(Guid room, Guid clientId, byte[] ciphertext)
    {
        using var content = new ByteArrayContent(ciphertext);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var response = await _http.PostAsync($"api/conversations/{room}/files/{clientId}", content);
        return await ReadAsync<FileUploadResult>(response, default);
    }
    public async Task<byte[]> DownloadFileAsync(string token)
    {
        using var response = await _http.GetAsync($"api/files/{Uri.EscapeDataString(token)}");
        await EnsureSuccessAsync(response, default);
        return await response.Content.ReadAsByteArrayAsync();
    }
    public async Task CheckAdminAccessAsync()
    {
        using var response = await _http.GetAsync("api/admin/access");
        await EnsureSuccessAsync(response, default);
    }

    public async Task<ChatUser> SavePhotoAsync(byte[]? jpeg, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(jpeg is null ? HttpMethod.Delete : HttpMethod.Put, "api/profile/photo");
        if (jpeg is not null)
        {
            request.Content = new ByteArrayContent(jpeg);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue(jpeg.AsSpan().StartsWith("GIF8"u8) ? "image/gif" : "image/jpeg");
        }
        using var response = await _http.SendAsync(request, cancellationToken);
        return await ReadAsync<ChatUser>(response, cancellationToken);
    }

    public async Task<byte[]?> GetPhotoAsync(Guid userId)
    {
        using var response = await _http.GetAsync($"api/users/{userId}/photo");
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        await EnsureSuccessAsync(response, default);
        return await response.Content.ReadAsByteArrayAsync();
    }

    public async Task<GroupPhotoResult> SaveGroupPhotoAsync(Guid conversationId, byte[]? jpeg)
    {
        using var request = new HttpRequestMessage(jpeg is null ? HttpMethod.Delete : HttpMethod.Put,
            $"api/conversations/{conversationId}/photo");
        if (jpeg is not null)
        {
            request.Content = new ByteArrayContent(jpeg);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        }
        using var response = await _http.SendAsync(request);
        return await ReadAsync<GroupPhotoResult>(response, default);
    }

    public async Task<byte[]?> GetGroupPhotoAsync(Guid conversationId)
    {
        using var response = await _http.GetAsync($"api/conversations/{conversationId}/photo");
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        await EnsureSuccessAsync(response, default);
        return await response.Content.ReadAsByteArrayAsync();
    }

    public Task<IReadOnlyList<Guid>> GetBlockedUsersAsync(CancellationToken cancellationToken = default) =>
        GetAsync<IReadOnlyList<Guid>>("api/blocks", cancellationToken);

    public async Task SetBlockedAsync(Guid userId, bool blocked, CancellationToken cancellationToken = default)
    {
        using var response = blocked
            ? await _http.PutAsync($"api/blocks/{userId}", null, cancellationToken)
            : await _http.DeleteAsync($"api/blocks/{userId}", cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public Task<DeviceKeyBundle?> TryGetDeviceAsync(Guid userId, CancellationToken cancellationToken = default) =>
        TryGetAsync<DeviceKeyBundle>($"api/users/{userId}/device", cancellationToken);

    public Task<IReadOnlyList<ConversationSummary>> GetConversationsAsync(CancellationToken cancellationToken = default) =>
        GetAsync<IReadOnlyList<ConversationSummary>>("api/conversations", cancellationToken);

    public async Task<ConversationSummary> CreateConversationAsync(CreateConversationRequest request)
    {
        using var response = await _http.PostAsJsonAsync("api/conversations", request);
        return await ReadAsync<ConversationSummary>(response, default);
    }
    public async Task<GroupTitleResult> RenameGroupAsync(Guid conversationId, string title, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PutAsJsonAsync($"api/conversations/{conversationId}/title",
            new RenameConversationRequest(title), cancellationToken);
        return await ReadAsync<GroupTitleResult>(response, cancellationToken);
    }

    public async Task<GroupInviteResult> CreateGroupInviteAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsync($"api/conversations/{conversationId}/invite", null, cancellationToken);
        return await ReadAsync<GroupInviteResult>(response, cancellationToken);
    }

    public Task<IReadOnlyList<GroupInviteEntry>> ListGroupInvitesAsync(Guid room, CancellationToken cancellationToken = default) =>
        GetAsync<IReadOnlyList<GroupInviteEntry>>($"api/conversations/{room}/invites", cancellationToken);

    public async Task<GroupInviteEntry> CreateManagedGroupInviteAsync(Guid room, int minutes, CancellationToken cancellationToken = default, bool neverExpires = false)
    {
        using var response = await _http.PostAsJsonAsync($"api/conversations/{room}/invites", new CreateGroupInviteRequest(minutes, neverExpires), cancellationToken);
        return await ReadAsync<GroupInviteEntry>(response, cancellationToken);
    }

    public async Task<GroupInviteEntry> ChangeGroupInviteDurationAsync(Guid room, Guid inviteId, int minutes, CancellationToken cancellationToken = default, bool neverExpires = false)
    {
        using var response = await _http.PutAsJsonAsync($"api/conversations/{room}/invites/{inviteId}", new ChangeGroupInviteDurationRequest(minutes, neverExpires), cancellationToken);
        return await ReadAsync<GroupInviteEntry>(response, cancellationToken);
    }

    public async Task DeleteGroupInviteAsync(Guid room, Guid inviteId, CancellationToken cancellationToken = default)
    {
        using var response = await _http.DeleteAsync($"api/conversations/{room}/invites/{inviteId}", cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task RevokeGroupInviteAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        using var response = await _http.DeleteAsync($"api/conversations/{conversationId}/invite", cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task<GroupInvitePreview> PreviewGroupInviteAsync(string token, CancellationToken cancellationToken = default)
    {
        GroupInviteLink.RequireToken(token);
        // Keep the invitation secret out of URL/access logs. This authenticated
        // POST only previews; membership changes require the separate join POST.
        using var response = await _http.PostAsJsonAsync("api/group-invites/preview", new GroupInviteTokenRequest(token), cancellationToken);
        return await ReadAsync<GroupInvitePreview>(response, cancellationToken);
    }

    public async Task<ConversationSummary> JoinGroupInviteAsync(string token, CancellationToken cancellationToken = default)
    {
        GroupInviteLink.RequireToken(token);
        using var response = await _http.PostAsJsonAsync("api/group-invites/join", new GroupInviteTokenRequest(token), cancellationToken);
        return await ReadAsync<ConversationSummary>(response, cancellationToken);
    }
    public async Task<ConversationSummary> OpenDirectAsync(Guid target)
    {
        using var response = await _http.PostAsJsonAsync("api/conversations/direct", new CreateDirectConversationRequest(target));
        return await ReadAsync<ConversationSummary>(response, default);
    }

    public Task<IReadOnlyList<ChatUser>> GetMembersAsync(Guid conversationId, CancellationToken cancellationToken = default) =>
        GetAsync<IReadOnlyList<ChatUser>>($"api/conversations/{conversationId}/members", cancellationToken);

    public async Task<IReadOnlyList<PresenceView>> GetPresenceAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        var path = $"api/conversations/{conversationId}/presence";
        using var response = await _http.PostAsync(path, null, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await GetAsync<IReadOnlyList<PresenceView>>(path, cancellationToken);
    }

    public Task<IReadOnlyList<PresenceView>> GetPresenceStatusAsync(Guid conversationId) =>
        GetAsync<IReadOnlyList<PresenceView>>($"api/conversations/{conversationId}/presence", default);

    public Task<IReadOnlyList<StoredMessage>> GetMessagesAsync(Guid conversationId, CancellationToken cancellationToken = default) =>
        GetAsync<IReadOnlyList<StoredMessage>>($"api/conversations/{conversationId}/messages", cancellationToken);

    public Task<IReadOnlyList<StoredMessage>> GetPreviewMessagesAsync(Guid conversationId, DateTimeOffset latestAt,
        CancellationToken cancellationToken = default)
    {
        // The existing endpoint already filters by signed CreatedAt. Ask only for
        // the latest timestamp (including ties), not every image/voice in history.
        var after = latestAt == DateTimeOffset.MinValue ? latestAt : latestAt.AddTicks(-1);
        var timestamp = Uri.EscapeDataString(after.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        return GetAsync<IReadOnlyList<StoredMessage>>($"api/conversations/{conversationId}/messages?after={timestamp}", cancellationToken);
    }

    public Task<IReadOnlyList<StoredMessage>> GetDeliveryInboxAsync() =>
        GetAsync<IReadOnlyList<StoredMessage>>("api/messages/inbox", default);

    public Task AcknowledgeMessagesAsync(IReadOnlyList<Guid> ids, bool read = false) =>
        SendWithoutResultAsync(HttpMethod.Post, "api/messages/ack", new AcknowledgeMessagesRequest(ids, read), default);

    public async Task<StoredMessage> SendMessageAsync(SendMessageRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsJsonAsync("api/messages", request, cancellationToken);
        return await ReadAsync<StoredMessage>(response, cancellationToken);
    }

    public async Task DeleteMessageAsync(Guid id, bool forEveryone, CancellationToken cancellationToken = default)
    {
        using var response = await _http.DeleteAsync($"api/messages/{id}?forEveryone={forEveryone.ToString().ToLowerInvariant()}", cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task ClearConversationMessagesAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        using var response = await _http.DeleteAsync(
            $"api/conversations/{conversationId}/messages?forEveryone=false",
            cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task RemoveConversationAsync(Guid conversationId)
    {
        using var response = await _http.DeleteAsync($"api/conversations/{conversationId}");
        await EnsureSuccessAsync(response, default);
    }

    public async Task LeaveConversationAsync(Guid conversationId)
    {
        using var response = await _http.PostAsync($"api/conversations/{conversationId}/leave", null);
        await EnsureSuccessAsync(response, default);
    }

    public Task<IReadOnlyList<AdminUserView>> GetAdminUsersAsync(CancellationToken cancellationToken = default) =>
        GetAsync<IReadOnlyList<AdminUserView>>("api/admin/users", cancellationToken);

    public Task<IReadOnlyList<ChatModerationView>> GetChatModerationAsync(Guid conversationId, CancellationToken cancellationToken = default) =>
        GetAsync<IReadOnlyList<ChatModerationView>>($"api/admin/conversations/{conversationId}/moderation", cancellationToken);

    public Task ModerateChatUserAsync(Guid conversationId, Guid userId, string action, int? durationMinutes = null,
        CancellationToken cancellationToken = default) =>
        SendWithoutResultAsync(HttpMethod.Put, $"api/admin/conversations/{conversationId}/users/{userId}/moderation",
            new ChatModerationRequest(action, durationMinutes), cancellationToken);

    public Task SetUserRoleAsync(Guid userId, string role, CancellationToken cancellationToken = default) =>
        SendWithoutResultAsync(HttpMethod.Put, $"api/admin/users/{userId}/role", new RoleChangeRequest(role), cancellationToken);

    public Task ModerateUserAsync(Guid userId, string action, int? durationMinutes = null, CancellationToken cancellationToken = default) =>
        SendWithoutResultAsync(HttpMethod.Put, $"api/admin/users/{userId}/moderation", new ModerationRequest(action, durationMinutes), cancellationToken);

    private async Task<T> GetAsync<T>(string path, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(path, cancellationToken);
        return await ReadAsync<T>(response, cancellationToken);
    }

    private async Task<T?> TryGetAsync<T>(string path, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(path, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return default;
        return await ReadAsync<T>(response, cancellationToken);
    }

    private async Task SendWithoutResultAsync<T>(HttpMethod method, string path, T body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, path) { Content = JsonContent.Create(body) };
        using var response = await _http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException("Sunucu boş yanıt döndürdü.");
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        try
        {
            var error = await response.Content.ReadFromJsonAsync<ApiError>(cancellationToken: cancellationToken);
            throw new ChatApiException(response.StatusCode, error?.Message ?? $"Sunucu hatası: {(int)response.StatusCode}");
        }
        catch (System.Text.Json.JsonException)
        {
            throw new ChatApiException(response.StatusCode, $"Sunucu hatası: {(int)response.StatusCode}");
        }
    }

    public void Dispose() => _http.Dispose();
}
