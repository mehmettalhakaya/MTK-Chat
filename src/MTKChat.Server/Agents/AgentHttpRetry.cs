using System.Net;

namespace MTKChat.Server.Agents;

internal static class AgentHttpRetry
{
    public static async Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        Func<HttpRequestMessage> requestFactory,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var request = requestFactory();
            var response = await client.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode || !IsTransient(response.StatusCode) || attempt >= 2)
                return response;

            var delay = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(Math.Pow(2, attempt));
            response.Dispose();
            await Task.Delay(delay > TimeSpan.FromSeconds(10) ? TimeSpan.FromSeconds(10) : delay, cancellationToken);
        }
    }

    private static bool IsTransient(HttpStatusCode statusCode) =>
        statusCode == HttpStatusCode.RequestTimeout ||
        statusCode == HttpStatusCode.TooManyRequests ||
        (int)statusCode >= 500;
}
