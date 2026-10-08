namespace MTKChat.Desktop;

internal sealed partial class MainForm
{
    private LocalChatPreferences? _chatPreferences;
    private bool _preferencesUnavailable;

    private LocalChatPreferences? ChatPreferences()
    {
        if (_session is null) return null;
        if (_chatPreferences?.UserId == _session.User.Id) return _chatPreferences;
        // Snapshot/QA never writes a real account's bookmarks. Production loads
        // the protected file once after authentication, before list reconciliation.
        _chatPreferences = LocalChatPreferences.Memory(_session.User.Id);
        // A failed file belongs to its account, not to this window forever.
        // Switching accounts must not inherit an unrelated write lockout.
        _preferencesUnavailable = false;
        return _chatPreferences;
    }

    private async Task LoadChatPreferencesAsync()
    {
        if (_session is null || _snapshotMode) { ChatPreferences(); return; }
        var userId = _session.User.Id;
        try
        {
            var preferences = await Task.Run(() => LocalChatPreferences.Open(userId));
            ApplyLoadedChatPreferences(userId, preferences, unavailable: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            System.Security.Cryptography.CryptographicException or System.Text.Json.JsonException)
        {
            ApplyLoadedChatPreferences(userId, LocalChatPreferences.Memory(userId), unavailable: true);
            // A corrupt preference file must not break login or get overwritten.
            // The first attempted edit explains the problem instead of faking persistence.
        }
    }

    private bool ApplyLoadedChatPreferences(Guid userId, LocalChatPreferences preferences, bool unavailable)
    {
        // Loading runs off the UI thread. A late result from a previous login (or
        // a closed window) must never publish its bookmarks to a different user.
        if (IsDisposed || Disposing || _session?.User.Id != userId || preferences.UserId != userId) return false;
        _chatPreferences = preferences;
        _preferencesUnavailable = unavailable;
        return true;
    }

    private bool IsFavoriteConversation(Guid roomId) => ChatPreferences()?.IsFavorite(roomId) == true;

    private bool IsPinnedConversation(Guid roomId) => ChatPreferences()?.IsPinned(roomId) == true;

    private void SetPinnedConversation(Guid roomId, bool pinned)
    {
        EnsurePreferencesWritable();
        ChatPreferences()?.SetPinned(roomId, pinned);
        RefreshPersonalConversationIndicators();
        ApplyConversationOrder();
    }

    private void SetFavoriteConversation(Guid roomId, bool favorite)
    {
        EnsurePreferencesWritable();
        ChatPreferences()?.SetFavorite(roomId, favorite);
        FilterConversations(_premiumSearch.Text);
    }

    private void EnsurePreferencesWritable()
    {
        ChatPreferences();
        if (_preferencesUnavailable)
            throw new InvalidOperationException("Kişisel tercihler okunamadığı için değişiklik kaydedilemiyor. Mevcut dosya korunuyor.");
    }
}
