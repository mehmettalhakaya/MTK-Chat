using System.Net;
using MTKChat.Contracts;

namespace MTKChat.Desktop;

internal sealed partial class MainForm
{
    private readonly PinnedMessageBanner _pinnedBanner = new() { Dock = DockStyle.Fill, Margin = Padding.Empty };
    private RowStyle? _pinBannerHeight;
    private IReadOnlyList<PinnedMessageView> _pinnedMessages = [];
    private CancellationTokenSource? _pinLoadCts;
    private CancellationTokenSource? _pinMutationCts;
    private DateTimeOffset _nextPinPoll;
    private DateTimeOffset _pinsUnsupportedUntil;
    private int _pinIndex;
    private bool _pinMutationRunning;
    private readonly System.Windows.Forms.Timer _pinExpiryTimer = new() { Interval = 1000 };

    internal static bool CanManagePins(ChatUser? actor, ConversationSummary? conversation) =>
        actor is { IsAgent: false } && conversation is not null &&
        conversation.Participants.Any(user => user.Id == actor.Id) &&
        (conversation.Kind == "direct" || actor.Role == "admin" ||
            conversation.GroupRoles?.GetValueOrDefault(actor.Id) is "admin" or "mod");

    private Control BuildPinnedMessageBanner(RowStyle height)
    {
        _pinBannerHeight = height;
        var host = new ReferenceSurface { Dock = DockStyle.Fill, Margin = Padding.Empty, Padding = new Padding(16, 4, 16, 4) };
        host.Controls.Add(_pinnedBanner);
        _pinnedBanner.JumpRequested += (_, _) => JumpToCurrentPinnedMessage();
        _pinnedBanner.NextRequested += (_, _) => { _pinIndex++; UpdatePinnedMessageBanner(); };
        _pinnedBanner.DpiChangedAfterParent += (_, _) => UpdatePinnedMessageBanner();
        // Local expiry is independent of HTTP polling/backoff and keeps working
        // while a slow GET, offline connection or minimized window delays it.
        _pinExpiryTimer.Tick += (_, _) => UpdatePinnedMessageBanner();
        return host;
    }

    private void JumpToCurrentPinnedMessage()
    {
        if (_pinnedBanner.MessageId is { } id && AccessiblePinnedRows().Any(item => item.Row.MessageId == id))
            ScrollToStarredMessage(id);
    }

    private void ResetPinnedMessages()
    {
        // Detach before cancel: late continuations cannot clear a newer room's
        // request or restore an old banner after selection/logout/disposal.
        var load = _pinLoadCts; _pinLoadCts = null; load?.Cancel();
        _pinMutationCts?.Cancel();
        _nextPinPoll = default; _pinnedMessages = []; _pinIndex = 0;
        _pinExpiryTimer.Stop();
        _pinnedBanner.ClearMessage();
        if (_pinBannerHeight is not null) _pinBannerHeight.Height = 0;
    }

    private void DisposePinnedMessages()
    {
        ResetPinnedMessages();
        _pinExpiryTimer.Dispose();
    }

    private async Task RefreshPinnedMessagesAsync(bool force = false)
    {
        if (IsDisposed || Disposing || _session is null || _selectedConversation is null || _pinLoadCts is not null ||
            _pinMutationRunning && !force ||
            !force && DateTimeOffset.UtcNow < _nextPinPoll || DateTimeOffset.UtcNow < _pinsUnsupportedUntil) return;
        var room = _selectedConversation.Id; var version = _conversationVersion; var user = _session.User.Id;
        using var load = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        _pinLoadCts = load;
        _nextPinPoll = DateTimeOffset.UtcNow.AddSeconds(9);
        try
        {
            var pins = await _api.GetPinnedMessagesAsync(room, load.Token);
            if (!PinSelectionMatches(room, user, version) || load.IsCancellationRequested) return;
            if (pins.Count > 3 || pins.Any(pin => pin.ConversationId != room || pin.MessageId == Guid.Empty || pin.PinnedBy == Guid.Empty) ||
                pins.Select(pin => pin.MessageId).Distinct().Count() != pins.Count)
                throw new InvalidDataException("Sabitlenen mesaj listesi geçersiz.");
            _pinnedMessages = pins.OrderByDescending(pin => pin.PinnedAt).ToArray();
            UpdatePinnedMessageBanner();
        }
        catch (ChatApiException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed)
        {
            // An older VDS has no pins route yet. Do not turn an optional banner
            // into repeated modal errors or keep polling a missing endpoint.
            if (!PinSelectionMatches(room, user, version)) return;
            _pinsUnsupportedUntil = DateTimeOffset.UtcNow.AddMinutes(5);
            _pinnedMessages = []; UpdatePinnedMessageBanner();
        }
        catch (ChatApiException ex) when (ex.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
        {
            if (!PinSelectionMatches(room, user, version)) return;
            _pinnedMessages = []; UpdatePinnedMessageBanner();
        }
        catch (Exception) when (IsDisposed || Disposing || load.IsCancellationRequested) { }
        catch
        {
            // Pins are auxiliary read-only metadata. History, drafts, voice
            // playback and the call signaling loop must not wait for this GET.
            if (PinSelectionMatches(room, user, version)) UpdatePinnedMessageBanner();
        }
        finally { if (ReferenceEquals(_pinLoadCts, load)) _pinLoadCts = null; }
    }

    private bool PinSelectionMatches(Guid room, Guid user, int version) =>
        !IsDisposed && !Disposing && _session?.User.Id == user && _selectedConversation?.Id == room && _conversationVersion == version;

    private IReadOnlyList<(PinnedMessageView Pin, MessageRow Row)> AccessiblePinnedRows()
    {
        if (_selectedConversation is null || _session is null) return [];
        var now = DateTimeOffset.UtcNow;
        var rows = _messageList.Controls.OfType<MessageRow>()
            .Where(row => !row.IsDisposed && row.ConversationId == _selectedConversation.Id && row.CanPin &&
                (row.ExpiresAt is null || row.ExpiresAt > now)).ToDictionary(row => row.MessageId);
        // Never ask the server for plaintext/hidden history to fill a banner.
        // A deleted, expired, hidden or unauthenticated row disappears here.
        return _pinnedMessages.Where(pin => pin.ConversationId == _selectedConversation.Id && pin.ExpiresAt > now && rows.ContainsKey(pin.MessageId))
            .Select(pin => (pin, rows[pin.MessageId])).ToArray();
    }

    private void UpdatePinnedMessageBanner()
    {
        if (IsDisposed || Disposing || _pinBannerHeight is null) return;
        var now = DateTimeOffset.UtcNow;
        if (_pinnedMessages.Any(pin => pin.ExpiresAt <= now))
            _pinnedMessages = _pinnedMessages.Where(pin => pin.ExpiresAt > now).ToArray();
        if (_pinnedMessages.Count == 0) _pinExpiryTimer.Stop();
        else if (!_pinExpiryTimer.Enabled) _pinExpiryTimer.Start();
        var rows = AccessiblePinnedRows();
        if (rows.Count == 0)
        {
            _pinnedBanner.ClearMessage();
            if (_pinBannerHeight.Height != 0) _pinBannerHeight.Height = 0;
            return;
        }
        _pinIndex %= rows.Count;
        var current = rows[_pinIndex];
        _pinnedBanner.SetMessage(current.Pin.MessageId, current.Row.SenderHeading, current.Row.StarredPreview, _pinIndex, rows.Count);
        var height = Math.Max((int)Math.Round(60 * DeviceDpi / 96d),
            _pinnedBanner.RequiredTextHeight + (int)Math.Round(18 * DeviceDpi / 96d));
        if (_pinBannerHeight.Height != height) _pinBannerHeight.Height = height;
    }

    private void AddMessagePinAction(ContextMenuStrip menu, MessageRow row)
    {
        var action = menu.Items.AddAction("Mesajı sabitle", ModernMenuIcon.Pin, async (_, _) => await ToggleMessagePinAsync(row));
        menu.Opening += (_, _) =>
        {
            var visible = CanManagePins(_session?.User, _selectedConversation) && row.ConversationId == _selectedConversation?.Id && row.CanPin;
            action.Visible = visible;
            action.Enabled = visible && !_pinMutationRunning && (row.ExpiresAt is null || row.ExpiresAt > DateTimeOffset.UtcNow);
            action.Text = _pinnedMessages.Any(pin => pin.MessageId == row.MessageId && pin.ExpiresAt > DateTimeOffset.UtcNow)
                ? "Sabitlemeyi kaldır" : "Mesajı sabitle";
        };
    }

    private async Task ToggleMessagePinAsync(MessageRow row)
    {
        await Task.Yield(); // Let the owning native message menu finish closing.
        if (_pinMutationRunning || row.IsDisposed || !row.CanPin || _session is null || _selectedConversation is null ||
            row.ConversationId != _selectedConversation.Id || !CanManagePins(_session.User, _selectedConversation)) return;
        var unpin = _pinnedMessages.Any(pin => pin.MessageId == row.MessageId && pin.ExpiresAt > DateTimeOffset.UtcNow);
        var hours = 168;
        if (!unpin)
        {
            using var duration = new PinDurationForm();
            if (duration.ShowDialog(this) != DialogResult.OK || row.IsDisposed || !row.CanPin) return;
            hours = duration.DurationHours;
        }
        await SetMessagePinnedAsync(row, unpin ? null : hours);
    }

    private async Task SetMessagePinnedAsync(MessageRow row, int? hours)
    {
        if (_pinMutationRunning || row.IsDisposed || !row.CanPin || _session is null || _selectedConversation is null ||
            row.ConversationId != _selectedConversation.Id || !CanManagePins(_session.User, _selectedConversation)) return;
        var room = _selectedConversation.Id; var user = _session.User.Id; var version = _conversationVersion;
        var pendingRead = _pinLoadCts; _pinLoadCts = null; pendingRead?.Cancel();
        using var mutation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        _pinMutationRunning = true; _pinMutationCts = mutation;
        try
        {
            if (hours is null) await _api.UnpinMessageAsync(room, row.MessageId, mutation.Token);
            else
            {
                var pin = await _api.PinMessageAsync(room, row.MessageId, hours.Value, mutation.Token);
                if (pin.ConversationId != room || pin.MessageId != row.MessageId || pin.ExpiresAt <= DateTimeOffset.UtcNow)
                    throw new InvalidDataException("Sunucu beklenen sabitleme sonucunu döndürmedi.");
                if (PinSelectionMatches(room, user, version))
                    _pinnedMessages = _pinnedMessages.Where(old => old.MessageId != pin.MessageId).Append(pin)
                        .OrderByDescending(current => current.PinnedAt).Take(3).ToArray();
            }
            if (!PinSelectionMatches(room, user, version)) return;
            if (hours is null) _pinnedMessages = _pinnedMessages.Where(pin => pin.MessageId != row.MessageId).ToArray();
            _pinIndex = 0; UpdatePinnedMessageBanner();
            _pinsUnsupportedUntil = default;
            await RefreshPinnedMessagesAsync(force: true);
        }
        catch (Exception ex)
        {
            if (PinSelectionMatches(room, user, version))
                ShowError(mutation.IsCancellationRequested ? "Sabitleme işleminin sonucu alınamadı. Bağlantıyı kontrol edip yeniden deneyin." :
                    ex is ChatApiException { StatusCode: HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed }
                    ? "Sunucu henüz mesaj sabitlemeyi desteklemiyor. Sunucu güncellemesinden sonra tekrar deneyin."
                    : ex.Message);
        }
        finally { _pinMutationRunning = false; if (ReferenceEquals(_pinMutationCts, mutation)) _pinMutationCts = null; }
    }
}
