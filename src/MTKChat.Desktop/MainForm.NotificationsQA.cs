using MTKChat.Contracts;

namespace MTKChat.Desktop;

internal sealed partial class MainForm
{
    internal static IReadOnlyList<string> VerifyChatNotifications()
    {
        using var context = new HistoryQaUiContext();
        using var fixture = new HistoryQaFixture();
        var form = fixture.Form;
        var checks = new List<string>();
        void Require(bool condition, string check)
        {
            if (!condition) throw new InvalidOperationException("Chat notification QA: " + check);
            checks.Add(check);
        }
        var flashes = 0;
        var stops = 0;
        var foreground = false;
        var now = new DateTimeOffset(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);
        form._chatNotificationFlashObserverForQa = start => { if (start) flashes++; else stops++; };
        form._chatNotificationForegroundForQa = () => foreground;
        form._chatNotificationClockForQa = () => now;
        form.ConfigureChatNotifications();
        form.ConfigureChatNotifications();
        var direct = fixture.InitialRoom with { Kind = "direct", UnreadCount = 4, LastMessageAt = now.AddMinutes(-10) };
        form.ObserveConversationNotifications([direct]);
        Require(flashes == 0 && form._chatNotificationWatermarks.Count == 1,
            "First successful authenticated inbox metadata establishes a quiet unread baseline");
        form.ObserveConversationNotifications([direct]);
        Require(flashes == 0, "An identical initial poll never flashes");
        now = now.AddMinutes(1);
        var firstArrival = direct with { UnreadCount = 5, LastMessageAt = now };
        form.ObserveConversationNotifications([firstArrival]);
        Require(flashes == 1 && form._chatNotificationFlashing,
            "A genuinely newer unread direct-chat arrival while background starts one silent taskbar notification");
        form.ObserveConversationNotifications([firstArrival]);
        Require(flashes == 1, "Repeated successful polling of the same arrival cannot flash again");
        form.ObserveConversationNotifications([direct]);
        form.ObserveConversationNotifications([firstArrival]);
        Require(flashes == 1, "A stale older summary cannot roll back the high-water mark or replay the same arrival");
        form.ObserveConversationNotifications([firstArrival with { UnreadCount = 9 }]);
        Require(flashes == 1, "Unread/receipt metadata changes at an unchanged timestamp are not new-message alerts");
        now = now.AddMinutes(1);
        var outgoingOnly = firstArrival with { UnreadCount = 9, LastMessageAt = now };
        form.ObserveConversationNotifications([outgoingOnly]);
        Require(flashes == 1, "A newer outgoing/metadata timestamp without unread increase is quiet");
        form.OnChatNotificationActivated(form, EventArgs.Empty);
        form.OnChatNotificationActivated(form, EventArgs.Empty);
        Require(stops == 1 && !form._chatNotificationFlashing, "Activation stops flashing exactly once, including after its bounded native cycle");
        foreground = true;
        now = now.AddMinutes(1);
        var foregroundArrival = outgoingOnly with { UnreadCount = 10, LastMessageAt = now };
        form.ObserveConversationNotifications([foregroundArrival]);
        foreground = false;
        form.ObserveConversationNotifications([foregroundArrival]);
        Require(flashes == 1, "Foreground activity advances the watermark quietly and is not replayed after switching apps");
        // Real foreground predicate includes a focused composer and owned modal
        // surfaces; the arrival tests above use an observer, never FlashWindowEx.
        form._chatNotificationForegroundForQa = null;
        form.Activate(); form._premiumComposer.Focus(); Application.DoEvents();
        HistoryQaUntil(() => form.ContainsFocus);
        Require(form.ChatNotificationForeground(), "The native focused composer counts as foreground and suppresses notification interruption");
        using (var child = new ModernForm { Text = "Notification QA owned child", Size = new Size(260, 180) })
        {
            child.Show(form); child.Activate(); Application.DoEvents();
            // Window-manager activation is asynchronous. Wait for the actual
            // owned HWND rather than replacing the predicate with a fake value.
            HistoryQaUntil(() => ReferenceEquals(ActiveForm, child));
            Require(form.ChatNotificationForeground(), "A native active form owned by the chat is foreground, not another app");
            child.Hide();
        }
        form._chatNotificationForegroundForQa = () => foreground;
        var group = fixture.InitialRoom with { Id = Guid.NewGuid(), Title = "Sessiz grup", UnreadCount = 1, LastMessageAt = now.AddDays(-1) };
        now = now.AddMinutes(1);
        form.ObserveConversationNotifications([foregroundArrival, group]);
        Require(flashes == 1, "A first-seen group with historical unread messages establishes a quiet baseline");
        var old = group;
        now = now.AddMinutes(1);
        group = group with { UnreadCount = 2, LastMessageAt = now };
        form.ObserveConversationNotifications([foregroundArrival, group]);
        Require(flashes == 2, "A group uses the same newer unread arrival rule as a direct chat");
        form.SetArchivedConversation(group.Id, true);
        now = now.AddMinutes(1); group = group with { UnreadCount = 3, LastMessageAt = now };
        form.ObserveConversationNotifications([foregroundArrival, group]);
        Require(flashes == 2, "An archived group's incoming arrival is silent while its unread metadata is observed");
        form.SetArchivedConversation(group.Id, false);
        form.ObserveConversationNotifications([foregroundArrival, group]);
        Require(flashes == 2, "Unarchive does not replay arrivals already observed in the archive");
        var store = form.ChatPreferences()!;
        var deadline = now.AddHours(8);
        store.SetMute(group.Id, deadline, now);
        now = now.AddMinutes(1); group = group with { UnreadCount = 4, LastMessageAt = now };
        form.ObserveConversationNotifications([foregroundArrival, group]);
        Require(flashes == 2, "A finite mute suppresses taskbar notifications before its deadline");
        now = deadline; group = group with { UnreadCount = 5, LastMessageAt = now };
        form.ObserveConversationNotifications([foregroundArrival, group]);
        Require(flashes == 3, "A new arrival exactly at the finite mute deadline can notify again");
        store.SetMute(group.Id, null, now);
        now = now.AddDays(30); group = group with { UnreadCount = 6, LastMessageAt = now };
        form.ObserveConversationNotifications([foregroundArrival, group]);
        Require(flashes == 3, "Unlimited mute has no arbitrary expiry and suppresses a much later arrival");
        store.ClearMute(group.Id); form.ObserveConversationNotifications([foregroundArrival, group]);
        Require(flashes == 3, "Explicit unmute does not replay an already observed muted arrival");
        form.ObserveConversationNotifications([foregroundArrival]);
        form.ObserveConversationNotifications([foregroundArrival, group]);
        Require(flashes == 3, "A temporarily missing conversation retains its tombstone and does not notify on historical reappearance");
        var restored = old with { Id = Guid.NewGuid(), UnreadCount = 5 };
        form.ObserveConversationNotifications([foregroundArrival, group, restored]);
        Require(flashes == 3, "Another first-seen historical conversation remains quiet rather than treating membership/list changes as messages");
        var justCreated = restored with { Id = Guid.NewGuid(), LastMessageAt = now.AddMinutes(1), UnreadCount = 1 };
        now = now.AddMinutes(2);
        form.ObserveConversationNotifications([foregroundArrival, group, restored, justCreated]);
        Require(flashes == 4, "A newly discovered conversation with an unread message newer than the preceding poll can notify once");
        form.ObserveConversationNotifications([foregroundArrival, group, restored, justCreated]);
        Require(flashes == 4, "The same new conversation arrival cannot repeatedly notify");
        var otherSession = form._session! with { User = form._session.User with { Id = Guid.NewGuid() } };
        form._session = otherSession;
        form.ObserveConversationNotifications([justCreated]);
        Require(flashes == 4 && form._chatNotificationOwner == otherSession.User.Id && form._chatNotificationWatermarks.Count == 1,
            "Account switching clears old watermarks and flashing, and builds a quiet account-specific baseline");
        now = now.AddMinutes(1); justCreated = justCreated with { UnreadCount = 2, LastMessageAt = now };
        form.ObserveConversationNotifications([justCreated]);
        Require(flashes == 5 && form.ShouldNotifyConversation(justCreated.Id, now),
            "The new account does not inherit the old account's archive or mute policy");
        var stopsBeforeDispose = stops;
        form.DisposeChatNotifications();
        Require(stops == stopsBeforeDispose + 1 && !form._chatNotificationsConfigured &&
            form._chatNotificationOwner is null && form._chatNotificationWatermarks.Count == 0,
            "Notification disposal stops flashing, detaches activation and clears all ephemeral account metadata");
        form.ObserveConversationNotifications([justCreated with { LastMessageAt = now.AddMinutes(1), UnreadCount = 3 }]);
        Require(flashes == 5, "Disposed notification routing cannot publish late arrival alerts");
        Require(!form.ChatPreferences()!.IsPersistent && fixture.Dialogs == 0,
            "Notification QA uses synthetic IDs and observers only: no real preference files, sound, toast or taskbar PInvoke");
        return checks;
    }
}
