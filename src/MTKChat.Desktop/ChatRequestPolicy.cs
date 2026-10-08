namespace MTKChat.Desktop;

// One total deadline covers connection, retry delay AND the response body. Larger
// encrypted histories/media must not share the short metadata request deadline.
internal sealed record ChatRequestPolicy
{
    internal TimeSpan MetadataTimeout { get; init; } = TimeSpan.FromSeconds(30);
    internal TimeSpan HistoryTimeout { get; init; } = TimeSpan.FromSeconds(90);
    internal TimeSpan MediaTimeout { get; init; } = TimeSpan.FromSeconds(120);
    internal TimeSpan CommandTimeout { get; init; } = TimeSpan.FromSeconds(45);
    internal TimeSpan CallTimeout { get; init; } = TimeSpan.FromSeconds(15);
    internal TimeSpan RetryDelay { get; init; } = TimeSpan.FromMilliseconds(250);

    internal TimeSpan TimeoutFor(HttpRequestMessage request)
    {
        var path = request.RequestUri?.AbsolutePath.TrimEnd('/') ?? "";
        if (path.Contains("/api/calls", StringComparison.Ordinal)) return CallTimeout;
        if (path.Contains("/api/statuses/", StringComparison.Ordinal) ||
            request.Method == HttpMethod.Post && path.EndsWith("/api/statuses", StringComparison.Ordinal)) return MediaTimeout;
        if (path.Contains("/photo", StringComparison.Ordinal) || path.Contains("/files", StringComparison.Ordinal)) return MediaTimeout;
        if (request.Method == HttpMethod.Post && path.EndsWith("/api/messages", StringComparison.Ordinal)) return MediaTimeout;
        if (request.Method == HttpMethod.Get && path.EndsWith("/messages", StringComparison.Ordinal)) return HistoryTimeout;
        return request.Method == HttpMethod.Get ? MetadataTimeout : CommandTimeout;
    }

    internal static string OperationFor(HttpRequestMessage request)
    {
        var path = request.RequestUri?.AbsolutePath.TrimEnd('/') ?? "";
        if (path.Contains("/api/statuses", StringComparison.Ordinal)) return "encrypted_status";
        if (path.EndsWith("/device", StringComparison.Ordinal)) return "device_key";
        if (path.EndsWith("/presence", StringComparison.Ordinal)) return "presence";
        if (path.EndsWith("/messages", StringComparison.Ordinal)) return request.Method == HttpMethod.Get ? "message_history" : "message_command";
        if (path.EndsWith("/inbox", StringComparison.Ordinal)) return "delivery_inbox";
        if (path.EndsWith("/ack", StringComparison.Ordinal)) return "message_ack";
        if (path.Contains("/photo", StringComparison.Ordinal)) return "photo";
        if (path.Contains("/files", StringComparison.Ordinal)) return "encrypted_file";
        if (path.Contains("/api/calls", StringComparison.Ordinal)) return "call";
        if (path.EndsWith("/conversations", StringComparison.Ordinal)) return "conversations";
        if (path.EndsWith("/members", StringComparison.Ordinal)) return "members";
        if (path.EndsWith("/login", StringComparison.Ordinal)) return "login";
        return "metadata_command";
    }
}
