using System.Net;
using MTKChat.Contracts;

namespace MTKChat.Desktop;

internal sealed partial class MainForm
{
    private CancellationTokenSource? _historyLoadCts;
    private TimeSpan _historyLoadTimeout = TimeSpan.FromSeconds(60);
    private Guid? _historyLoadedConversation;

    private void CancelHistoryLoad()
    {
        // Each async load owns/disposes its source in its own finally. Detach first:
        // an old continuation must not clear the new selection's busy flag.
        var old = _historyLoadCts;
        _historyLoadCts = null;
        _refreshing = false;
        old?.Cancel();
    }

    private Control CreatePendingKeyRow(StoredMessage message)
    {
        var row = (MessageRow)BuildMessageBubble(message, "Mesajın anahtarı yüklenemedi; yeniden denenecek.", null);
        row.CanMarkRead = false;
        row.CanPin = false;
        return row;
    }

    private void ShowHistoryLoadFailure(Guid room, Exception exception)
    {
        if (_selectedConversation?.Id != room || _historyLoadedConversation == room || IsDisposed) return;
        _messageList.SuspendLayout();
        try
        {
            foreach (Control row in _messageList.Controls.Cast<Control>().ToArray()) row.Dispose();
            _messageList.Controls.Clear();
            var expired = exception is ChatApiException { StatusCode: HttpStatusCode.Unauthorized };
            _messageList.Controls.Add(CreateEmptyState(expired ? "Yeniden giriş gerekli" : "Mesajlar yüklenemedi",
                expired ? null : RetryHistoryAsync));
            ResizeBubbles();
        }
        finally { _messageList.ResumeLayout(); }
    }

    private async Task RetryHistoryAsync()
    {
        if (_selectedConversation is null || IsDisposed || _refreshing) return;
        // Explicit retry is a read only; it does not replay a Send/ACK command.
        _nextNetworkPoll = default;
        await RefreshMessagesAsync(silent: true);
    }

    internal void PopulateHistoryStateSnapshot(bool failed)
    {
        PopulateSnapshot();
        _historyLoadedConversation = null;
        foreach (Control row in _messageList.Controls.Cast<Control>().ToArray()) row.Dispose();
        _messageList.Controls.Clear();
        if (failed) ShowHistoryLoadFailure(_selectedConversation!.Id,
            new ChatTransportException("message_history", true, new TimeoutException("Synthetic history snapshot")));
        else _messageList.Controls.Add(CreateEmptyState("Mesajlar yükleniyor..."));
        ResizeBubbles();
    }
}
