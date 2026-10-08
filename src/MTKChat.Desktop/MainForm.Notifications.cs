using System.Runtime.InteropServices;
using MTKChat.Contracts;

namespace MTKChat.Desktop;

internal sealed partial class MainForm
{
    private readonly Dictionary<Guid, ChatNotificationWatermark> _chatNotificationWatermarks = [];
    private Guid? _chatNotificationOwner;
    private DateTimeOffset _chatNotificationObservedAt;
    private bool _chatNotificationsConfigured;
    private bool _chatNotificationFlashing;
    private Action<bool>? _chatNotificationFlashObserverForQa;
    private Func<bool>? _chatNotificationForegroundForQa;
    private Func<DateTimeOffset>? _chatNotificationClockForQa;
    private sealed record ChatNotificationWatermark(DateTimeOffset? LatestAt, int Unread);

    private void ConfigureChatNotifications()
    {
        if (_chatNotificationsConfigured) return;
        _chatNotificationsConfigured = true;
        Activated += OnChatNotificationActivated;
    }

    private void OnChatNotificationActivated(object? sender, EventArgs args) => StopChatNotificationFlash();

    private DateTimeOffset ChatNotificationNow() =>
        _snapshotMode && _chatNotificationClockForQa is { } clock ? clock() : DateTimeOffset.UtcNow;

    private bool ChatNotificationForeground()
    {
        if (_snapshotMode && _chatNotificationForegroundForQa is { } probe) return probe();
        if (ContainsFocus || ReferenceEquals(ActiveForm, this)) return true;
        // Profile/group/admin modal surfaces belong to the foreground app. They
        // are not another external program and must not cause a background alert.
        for (var active = ActiveForm; active is not null; active = active.Owner)
            if (ReferenceEquals(active, this) || active.Modal) return true;
        return false;
    }

    private void ObserveConversationNotifications(IReadOnlyList<ConversationSummary> conversations)
    {
        if (!_chatNotificationsConfigured || _session is null || IsDisposed || Disposing) return;
        var now = ChatNotificationNow();
        if (_chatNotificationOwner != _session.User.Id)
        {
            // The first authenticated successful poll is a baseline, not an
            // arrival event. Account switching cannot replay another account's
            // historical unread badges or carry its watermarks into the new one.
            StopChatNotificationFlash();
            _chatNotificationOwner = _session.User.Id;
            _chatNotificationWatermarks.Clear();
            foreach (var room in conversations)
                _chatNotificationWatermarks[room.Id] = new(room.LastMessageAt, Math.Max(0, room.UnreadCount));
            _chatNotificationObservedAt = now;
            return;
        }

        var eligible = false;
        foreach (var room in conversations)
        {
            var unread = Math.Max(0, room.UnreadCount);
            if (_chatNotificationWatermarks.TryGetValue(room.Id, out var prior))
            {
                var newer = room.LastMessageAt is { } at && (prior.LatestAt is null || at > prior.LatestAt);
                if (newer && unread > prior.Unread && ShouldNotifyConversation(room.Id, now)) eligible = true;
                // Receipt/count changes at the same timestamp only adjust the
                // next unread baseline. A late older summary cannot lower the
                // watermark and make a repeated/newer snapshot flash twice.
                if (newer || room.LastMessageAt == prior.LatestAt)
                    _chatNotificationWatermarks[room.Id] = new(room.LastMessageAt, unread);
            }
            else
            {
                // Summary contracts do not contain CreatedAt or a message ID.
                // Only a new room with an unread message newer than the last
                // successful poll can signal arrival; restored historical rooms,
                // invitations and newly opened empty chats establish a baseline.
                if (unread > 0 && room.LastMessageAt > _chatNotificationObservedAt && ShouldNotifyConversation(room.Id, now))
                    eligible = true;
                _chatNotificationWatermarks[room.Id] = new(room.LastMessageAt, unread);
            }
        }
        _chatNotificationObservedAt = now > _chatNotificationObservedAt ? now : _chatNotificationObservedAt;
        // Retain tombstones across leave/delete/filter changes so merely
        // reappearing in a later list does not replay old unread notifications.
        // The cap bounds a very long-lived window without retaining any bodies.
        if (_chatNotificationWatermarks.Count > 20000)
        {
            var current = conversations.Select(room => room.Id).ToHashSet();
            foreach (var obsolete in _chatNotificationWatermarks.Keys.Where(id => !current.Contains(id))
                         .Take(_chatNotificationWatermarks.Count - 20000).ToArray())
                _chatNotificationWatermarks.Remove(obsolete);
        }
        if (eligible && !ChatNotificationForeground()) FlashChatTaskbar();
    }

    private void FlashChatTaskbar()
    {
        if (IsDisposed || Disposing) return;
        _chatNotificationFlashing = true;
        _chatNotificationFlashObserverForQa?.Invoke(true);
        if (_snapshotMode || !IsHandleCreated) return;
        // Silent, bounded taskbar-only flash. Do not play sound, show message
        // bodies/toasts, change focus or interrupt the user's current program.
        var request = new ChatFlashInfo
        {
            Size = (uint)Marshal.SizeOf<ChatFlashInfo>(), Window = Handle,
            Flags = 2, Count = 3, Timeout = 0 // FLASHW_TRAY, system default frequency.
        };
        FlashWindowEx(ref request);
    }

    private void StopChatNotificationFlash()
    {
        if (!_chatNotificationFlashing) return;
        _chatNotificationFlashing = false;
        _chatNotificationFlashObserverForQa?.Invoke(false);
        if (_snapshotMode || !IsHandleCreated) return;
        var request = new ChatFlashInfo { Size = (uint)Marshal.SizeOf<ChatFlashInfo>(), Window = Handle, Flags = 0 };
        FlashWindowEx(ref request); // FLASHW_STOP, including activation after a finite flash.
    }

    private void DisposeChatNotifications()
    {
        StopChatNotificationFlash();
        if (_chatNotificationsConfigured) Activated -= OnChatNotificationActivated;
        _chatNotificationsConfigured = false;
        _chatNotificationOwner = null;
        _chatNotificationWatermarks.Clear();
        _chatNotificationFlashObserverForQa = null;
        _chatNotificationForegroundForQa = null;
        _chatNotificationClockForQa = null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ChatFlashInfo
    {
        internal uint Size;
        internal IntPtr Window;
        internal uint Flags;
        internal uint Count;
        internal uint Timeout;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlashWindowEx(ref ChatFlashInfo info);
}
