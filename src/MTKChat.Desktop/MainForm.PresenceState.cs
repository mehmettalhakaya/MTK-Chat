using MTKChat.Contracts;

namespace MTKChat.Desktop;

internal sealed partial class MainForm
{
    private sealed record PendingPresence(Guid ConversationId, int ConversationVersion, PresenceView[] People);
    private readonly HashSet<ContextMenuStrip> _presenceMenus = new();
    private PendingPresence? _pendingPresence;
    private Guid? _renderedPresenceConversation;
    private int _renderedPresenceVersion = -1;
    private PresenceView[] _renderedPresencePeople = [];
    private bool _presenceApplyQueued;
    private bool _presenceContinuityProbe;

    private bool DeferPresenceRender(IReadOnlyList<PresenceView> people)
    {
        if (_selectedConversation is null || IsDisposed || Disposing)
        {
            _pendingPresence = null;
            return true;
        }
        var conversation = _selectedConversation.Id;
        if (_renderedPresenceConversation != conversation || _renderedPresenceVersion != _conversationVersion)
        {
            // A menu's commands belong to the room in which it was created. Never
            // carry that menu, or its queued people snapshot, into another selection.
            _pendingPresence = null;
            foreach (var menu in _presenceMenus.Where(menu => !menu.IsDisposed && menu.Visible).ToArray()) menu.Close();
        }
        else if (_presenceMenus.Any(menu => !menu.IsDisposed && menu.Visible))
        {
            _pendingPresence = new PendingPresence(conversation, _conversationVersion, people.ToArray());
            return true;
        }
        _pendingPresence = null;
        return false;
    }

    private void RememberRenderedPresence(IReadOnlyList<PresenceView> people)
    {
        _renderedPresenceConversation = _selectedConversation?.Id;
        _renderedPresenceVersion = _conversationVersion;
        _renderedPresencePeople = people.ToArray();
    }

    private void TrackPresenceMenu(ContextMenuStrip? menu)
    {
        if (menu is null || !_presenceMenus.Add(menu)) return;
        menu.Closed += (_, _) => QueuePendingPresence();
        menu.Disposed += (_, _) => { _presenceMenus.Remove(menu); QueuePendingPresence(); };
    }

    private void QueuePendingPresence()
    {
        if (_presenceApplyQueued || _pendingPresence is null || IsDisposed || Disposing || !IsHandleCreated) return;
        _presenceApplyQueued = true;
        // Closed can run while a row/menu is being disposed. Wait until the current
        // layout/message finishes before touching the row collection again.
        BeginInvoke((Action)(() =>
        {
            _presenceApplyQueued = false;
            if (IsDisposed || Disposing || _pendingPresence is not { } pending) return;
            if (_presenceMenus.Any(menu => !menu.IsDisposed && menu.Visible)) return;
            _pendingPresence = null;
            if (_selectedConversation?.Id != pending.ConversationId || _conversationVersion != pending.ConversationVersion) return;
            RenderPresence(pending.People);
        }));
    }

    private static void AddPresenceProbeMenu(Control row)
    {
        // Snapshot-only menus intentionally omit real block/role API handlers. Their
        // visible/Closed/disposal lifecycle is the same as the production row popup.
        var menu = Theme.ContextMenu();
        menu.Items.Add("Kullanıcı seçenekleri");
        row.ContextMenuStrip = menu;
        row.Disposed += (_, _) => menu.Dispose();
    }

    internal IReadOnlyList<string> VerifyPresenceContinuity()
    {
        if (!IsHandleCreated || _selectedConversation is null || _session is null)
            throw new InvalidOperationException("Presence QA requires a populated, shown snapshot form.");
        var oldConversation = _selectedConversation;
        var oldVersion = _conversationVersion;
        var oldPeople = _renderedPresencePeople;
        var oldParticipantsShown = _participantsShown;
        var oldProbe = _presenceContinuityProbe;
        var checks = new List<string>();
        void Require(bool condition, string check)
        {
            if (!condition) throw new InvalidOperationException("Presence continuity regression: " + check);
            checks.Add(check);
        }
        try
        {
            _presenceContinuityProbe = true;
            _participantsShown = true; UpdatePresenceVisibility(); PerformLayout(); Application.DoEvents();
            var people = Enumerable.Range(0, 32).Select(i => new PresenceView(
                new ChatUser(Guid.NewGuid(), $"Katılımcı {i:00}", $"participant{i}@example.invalid", false, null),
                false, true, false, DateTimeOffset.UtcNow.AddHours(-2))).ToArray();
            RenderPresence(people); Application.DoEvents(); _presenceList.PerformLayout();
            var maximum = Math.Max(0, _presenceList.VerticalScroll.Maximum - _presenceList.VerticalScroll.LargeChange + 1);
            Require(_presenceList.VerticalScroll.Visible && maximum > 0, "Synthetic participants overflow the real native list");
            _presenceList.AutoScrollPosition = new Point(0, Math.Min(260, maximum));
            var offset = -_presenceList.AutoScrollPosition.Y;
            Require(offset > 0, "Participant list can scroll away from the top");
            RenderPresence(people.Select(person => person with { LastSeenAt = person.LastSeenAt?.AddMinutes(1) }).ToArray());
            Require(Math.Abs(-_presenceList.AutoScrollPosition.Y - offset) <= 1, "Presence row refresh preserves the existing scroll offset");
            var previousRows = _presenceList.Controls.Cast<Control>().ToArray();
            var menu = _presenceList.Controls.OfType<RoundedPanel>().First().ContextMenuStrip!;
            menu.Show(_presenceList, new Point(12, 12)); Application.DoEvents();
            Require(menu.Visible, "A real participant popup is visible");
            var intermediate = people.Select(person => person with { User = person.User with { DisplayName = "Ara " + person.User.DisplayName } }).ToArray();
            var latest = people.Select(person => person with { User = person.User with { DisplayName = "Son " + person.User.DisplayName } }).ToArray();
            RenderPresence(intermediate); RenderPresence(latest);
            Require(menu.Visible && previousRows.All(row => !row.IsDisposed && _presenceList.Controls.Contains(row)),
                "Presence polling leaves the open popup and its rows alive");
            Require(_pendingPresence?.People[0].User.DisplayName == latest[0].User.DisplayName,
                "Only the most recent people snapshot remains queued");
            menu.Close(); Application.DoEvents();
            Require(previousRows.All(row => row.IsDisposed) && _renderedPresencePeople[0].User.DisplayName == latest[0].User.DisplayName,
                "Closing the popup applies the latest queued people snapshot");
            Require(Math.Abs(-_presenceList.AutoScrollPosition.Y - offset) <= 1, "Deferred presence rendering also preserves scroll");
            RenderPresence(people.Take(2).ToArray());
            Require(_presenceList.AutoScrollPosition.Y == 0, "A shorter participant list clamps the old scroll offset");
            RenderPresence(people); Application.DoEvents();
            var rowsBeforeSwitch = _presenceList.Controls.Cast<Control>().ToArray();
            var oldMenu = _presenceList.Controls.OfType<RoundedPanel>().First().ContextMenuStrip!;
            oldMenu.Show(_presenceList, new Point(12, 12)); Application.DoEvents();
            RenderPresence(latest);
            _selectedConversation = oldConversation with { Id = Guid.NewGuid() }; _conversationVersion++;
            oldMenu.Close(); Application.DoEvents();
            Require(_pendingPresence is null && rowsBeforeSwitch.All(row => !row.IsDisposed),
                "A queued snapshot from an old conversation is dropped after selection changes");
            RenderPresence(people.Take(2).ToArray());
            Require(rowsBeforeSwitch.All(row => row.IsDisposed) && _renderedPresenceConversation == _selectedConversation.Id,
                "The new conversation renders only its own supplied people");
            return checks;
        }
        finally
        {
            _pendingPresence = null;
            foreach (var menu in _presenceMenus.Where(menu => !menu.IsDisposed && menu.Visible).ToArray()) menu.Close();
            _selectedConversation = oldConversation; _conversationVersion = oldVersion;
            _presenceContinuityProbe = oldProbe;
            RenderPresence(oldPeople);
            _participantsShown = oldParticipantsShown; UpdatePresenceVisibility();
        }
    }
}
