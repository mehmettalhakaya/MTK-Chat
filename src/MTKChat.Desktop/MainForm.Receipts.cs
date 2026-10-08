using System.Runtime.InteropServices;

namespace MTKChat.Desktop;

internal sealed partial class MainForm
{
    private bool _pollingDelivery;
    private bool _readingReceipts;
    private readonly HashSet<Guid> _readAcknowledged = new();
    private readonly System.Windows.Forms.Timer _readTimer = new() { Interval = 700 };

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    private async Task PollDeliveryAsync()
    {
        if (_session is null || _pollingDelivery || IsDisposed) return;
        _pollingDelivery = true;
        try
        {
            var inbox = await _api.GetDeliveryInboxAsync();
            if (inbox.Count > 0 && !IsDisposed)
                await _api.AcknowledgeMessagesAsync(inbox.Select(m => m.Id).ToArray());
        }
        catch (Exception exception)
        {
            RecordNetworkFailure(exception);
            // Offline / failed ACK: next poll downloads and acknowledges the batch again.
            // Never infer delivery from the online list or fabricate a local success.
        }
        finally { _pollingDelivery = false; }
    }

    private async Task MarkVisibleReadAsync()
    {
        if (_session is null || _selectedConversation is null || _readingReceipts || _refreshing ||
            DateTimeOffset.UtcNow < _nextNetworkPoll ||
            IsDisposed || !Visible || Opacity < 1 || _messageInfoOverlayMode || WindowState == FormWindowState.Minimized ||
            !IsHandleCreated || GetForegroundWindow() != Handle) return;
        var version = _conversationVersion;
        var ids = _messageList.Controls.OfType<MessageRow>()
            .Where(row => row.ConversationId == _selectedConversation.Id && !_readAcknowledged.Contains(row.MessageId) &&
                row.IsContentVisible(_messageList.ViewportRectangle))
            .Select(row => row.MessageId).Take(100).ToArray();
        if (ids.Length == 0 || version != _conversationVersion) return;
        _readingReceipts = true;
        try
        {
            // Read implies delivered. Only successfully decrypted visible bubbles reach here;
            // a background/minimized form, hidden history or decryption error cannot turn ticks blue.
            await _api.AcknowledgeMessagesAsync(ids, read: true);
            foreach (var id in ids) _readAcknowledged.Add(id);
        }
        catch (Exception exception) { RecordNetworkFailure(exception); /* Retry without losing unread status. */ }
        finally { _readingReceipts = false; }
    }

    internal void PopulateReceiptSnapshot()
    {
        PopulateSnapshot();
        foreach (var row in _messageList.Controls.Cast<Control>().ToArray()) row.Dispose();
        _messageList.Controls.Clear();
        var now = DateTimeOffset.Now;
        var other = _selectedConversation!.Participants.First(u => u.Id != _session!.User.Id);
        foreach (var (status, text) in new[] { ("sent", "Gönderildi"), ("delivered", "Teslim edildi"), ("read", "Okundu") })
        {
            var receipt = new MTKChat.Contracts.RecipientReceipt(other.Id,
                status == "sent" ? null : now, status == "read" ? now : null);
            var message = new MTKChat.Contracts.StoredMessage(Guid.NewGuid(), Guid.NewGuid(), _selectedConversation.Id,
                _session!.User.Id, "text", now, status == "delivered" ? now.AddMinutes(5) : null, false, [], null,
                new MTKChat.Contracts.MessageDelivery(status, [receipt]));
            _messageList.Controls.Add(BuildMessageBubble(message, text, null));
        }
        ResizeBubbles();
    }
}
