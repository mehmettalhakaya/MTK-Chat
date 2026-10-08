using MTKChat.Contracts;

namespace MTKChat.Desktop;

// Cache encoded thumbnails only. Each control owns its decoded bitmap so repaint/disposal are independent.
internal sealed class AvatarCache(ChatApiClient api)
{
    private readonly Dictionary<(Guid, string), Task<byte[]?>> _requests = new();
    private readonly Dictionary<(Guid, string), Task<byte[]?>> _groupRequests = new();
    private readonly SemaphoreSlim _downloads = new(4);

    internal async Task ApplyAsync(AvatarView view, ChatUser user)
    {
        if (view.IsDisposed) return;
        view.Initials = UserPresentation.Initials(user.DisplayName);
        view.Invalidate();
        // Unchanged presence polling should not flash the initials between thumbnail paints.
        if (Equals(view.Tag, (user.Id, user.PhotoVersion))) return;
        view.AvatarColor = Theme.SurfaceHover;
        view.SetPhoto(null);
        view.Tag = (user.Id, user.PhotoVersion);
        if (user.PhotoVersion is null) return;
        var key = (user.Id, user.PhotoVersion);
        try
        {
            if (!_requests.TryGetValue(key, out var request))
            {
                // A bounded cache avoids retaining every historical profile revision.
                if (_requests.Count >= 128) _requests.Clear();
                request = DownloadAsync(user.Id);
                _requests[key] = request;
            }
            var bytes = await request;
            // A late missing/404 photo must not invalidate a newer revision
            // already assigned to the same rail or participant avatar.
            if (bytes is null) { _requests.Remove(key); if (!view.IsDisposed && Equals(view.Tag, key)) view.Tag = null; return; }
            if (view.IsDisposed || !Equals(view.Tag, key)) return;
            view.SetEncodedPhoto(bytes);
        }
        catch
        {
            _requests.Remove(key); // Allow a later presence refresh to recover a transient network failure.
            if (!view.IsDisposed && Equals(view.Tag, key)) view.Tag = null;
        }
    }

    private async Task<byte[]?> DownloadAsync(Guid id)
    {
        await _downloads.WaitAsync();
        try { return await api.GetPhotoAsync(id); }
        finally { _downloads.Release(); }
    }

    internal async Task ApplyGroupAsync(AvatarView view, ConversationSummary conversation)
    {
        if (view.IsDisposed) return;
        view.Initials = UserPresentation.Initials(conversation.Title);
        view.Invalidate();
        var tag = ("group", conversation.Id, conversation.PhotoVersion);
        if (Equals(view.Tag, tag)) return;
        view.AvatarColor = Theme.Accent;
        view.SetPhoto(null);
        view.Tag = tag;
        if (conversation.PhotoVersion is null) return;
        var key = (conversation.Id, conversation.PhotoVersion);
        try
        {
            if (!_groupRequests.TryGetValue(key, out var request))
            {
                if (_groupRequests.Count >= 128) _groupRequests.Clear();
                request = DownloadGroupAsync(conversation.Id);
                _groupRequests[key] = request;
            }
            var bytes = await request;
            if (bytes is null) { _groupRequests.Remove(key); if (!view.IsDisposed && Equals(view.Tag, tag)) view.Tag = null; return; }
            if (view.IsDisposed || !Equals(view.Tag, tag)) return;
            view.SetEncodedPhoto(bytes);
        }
        catch
        {
            _groupRequests.Remove(key);
            if (!view.IsDisposed && Equals(view.Tag, tag)) view.Tag = null;
        }
    }

    private async Task<byte[]?> DownloadGroupAsync(Guid id)
    {
        await _downloads.WaitAsync();
        try { return await api.GetGroupPhotoAsync(id); }
        finally { _downloads.Release(); }
    }
}
