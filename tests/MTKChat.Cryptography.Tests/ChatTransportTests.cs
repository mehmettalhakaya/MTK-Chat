using System.Net;
using System.Net.Http.Json;
using MTKChat.Contracts;
using MTKChat.Desktop;
using Xunit;

namespace MTKChat.Cryptography.Tests;

public sealed class ChatTransportTests
{
    private static ChatRequestPolicy Fast => new()
    {
        MetadataTimeout = TimeSpan.FromMilliseconds(250),
        HistoryTimeout = TimeSpan.FromMilliseconds(250),
        MediaTimeout = TimeSpan.FromMilliseconds(250),
        CommandTimeout = TimeSpan.FromMilliseconds(250),
        CallTimeout = TimeSpan.FromMilliseconds(250),
        RetryDelay = TimeSpan.Zero
    };

    [Theory]
    [InlineData("GET", "api/users", 30)]
    [InlineData("GET", "api/conversations/room/messages", 90)]
    [InlineData("POST", "api/messages", 120)]
    [InlineData("GET", "api/files/token", 120)]
    [InlineData("PUT", "api/profile/photo", 120)]
    [InlineData("POST", "api/conversations/room/files/client", 120)]
    [InlineData("POST", "api/messages/ack", 45)]
    [InlineData("GET", "api/calls", 15)]
    public void Budgets_are_operation_specific(string method, string path, int seconds)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), "https://chat.invalid/chat/" + path);
        Assert.Equal(TimeSpan.FromSeconds(seconds), new ChatRequestPolicy().TimeoutFor(request));
    }

    [Fact]
    public async Task Header_timeout_is_typed_localized_and_does_not_replay()
    {
        using var handler = new Stub(async (_, ct) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return Json(Array.Empty<ChatUser>());
        });
        using var api = new ChatApiClient("https://chat.invalid/", handler, Fast);
        var error = await Assert.ThrowsAsync<ChatTransportException>(() => api.GetUsersAsync());
        Assert.True(error.IsTimeout);
        Assert.DoesNotContain("HttpClient.Timeout", error.Message);
        Assert.DoesNotContain("https://", error.Message);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Deadline_also_covers_body_after_headers_and_disposes_it()
    {
        using var content = new StalledContent();
        using var handler = new Stub((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content }));
        using var api = new ChatApiClient("https://chat.invalid/", handler, Fast);
        var error = await Assert.ThrowsAsync<ChatTransportException>(() => api.GetUsersAsync());
        Assert.True(error.IsTimeout);
        Assert.True(content.Released);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Explicit_caller_cancellation_is_not_a_network_timeout()
    {
        using var handler = new Stub(async (_, ct) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return Json(Array.Empty<ChatUser>());
        });
        using var api = new ChatApiClient("https://chat.invalid/", handler, Fast);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => api.GetUsersAsync(cancel.Token));
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData(502)]
    [InlineData(503)]
    [InlineData(504)]
    public async Task Safe_read_has_one_bounded_gateway_retry(int status)
    {
        var count = 0;
        using var handler = new Stub((_, _) => Task.FromResult(++count == 1
            ? new HttpResponseMessage((HttpStatusCode)status)
            : Json(Array.Empty<ChatUser>())));
        using var api = new ChatApiClient("https://chat.invalid/", handler, Fast);
        Assert.Empty(await api.GetUsersAsync());
        Assert.Equal(2, handler.Calls);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(429)]
    public async Task Authentication_permission_and_rate_rejections_are_not_replayed(int status)
    {
        using var handler = new Stub((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)
        { Content = JsonContent.Create(new ApiError("denied", "Reddedildi")) }));
        using var api = new ChatApiClient("https://chat.invalid/", handler, Fast);
        var error = await Assert.ThrowsAsync<ChatApiException>(() => api.GetUsersAsync());
        Assert.Equal((HttpStatusCode)status, error.StatusCode);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Safe_read_connection_retry_preserves_authentication()
    {
        var headers = new List<string?>();
        var reads = 0;
        using var handler = new Stub((request, _) =>
        {
            if (request.Method == HttpMethod.Post)
                return Task.FromResult(Json(new LoginResponse("test-token", new ChatUser(Guid.NewGuid(), "Test", "", false, null))));
            headers.Add(request.Headers.Authorization?.ToString());
            if (++reads == 1) throw new HttpRequestException("fake connection loss");
            return Task.FromResult(Json(Array.Empty<ChatUser>()));
        });
        using var api = new ChatApiClient("https://chat.invalid/", handler, Fast);
        await api.LoginAsync("test@example.invalid", "not-a-real-password");
        Assert.Empty(await api.GetUsersAsync());
        Assert.Equal(new[] { "Bearer test-token", "Bearer test-token" }, headers);
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    public async Task Mutation_connection_failures_are_never_automatically_replayed(string method)
    {
        using var handler = new Stub((_, _) => throw new HttpRequestException("fake uncertain commit"));
        using var client = new HttpClient(new ChatTransportHandler(handler, Fast)) { Timeout = Timeout.InfiniteTimeSpan };
        using var request = new HttpRequestMessage(new HttpMethod(method), "https://chat.invalid/api/messages");
        await Assert.ThrowsAsync<ChatTransportException>(() => client.SendAsync(request));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Send_timeout_retains_request_identity_and_is_not_replayed()
    {
        SendMessageRequest? seen = null;
        using var handler = new Stub(async (request, ct) =>
        {
            seen = await request.Content!.ReadFromJsonAsync<SendMessageRequest>(ct);
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return Json(seen!);
        });
        using var api = new ChatApiClient("https://chat.invalid/", handler, Fast);
        var message = new SendMessageRequest(Guid.NewGuid(), Guid.NewGuid(), "text", DateTimeOffset.UtcNow, null, [], null);
        var error = await Assert.ThrowsAsync<ChatTransportException>(() => api.SendMessageAsync(message));
        Assert.Contains("tamamlanmış olabilir", error.Message);
        Assert.Equal(message.ClientMessageId, seen!.ClientMessageId);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Retry_delay_uses_the_same_total_deadline()
    {
        using var handler = new Stub((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        using var api = new ChatApiClient("https://chat.invalid/", handler, Fast with { RetryDelay = TimeSpan.FromSeconds(2) });
        var error = await Assert.ThrowsAsync<ChatTransportException>(() => api.GetUsersAsync());
        Assert.True(error.IsTimeout);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task A_failed_request_does_not_poison_the_next_request()
    {
        var broken = true;
        using var handler = new Stub((_, _) => broken
            ? throw new HttpRequestException("fake offline")
            : Task.FromResult(Json(Array.Empty<ChatUser>())));
        using var api = new ChatApiClient("https://chat.invalid/", handler, Fast);
        await Assert.ThrowsAsync<ChatTransportException>(() => api.GetUsersAsync());
        broken = false;
        Assert.Empty(await api.GetUsersAsync());
        Assert.Equal(3, handler.Calls);
    }

    private static HttpResponseMessage Json<T>(T value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };

    private sealed class Stub(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> run) : HttpMessageHandler
    {
        internal int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { Calls++; return run(request, ct); }
    }

    private sealed class StalledContent : HttpContent
    {
        internal bool Released;
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken ct) =>
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        protected override void Dispose(bool disposing) { Released = true; base.Dispose(disposing); }
    }
}
