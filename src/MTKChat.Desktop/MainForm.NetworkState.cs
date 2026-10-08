using System.Net;

namespace MTKChat.Desktop;

internal sealed partial class MainForm
{
    private bool _pollCycleRunning;
    private Exception? _pollCycleFailure;
    private int _networkFailures;
    private DateTimeOffset _nextNetworkPoll;

    private static TimeSpan NetworkRetryDelay(int failures) =>
        TimeSpan.FromSeconds(Math.Min(30, 3 * (1 << Math.Clamp(failures - 1, 0, 4))));

    private void RecordNetworkFailure(Exception exception)
    {
        if (IsDisposed) return;
        if (_pollCycleRunning)
        {
            // A later 401 must override an earlier auxiliary network failure.
            if (exception is ChatApiException { StatusCode: HttpStatusCode.Unauthorized }) _pollCycleFailure = exception;
            else _pollCycleFailure ??= exception;
        }
        else ShowReconnectState(exception);
    }

    private void ShowReconnectState(Exception exception)
    {
        if (IsDisposed) return;
        _networkFailures = Math.Min(5, _networkFailures + 1);
        _nextNetworkPoll = DateTimeOffset.UtcNow.Add(NetworkRetryDelay(_networkFailures));
        // Keep E2EE's "Şifreli" label independent from connectivity. A network outage
        // does not weaken encryption and must not replace that security status.
        if (exception is ChatApiException { StatusCode: HttpStatusCode.Unauthorized })
        {
            ResetPinnedMessages();
            _refreshTimer.Stop(); _readTimer.Stop();
            Text = "MTK Chat · Yeniden giriş gerekli";
        }
        else Text = "MTK Chat · Bağlantı yeniden deneniyor";
    }

    private async Task RefreshBackgroundAsync()
    {
        if (_session is null || IsDisposed || _pollCycleRunning || DateTimeOffset.UtcNow < _nextNetworkPoll) return;
        _pollCycleRunning = true;
        _pollCycleFailure = null;
        try
        {
            // Delivery/ACK/presence are auxiliary. If one endpoint is broken, a
            // healthy selected history must still get its own turn every cycle.
            await RefreshMessagesAsync(silent: true);
            if (PollingMustStop()) return;
            // Auxiliary pin metadata has its own deadline and never delays calls,
            // delivery or a new room's history while a slow server is recovering.
            if (!_snapshotMode) _ = RefreshPinnedMessagesAsync();
            QueueMissingRecipientKeyRefresh();
            await LoadConversationsAsync(silent: true);
            if (PollingMustStop()) return;
            await PollDeliveryAsync();
            if (PollingMustStop()) return;
            await RefreshCallsAsync();
            if (PollingMustStop()) return;
            if (++_presenceTicks % 4 == 0)
            {
                await RefreshPresenceAsync();
                if (PollingMustStop()) return;
                await RefreshConversationPhotosAsync();
            }
            if (_pollCycleFailure is null && !IsDisposed)
            {
                _networkFailures = 0;
                _nextNetworkPoll = default;
                Text = "MTK Chat";
            }
        }
        catch (Exception exception) { _pollCycleFailure ??= exception; }
        finally
        {
            if (_pollCycleFailure is { } failure) ShowReconnectState(failure);
            _pollCycleRunning = false;
        }
    }

    private bool PollingMustStop() => IsDisposed ||
        _pollCycleFailure is ChatApiException { StatusCode: HttpStatusCode.Unauthorized };
}
