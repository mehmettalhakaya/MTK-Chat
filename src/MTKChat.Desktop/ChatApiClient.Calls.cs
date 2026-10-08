using System.Net.Http.Json;
using MTKChat.Contracts;

namespace MTKChat.Desktop;

internal sealed partial class ChatApiClient
{
    internal Task<IReadOnlyList<CallView>> GetCallsAsync(CancellationToken ct = default) => GetAsync<IReadOnlyList<CallView>>("api/calls", ct);
    internal Task<CallView> GetCallAsync(Guid id, CancellationToken ct) => GetAsync<CallView>($"api/calls/{id}", ct);
    internal async Task<CallView> StartCallAsync(StartCallRequest request, CancellationToken ct = default)
    {
        using var response = await _http.PostAsJsonAsync("api/calls", request, ct);
        return await ReadAsync<CallView>(response, ct);
    }
    internal async Task<CallView> JoinCallAsync(Guid id, CallJoin identity, CancellationToken ct)
    {
        using var response = await _http.PostAsJsonAsync($"api/calls/{id}/join", identity, ct);
        return await ReadAsync<CallView>(response, ct);
    }
    internal Task SendCallFramesAsync(Guid id, IReadOnlyList<CallFrame> frames, CancellationToken ct) =>
        SendWithoutResultAsync(HttpMethod.Post, $"api/calls/{id}/frames", frames, ct);
    internal Task<IReadOnlyList<CallFrame>> GetCallFramesAsync(Guid id, CancellationToken ct) =>
        GetAsync<IReadOnlyList<CallFrame>>($"api/calls/{id}/frames", ct);
    internal Task SetCallMuteAsync(Guid id, bool muted, CancellationToken ct) =>
        SendWithoutResultAsync(HttpMethod.Put, $"api/calls/{id}/mute", new CallMute(muted), ct);
    internal async Task LeaveCallAsync(Guid id, CancellationToken ct)
    {
        using var response = await _http.DeleteAsync($"api/calls/{id}/participation", ct);
        await EnsureSuccessAsync(response, ct);
    }
}
