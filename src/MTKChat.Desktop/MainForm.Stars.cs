namespace MTKChat.Desktop;

internal sealed partial class MainForm
{
    private void ToggleMessageStar(MessageRow row)
    {
        if (row.IsDisposed || !row.CanStar || _selectedConversation?.Id != row.ConversationId || _session is null) return;
        try
        {
            EnsurePreferencesWritable();
            var starred = !row.IsStarred;
            ChatPreferences()!.SetStarred(row.ConversationId, row.MessageId, starred);
            // Keep the same row and popup alive. Polling can reuse it; there is no
            // forced decrypt, media decode, scroll reset or premature menu disposal.
            row.SetStarred(starred);
        }
        catch (Exception ex) { if (!IsDisposed) ShowError(ex.Message); }
    }

    private IReadOnlyList<StarredMessagePreview> CurrentStarredMessages()
    {
        if (_selectedConversation is null) return [];
        var ids = ChatPreferences()?.StarredIds(_selectedConversation.Id);
        // Only currently accessible, authenticated/decrypted rows can be listed.
        // Deleted, expired, hidden or unavailable messages cannot be resurrected
        // from bookmarks, which store no text or media in the first place.
        return _messageList.Controls.OfType<MessageRow>()
            .Where(row => row.ConversationId == _selectedConversation.Id && row.CanStar && ids?.Contains(row.MessageId) == true)
            .OrderByDescending(row => row.CreatedAt)
            .Select(row => new StarredMessagePreview(row.MessageId, row.SenderHeading, row.SenderColor,
                row.CreatedAt, row.StarredPreview)).ToArray();
    }

    private async Task OpenStarredMessagesAsync(MTKChat.Contracts.ConversationSummary conversation)
    {
        // Let the native context menu finish closing before an async selection
        // replaces cards or opens another form owned by the main window.
        await Task.Yield();
        if (IsDisposed || _session is null) return;
        try
        {
            var card = _conversationList.Controls.OfType<RoundedPanel>()
                .FirstOrDefault(c => c.Tag is MTKChat.Contracts.ConversationSummary room && room.Id == conversation.Id);
            if (card?.Tag is not MTKChat.Contracts.ConversationSummary current) return;
            if (_selectedConversation?.Id != current.Id) await SelectConversationAsync(current, card);
            else await RefreshMessagesAsync(silent: true);
            if (IsDisposed || _selectedConversation?.Id != current.Id) return;
            if (_historyLoadedConversation != current.Id)
            { ShowError("Yıldızlı mesajlar için sohbet yüklenemedi. Bağlantıyı kontrol edip tekrar deneyin."); return; }
            using var dialog = new StarredMessagesForm(current.Title, CurrentStarredMessages());
            var ownerId = _session.User.Id;
            using var refresh = new System.Windows.Forms.Timer { Interval = 1000 };
            refresh.Tick += (_, _) =>
            {
                if (dialog.IsDisposed) { refresh.Stop(); return; }
                if (IsDisposed || _session?.User.Id != ownerId || _selectedConversation?.Id != current.Id)
                { refresh.Stop(); dialog.Close(); return; }
                // Reuse already loaded/authenticated rows; never issue extra HTTP
                // requests, decrypt again or capture a half-committed history batch.
                if (!_refreshing) dialog.UpdateMessages(CurrentStarredMessages());
            };
            refresh.Start();
            try
            {
                if (dialog.ShowDialog(this) == DialogResult.OK && dialog.SelectedMessageId is { } id)
                    ScrollToStarredMessage(id);
            }
            finally { refresh.Stop(); }
        }
        catch (Exception ex) { if (!IsDisposed) ShowError(ex.Message); }
    }

    private void ScrollToStarredMessage(Guid id)
    {
        var row = _messageList.Controls.OfType<MessageRow>().FirstOrDefault(r => r.MessageId == id);
        // AutoScroll shifts child coordinates. Convert back to content coordinates
        // before jumping, otherwise an already-scrolled history jumps above its target.
        if (row is not null) _messageList.ScrollToOffset(Math.Max(0,
            row.Top + _messageList.Offset - _messageList.Padding.Top));
    }

    internal void PopulateStarredSnapshot()
    {
        PopulateReferenceSnapshot();
        var rows = _messageList.Controls.OfType<MessageRow>().Take(2).ToArray();
        foreach (var row in rows) ToggleMessageStar(row);
    }

    internal StarredMessagesForm StarredSnapshotForm() => new(_selectedConversation!.Title, CurrentStarredMessages());
}
