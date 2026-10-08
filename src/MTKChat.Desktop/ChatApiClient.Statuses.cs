using System.Net.Http.Json;
using MTKChat.Contracts;

namespace MTKChat.Desktop;

internal sealed partial class ChatApiClient
{
    public Task<IReadOnlyList<StatusSummary>> GetStatusesAsync(CancellationToken cancellationToken = default) =>
        GetAsync<IReadOnlyList<StatusSummary>>("api/statuses", cancellationToken);
    public Task<StoredStatus> GetStatusAsync(Guid id, CancellationToken cancellationToken = default) =>
        GetAsync<StoredStatus>($"api/statuses/{id}", cancellationToken);
    public async Task<StoredStatus> PublishStatusAsync(SendStatusRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsJsonAsync("api/statuses", request, cancellationToken);
        return await ReadAsync<StoredStatus>(response, cancellationToken);
    }
    public async Task DeleteStatusAsync(Guid id, CancellationToken cancellationToken = default)
    {
        using var response = await _http.DeleteAsync($"api/statuses/{id}", cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }
}
