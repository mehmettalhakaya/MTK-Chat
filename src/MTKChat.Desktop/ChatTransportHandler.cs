using System.Diagnostics;
using System.Net;

namespace MTKChat.Desktop;

internal sealed class ChatTransportHandler(HttpMessageHandler inner, ChatRequestPolicy policy) : DelegatingHandler(inner)
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(policy.TimeoutFor(request));
        var operation = ChatRequestPolicy.OperationFor(request);
        var elapsed = Stopwatch.StartNew();
        // Writes are never replayed here: a timed-out POST may already be committed.
        // Replaying only safe reads avoids duplicate messages or moderation commands.
        var retryable = request.Method == HttpMethod.Get;
        var attempts = retryable ? 2 : 1;
        try
        {
            for (var attempt = 0; attempt < attempts; attempt++)
            {
                using var retry = attempt == 0 ? null : CloneRead(request);
                HttpResponseMessage? response = null;
                try
                {
                    response = await base.SendAsync(retry ?? request, deadline.Token).ConfigureAwait(false);
                    if (attempt == 0 && retryable && IsTemporaryGatewayFailure(response.StatusCode))
                    {
                        response.Dispose(); response = null;
                        await Task.Delay(policy.RetryDelay, deadline.Token).ConfigureAwait(false);
                        continue;
                    }
                    // HttpClient's own timeout is infinite, but NOTHING is unbounded:
                    // buffer under our total token before returning headers to HttpClient.
                    // This also covers a server that sends headers then stalls its body.
                    await response.Content.LoadIntoBufferAsync(deadline.Token).ConfigureAwait(false);
                    return response;
                }
                catch (HttpRequestException) when (attempt == 0 && retryable && !deadline.IsCancellationRequested)
                {
                    response?.Dispose();
                    await Task.Delay(policy.RetryDelay, deadline.Token).ConfigureAwait(false);
                }
                catch { response?.Dispose(); throw; }
            }
            throw new InvalidOperationException("Request retry loop completed without a response.");
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            Trace.TraceWarning("MTK network {0}: timeout after {1} ms.", operation, elapsed.ElapsedMilliseconds);
            throw new ChatTransportException(operation, true, exception, uncertainWrite: !retryable);
        }
        catch (HttpRequestException exception)
        {
            Trace.TraceWarning("MTK network {0}: connection failure after {1} ms.", operation, elapsed.ElapsedMilliseconds);
            throw new ChatTransportException(operation, false, exception, uncertainWrite: !retryable);
        }
    }

    private static bool IsTemporaryGatewayFailure(HttpStatusCode status) =>
        status is HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;

    private static HttpRequestMessage CloneRead(HttpRequestMessage original)
    {
        var copy = new HttpRequestMessage(HttpMethod.Get, original.RequestUri)
        { Version = original.Version, VersionPolicy = original.VersionPolicy };
        foreach (var header in original.Headers) copy.Headers.TryAddWithoutValidation(header.Key, header.Value);
        foreach (var option in original.Options) copy.Options.Set(new HttpRequestOptionsKey<object?>(option.Key), option.Value);
        return copy;
    }
}
