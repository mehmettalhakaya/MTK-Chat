using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MTKChat.Contracts;
using MTKChat.Cryptography;

namespace MTKChat.Desktop;

internal sealed partial class MainForm
{
    // Plaintext previews live only in this account's form memory. No preference,
    // diagnostic log, request or database stores them. Keep one bounded line per room.
    private readonly Dictionary<Guid, ConversationPreview> _conversationPreviews = new();
    private readonly Dictionary<Guid, object> _previewRequests = new();
    private readonly HashSet<Guid> _suppressedDeletedPreviews = new();
    private readonly CancellationTokenSource _previewLifetime = new();
    private Guid? _previewOwner;
    private Task _previewRefreshTask = Task.CompletedTask;
    private bool _previewRefreshAgain;
    private bool _previewSessionExpired;
    private int _previewVisibilityVersion;
    private const string DeletedConversationPreview = "Bu mesaj silindi";
    private sealed record ConversationPreview(DateTimeOffset? LastAt, DateTimeOffset? SummaryAt, string Text, DateTimeOffset? ExpiresAt,
        DateTimeOffset RefreshAfter, Guid? MessageId = null, bool Deleted = false,
        DateTimeOffset? DeletedSummaryAt = null, bool DeletedMetadataAvailable = false,
        DateTimeOffset? DeletedExpiresAt = null);

    private void EnsurePreviewOwner()
    {
        if (_previewOwner == _session?.User.Id) return;
        _previewOwner = _session?.User.Id;
        _previewSessionExpired = false;
        _conversationPreviews.Clear();
        _conversationActivity.Clear();
        _previewRequests.Clear(); // Late continuations from another account cannot commit.
        _suppressedDeletedPreviews.Clear();
    }

    private static bool PreviewSummaryMatches(ConversationPreview preview, ConversationSummary room) =>
        (preview.SummaryAt == room.LastMessageAt || preview.LastAt == room.LastMessageAt) &&
        preview.DeletedSummaryAt == room.LastDeletedMessageAt &&
        preview.DeletedMetadataAvailable == room.DeletedMessageMetadataAvailable;

    private DateTimeOffset? DeletedPreviewAt(ConversationSummary room)
    {
        if (_suppressedDeletedPreviews.Contains(room.Id) || room.LastDeletedMessageAt is not { } deleted ||
            room.LastMessageAt > deleted) return null;
        // History may have learned an expiry before the next summary poll. Never
        // let a date-only summary revive that already expired deletion marker.
        if (_conversationPreviews.TryGetValue(room.Id, out var known) && PreviewSummaryMatches(known, room) &&
            (known.DeletedExpiresAt <= DateTimeOffset.UtcNow ||
             known.Deleted && known.LastAt == deleted && known.ExpiresAt <= DateTimeOffset.UtcNow)) return null;
        return deleted;
    }

    private static bool PreviewMetadataEquals(ConversationSummary left, ConversationSummary right) =>
        left.Id == right.Id && left.LastMessageAt == right.LastMessageAt &&
        left.LastDeletedMessageAt == right.LastDeletedMessageAt &&
        left.DeletedMessageMetadataAvailable == right.DeletedMessageMetadataAvailable;

    private bool HistoryPreviewMetadataCurrent(ConversationSummary requested) =>
        _selectedConversation is { } current && PreviewMetadataEquals(requested, current);

    private string ConversationPreviewText(ConversationSummary room)
    {
        EnsurePreviewOwner();
        var valid = _conversationPreviews.TryGetValue(room.Id, out var preview) &&
            PreviewSummaryMatches(preview, room) && !(preview.ExpiresAt <= DateTimeOffset.UtcNow);
        // A server-authorized tombstone contains no old message content or key.
        // It also invalidates an old decrypted preview before another GET completes.
        if (DeletedPreviewAt(room) is { } deleted && !(valid && !preview!.Deleted && preview.LastAt > deleted))
            return DeletedConversationPreview;
        if (!valid || _suppressedDeletedPreviews.Contains(room.Id) ||
            preview!.Deleted && room.DeletedMessageMetadataAvailable && room.LastDeletedMessageAt is null &&
            !(preview.LastAt <= room.LastMessageAt)) return "";
        return preview.Text;
    }

    private string ConversationPreviewTime(ConversationSummary room)
    {
        // Activity survives deletion; plaintext previews still obey visibility.
        return ConversationTime(ConversationActivityAt(room));
    }

    private void ReconcileConversationPreviews(IReadOnlyList<ConversationSummary> rooms)
    {
        EnsurePreviewOwner();
        var current = rooms.ToDictionary(room => room.Id);
        // Only a fresh access-checked summary may end a local deletion/block barrier.
        _suppressedDeletedPreviews.Clear();
        foreach (var id in _conversationActivity.Keys.Where(id => !current.ContainsKey(id)).ToArray())
            _conversationActivity.Remove(id);
        foreach (var room in rooms) ObserveConversationActivity(room);
        foreach (var id in _conversationPreviews.Keys.Concat(_previewRequests.Keys).Distinct().ToArray())
        {
            if (!current.TryGetValue(id, out var room))
            {
                _conversationPreviews.Remove(id);
                _previewRequests.Remove(id);
                continue;
            }
            if (_conversationPreviews.TryGetValue(id, out var preview) &&
                (preview.SummaryAt != room.LastMessageAt || preview.DeletedSummaryAt != room.LastDeletedMessageAt ||
                 preview.DeletedMetadataAvailable != room.DeletedMessageMetadataAvailable))
            {
                if (preview.LastAt == room.LastMessageAt && preview.DeletedSummaryAt == room.LastDeletedMessageAt &&
                    preview.DeletedMetadataAvailable == room.DeletedMessageMetadataAvailable)
                    // The summary caught up with a newer already-authenticated history.
                    _conversationPreviews[id] = preview with { SummaryAt = room.LastMessageAt };
                else
                {
                    // Distinguish unchanged/lagging metadata from an actual rollback
                    // after private deletion, blocking or expiry, including clear to null.
                    _conversationPreviews.Remove(id);
                    _previewRequests.Remove(id);
                }
            }
        }
        foreach (var room in rooms) ApplyConversationPreviewLabel(room);
    }

    private void ApplyConversationPreviewLabel(ConversationSummary room)
    {
        var card = _conversationList.Controls.OfType<RoundedPanel>()
            .FirstOrDefault(c => c.Tag is ConversationSummary current && current.Id == room.Id);
        if (card is null) return;
        // A preview GET may finish after an activity-only list update with the
        // same visible message date. Never repaint its older activity metadata.
        room = (ConversationSummary)card.Tag!;
        var label = card.Controls.Find("ConversationLastMessagePreview", true).OfType<Label>().SingleOrDefault();
        var text = ConversationPreviewText(room);
        // Updating one label preserves popup owners, avatar, scroll and focus.
        if (label is not null && label.Text != text) label.Text = text;
        var clock = card.Controls.Find("ConversationLastMessageTime", true).OfType<Label>().SingleOrDefault();
        var time = ConversationPreviewTime(room);
        if (clock is not null && clock.Text != time)
        {
            clock.Text = time;
            card.PerformLayout(); // An empty preview has no phantom clock slot.
        }
    }

    private void UpdateConversationPreviewFromHistory(Guid roomId, IReadOnlyList<StoredMessage> messages)
    {
        EnsurePreviewOwner();
        var room = _conversationList.Controls.OfType<RoundedPanel>().Select(c => c.Tag)
            .OfType<ConversationSummary>().FirstOrDefault(c => c.Id == roomId);
        if (room is null) return;
        var now = DateTimeOffset.UtcNow;
        ObserveConversationActivity(room, messages);
        var last = LatestPreviewMessage(room, messages, now);
        // Keep expiry metadata even after that row drops out of the visible
        // candidate set, otherwise a stale summary could revive its marker.
        var deletedExpiry = messages.FirstOrDefault(message => message.ConversationId == roomId &&
            message.DeletedForEveryone && message.CreatedAt == room.LastDeletedMessageAt)?.ExpiresAt;
        if (deletedExpiry is null && _conversationPreviews.TryGetValue(roomId, out var previous) && PreviewSummaryMatches(previous, room))
            deletedExpiry = previous.DeletedExpiresAt;
        var text = "";
        var authenticated = last is null;
        if (last is { DeletedForEveryone: true })
        {
            authenticated = true;
            text = DeletedConversationPreview;
        }
        else if (last is not null && _renderedMessageRows.TryGetValue(last.Id, out var rendered) && rendered.Row.CanStar)
        {
            authenticated = true;
            text = PreviewLine(last.Kind, rendered.Row.StarredPreview);
        }
        // A newer committed history wins over an older in-flight preview GET.
        _previewRequests.Remove(roomId);
        _conversationPreviews[roomId] = new(last?.CreatedAt, room.LastMessageAt, text, last?.ExpiresAt,
            now.AddSeconds(authenticated ? 60 : 15), last?.Id, last?.DeletedForEveryone ?? false,
            room.LastDeletedMessageAt, room.DeletedMessageMetadataAvailable, deletedExpiry);
        ApplyConversationPreviewLabel(room);
        ApplyConversationOrder();
    }

    private StoredMessage? LatestPreviewMessage(ConversationSummary room, IEnumerable<StoredMessage> messages, DateTimeOffset now) =>
        messages.Where(m => PreviewMessageEligible(room, m, now))
            .OrderByDescending(m => m.CreatedAt).FirstOrDefault();

    private bool PreviewMessageEligible(ConversationSummary room, StoredMessage message, DateTimeOffset now)
    {
        if (message.ConversationId != room.Id || message.ExpiresAt <= now ||
            room.ActivityMetadataAvailable && room.LastActivityAt is null || _suppressedDeletedPreviews.Contains(room.Id)) return false;
        var addressed = message.SenderId == _session?.User.Id ||
            message.Payloads.Any(payload => payload.RecipientId == _session?.User.Id);
        if (!message.DeletedForEveryone) return addressed;
        if (room.DeletedMessageMetadataAvailable)
            return room.LastDeletedMessageAt is { } deleted && message.CreatedAt <= deleted &&
                (addressed || message.CreatedAt == deleted) ||
                // Selected history is polled before the list. Exact addressed
                // evidence may report deletion while its live summary still lags.
                // An authoritative null/rollback must not accept that evidence.
                room.LastMessageAt >= message.CreatedAt && (addressed ||
                    _conversationPreviews.TryGetValue(room.Id, out var prior) && prior.MessageId == message.Id);
        // An old server strips all recipient envelopes on deletion. Only an exact
        // previously addressed message id is evidence, never a matching timestamp.
        return addressed ||
            _conversationPreviews.TryGetValue(room.Id, out var known) && known.MessageId == message.Id ||
            _conversationActivity.TryGetValue(room.Id, out var activity) && activity.MessageId == message.Id;
    }

    private static string PreviewLine(string kind, string text)
    {
        if (kind.StartsWith("image/", StringComparison.Ordinal)) return "Fotoğraf";
        if (kind == "audio/wav") return "Sesli mesaj";
        // A file row also contains its size/download affordance, not message text.
        if (kind == "file") text = text.Split('\n')[0].TrimStart('▤', ' ');
        var line = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (line.Length <= 280) return line;
        var end = char.IsHighSurrogate(line[279]) ? 279 : 280;
        return line[..end] + "…";
    }

    private void ScheduleConversationPreviews()
    {
        if (IsDisposed || Disposing || _session is null) return;
        EnsurePreviewOwner();
        if (_previewSessionExpired) return;
        if (!_previewRefreshTask.IsCompleted) { _previewRefreshAgain = true; return; }
        _previewRefreshTask = RefreshConversationPreviewsAsync();
    }

    private void InvalidateConversationVisibility(Guid? roomId = null, bool preserveActivity = true)
    {
        EnsurePreviewOwner();
        _previewVisibilityVersion++;
        var affected = _conversationList.Controls.OfType<RoundedPanel>().Select(c => c.Tag)
            .OfType<ConversationSummary>().Where(r => roomId is null || r.Id == roomId).ToArray();
        foreach (var room in affected)
        {
            if (!preserveActivity) _conversationActivity.Remove(room.Id);
            _conversationPreviews.Remove(room.Id);
            _previewRequests.Remove(room.Id);
            _suppressedDeletedPreviews.Add(room.Id);
            ApplyConversationPreviewLabel(room);
        }
        if (_selectedConversation is null || roomId is not null && _selectedConversation.Id != roomId) return;
        CloseComposerEmojis();
        CloseMessageInfo();
        // A banner owns a copied, decrypted preview. Erase it at the same access
        // boundary even if an open native menu prevents row disposal right now.
        ResetPinnedMessages();
        // A successful private deletion/block is an authorization boundary for
        // in-flight reads, not merely a hint to redraw the same history later.
        _conversationVersion++;
        CancelHistoryLoad();
        _renderFingerprint = null;
        ClearRenderedMessageCache();
        if (MessagePopupOpen()) return; // Never dispose a native popup's owner.
        _historyLoadedConversation = null;
        _messageList.SuspendLayout();
        try
        {
            foreach (Control row in _messageList.Controls.Cast<Control>().ToArray()) row.Dispose();
            _messageList.Controls.Clear();
            _messageList.Controls.Add(CreateEmptyState("Mesajlar yükleniyor..."));
        }
        finally { _messageList.ResumeLayout(); }
        ResizeBubbles();
    }

    private async Task RefreshConversationPreviewsAsync()
    {
        // A low-priority bounded queue never holds the list/history gate or UI
        // busy flag. At most two GETs run concurrently, with a 12-second deadline.
        do
        {
            _previewRefreshAgain = false;
            if (IsDisposed || Disposing || _session is null || _previewSessionExpired || _previewLifetime.IsCancellationRequested) return;
            var now = DateTimeOffset.UtcNow;
            var rooms = _conversationList.Controls.OfType<RoundedPanel>().Select(c => c.Tag)
                .OfType<ConversationSummary>().ToArray();
            foreach (var room in rooms) ApplyConversationPreviewLabel(room);
            var pending = rooms.Where(room => room.LastMessageAt is not null && DeletedPreviewAt(room) is null &&
                !_suppressedDeletedPreviews.Contains(room.Id) && room.Id != _selectedConversation?.Id &&
                (!_conversationPreviews.TryGetValue(room.Id, out var p) || !PreviewSummaryMatches(p, room) ||
                 p.RefreshAfter <= now || p.ExpiresAt <= now)).ToArray();
            foreach (var batch in pending.Chunk(2))
            {
                if (IsDisposed || Disposing || _previewSessionExpired || _previewLifetime.IsCancellationRequested) return;
                await Task.WhenAll(batch.Select(LoadConversationPreviewAsync));
                await Task.Yield();
            }
        } while (_previewRefreshAgain);
    }

    private async Task LoadConversationPreviewAsync(ConversationSummary requested)
    {
        if (_session is null || IsDisposed || Disposing || _previewSessionExpired) return;
        if (DeletedPreviewAt(requested) is not null || requested.LastMessageAt is null) return;
        var owner = _session.User.Id;
        var request = new object();
        _previewRequests[requested.Id] = request;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_previewLifetime.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(12));
        string text = "";
        DateTimeOffset? expiry = null;
        DateTimeOffset? actualLastAt = requested.LastMessageAt;
        DateTimeOffset? deletedExpiry = null;
        var success = false;
        Guid? messageId = null;
        var deleted = false;
        ConversationPreview? retained = null;
        if (_conversationPreviews.TryGetValue(requested.Id, out var known) && PreviewSummaryMatches(known, requested))
        {
            // A failed GET cannot resurrect the clock of a known expired/empty
            // history. Preserve only its date/expiry metadata, never stale text.
            actualLastAt = known.LastAt;
            expiry = known.ExpiresAt;
            deletedExpiry = known.DeletedExpiresAt;
        }
        try
        {
            var messages = await _api.GetPreviewMessagesAsync(requested.Id, requested.LastMessageAt!.Value, deadline.Token);
            deadline.Token.ThrowIfCancellationRequested();
            if (!PreviewRequestCurrent(requested, owner, request)) return;
            if (messages.Any(m => m.ConversationId != requested.Id))
                throw new InvalidDataException("Önizleme farklı bir sohbete ait.");
            ObserveConversationActivity(requested, messages);
            deletedExpiry = messages.FirstOrDefault(message => message.DeletedForEveryone &&
                message.CreatedAt == requested.LastDeletedMessageAt)?.ExpiresAt ?? deletedExpiry;
            var last = LatestPreviewMessage(requested, messages, DateTimeOffset.UtcNow);
            actualLastAt = last?.CreatedAt;
            messageId = last?.Id;
            deleted = last?.DeletedForEveryone ?? false;
            // Even an unavailable key must not leave an expired clock behind.
            expiry = last?.ExpiresAt;
            if (deleted) text = DeletedConversationPreview;
            else if (last is not null)
            {
                var payload = last.Payloads.FirstOrDefault(p => p.RecipientId == owner)
                    ?? throw new CryptographicException("Önizleme zarfı bu hesaba ait değil.");
                var plaintext = await DecryptWithKeyRefreshAsync(last, payload, deadline.Token, new HashSet<Guid>());
                try
                {
                    var content = last.Kind == "file"
                        ? JsonSerializer.Deserialize<EncryptedFileDescriptor>(plaintext) is { } file &&
                          file.StorageToken == last.Attachment?.StorageToken && file.Size is > 0 and <= FileCryptography.MaxFileBytes
                            ? FileCryptography.SafeName(file.FileName) : throw new InvalidDataException("Dosya önizlemesi geçersiz.")
                        : last.Kind == "text" ? Encoding.UTF8.GetString(plaintext) : "";
                    text = PreviewLine(last.Kind, content);
                }
                finally { CryptographicOperations.ZeroMemory(plaintext); }
            }
            success = true;
        }
        catch (Exception ex)
        {
            if (!PreviewRequestCurrent(requested, owner, request)) return;
            // The optional preview is never modal and never marks content read.
            // Do not display a decrypt/key error or an API placeholder as a message.
            if (ex is ChatApiException { StatusCode: System.Net.HttpStatusCode.Unauthorized })
            {
                _previewSessionExpired = true;
                RecordNetworkFailure(ex);
            }
            else if (ex is ChatTransportException or OperationCanceledException ||
                     ex is ChatApiException api && ((int)api.StatusCode >= 500 || (int)api.StatusCode is 408 or 429))
            {
                // An optional refresh losing connectivity must not erase already
                // authenticated text. Never retain it for bad signatures, 401/403,
                // a changed summary or a locally expired message.
                if (_conversationPreviews.TryGetValue(requested.Id, out var cached) &&
                    PreviewSummaryMatches(cached, requested) &&
                    (cached.ExpiresAt is null || cached.ExpiresAt > DateTimeOffset.UtcNow)) retained = cached;
            }
        }
        if (!PreviewRequestCurrent(requested, owner, request)) return;
        _previewRequests.Remove(requested.Id);
        var refreshAfter = DateTimeOffset.UtcNow.AddSeconds(success ? 60 : 15);
        _conversationPreviews[requested.Id] = retained is not null ? retained with { RefreshAfter = refreshAfter } :
            new(actualLastAt, requested.LastMessageAt, text, expiry, refreshAfter, messageId, deleted,
                requested.LastDeletedMessageAt, requested.DeletedMessageMetadataAvailable, deletedExpiry);
        ApplyConversationPreviewLabel(requested);
        ApplyConversationOrder();
    }

    private bool PreviewRequestCurrent(ConversationSummary requested, Guid owner, object request) =>
        !IsDisposed && !Disposing && !_previewLifetime.IsCancellationRequested && _session?.User.Id == owner &&
        _previewRequests.TryGetValue(requested.Id, out var currentRequest) && ReferenceEquals(currentRequest, request) &&
        _conversationList.Controls.OfType<RoundedPanel>().Any(c => c.Tag is ConversationSummary room &&
            room.Id == requested.Id && room.LastMessageAt == requested.LastMessageAt &&
            room.LastDeletedMessageAt == requested.LastDeletedMessageAt &&
            room.DeletedMessageMetadataAvailable == requested.DeletedMessageMetadataAvailable);
}
