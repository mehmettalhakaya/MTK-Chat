namespace MTKChat.Desktop;

// A transient network failure is not an invalid encrypted message or an API denial.
// Keep only a fixed operation code here; URLs, account names and payloads are omitted.
internal sealed class ChatTransportException : HttpRequestException
{
    internal bool IsTimeout { get; }
    internal string OperationName { get; }

    internal ChatTransportException(string operationName, bool isTimeout, Exception inner, bool uncertainWrite = false)
        : base(uncertainWrite
            ? "Sunucunun işlem sonucu doğrulanamadı; işlem tamamlanmış olabilir. Yeniden göndermeden önce sohbeti veya işlem durumunu kontrol edin. Taslağınız korunuyor."
            : isTimeout
            ? "Sunucu zamanında yanıt vermedi. Bağlantı yeniden denenecek; mevcut mesajlarınız ve taslağınız korunuyor."
            : "Sunucuya bağlantı kurulamadı. İnternet bağlantınızı kontrol edin; mevcut mesajlarınız ve taslağınız korunuyor.", inner)
    {
        IsTimeout = isTimeout;
        OperationName = operationName;
    }
}

// API rejection must remain distinguishable from a transport outage; retrying an
// expired session or a permission denial cannot restore connectivity.
internal sealed class ChatApiException(System.Net.HttpStatusCode statusCode, string message) : InvalidOperationException(message)
{
    internal System.Net.HttpStatusCode StatusCode { get; } = statusCode;
}
