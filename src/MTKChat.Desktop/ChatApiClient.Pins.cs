using System.Net.Http.Json;
using MTKChat.Contracts;

namespace MTKChat.Desktop;

internal sealed partial class ChatApiClient
{
    public Task<IReadOnlyList<PinnedMessageView>> GetPinnedMessagesAsync(Guid conversationId,
        CancellationToken cancellationToken = default) =>
        GetAsync<IReadOnlyList<PinnedMessageView>>($"api/conversations/{conversationId}/pins", cancellationToken);

    public async Task<PinnedMessageView> PinMessageAsync(Guid conversationId, Guid messageId, int durationHours,
        CancellationToken cancellationToken = default)
    {
        // Only identifiers and expiry policy leave the device. The existing E2EE
        // envelope remains the sole source of the message text or media preview.
        using var response = await _http.PostAsJsonAsync($"api/conversations/{conversationId}/pins",
            new PinMessageRequest(messageId, durationHours), cancellationToken);
        return await ReadAsync<PinnedMessageView>(response, cancellationToken);
    }

    public async Task UnpinMessageAsync(Guid conversationId, Guid messageId,
        CancellationToken cancellationToken = default)
    {
        using var response = await _http.DeleteAsync($"api/conversations/{conversationId}/pins/{messageId}", cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }
}
