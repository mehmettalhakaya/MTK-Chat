using MTKChat.Contracts;

namespace MTKChat.Desktop;

internal sealed partial class MainForm
{
    internal static IReadOnlyList<string> VerifyArchiveMute(string directory)
    {
        using var context = new HistoryQaUiContext();
        using var fixture = new HistoryQaFixture();
        var form = fixture.Form;
        var checks = new List<string>();
        void Require(bool condition, string check)
        {
            if (!condition) throw new InvalidOperationException("Archive/mute QA: " + check);
            checks.Add(check);
        }
        var group = fixture.InitialRoom with { UnreadCount = 4 };
        fixture.Handler.Rooms[group.Id] = group;
        var direct = fixture.AddRoom("Sessiz kişisel sohbet") with { Kind = "direct", UnreadCount = 2 };
        fixture.Handler.Rooms[direct.Id] = direct;
        fixture.Handler.Messages[group.Id] = [fixture.Message(group, fixture.FirstSender, "Arşiv mesajları saklar, silmez.")];
        fixture.Handler.Messages[direct.Id] = [];
        HistoryQaPump(form.LoadConversationsAsync(group.Id));
        var selected = form._selectedConversation;
        var selectedCard = form._selectedConversationCard;
        var messages = form._messageList.Controls.Cast<Control>().ToArray();
        form._premiumComposer.Text = "Arşiv ve sessize alma sırasında korunacak taslak 😊";
        var groupMenu = fixture.Card(group).ContextMenuStrip!;
        ToolStripItem Item(ContextMenuStrip menu, string title) => menu.Items.Cast<ToolStripItem>().Single(item => item.Text == title);
        Item(groupMenu, "Sohbeti arşivle").PerformClick();
        Require(form.IsArchivedConversation(group.Id) && !fixture.Card(group).Visible,
            "The real group right-click action archives only its local sidebar card");
        Require(ReferenceEquals(selected, form._selectedConversation) && ReferenceEquals(selectedCard, form._selectedConversationCard) &&
            messages.SequenceEqual(form._messageList.Controls.Cast<Control>()) && form._premiumComposer.Text.Contains("korunacak", StringComparison.Ordinal),
            "Archiving never changes the selected chat, live message controls or unsent draft");
        Require(fixture.Handler.Rooms[group.Id].Participants.SequenceEqual(group.Participants) &&
            fixture.Handler.Messages[group.Id].Length == 1 && !form.ShouldNotifyConversation(group.Id),
            "Personal archive leaves server membership/history intact and suppresses notification alerts");
        form._filterButtons["archived"].PerformClick(); Application.DoEvents();
        Require(fixture.Card(group).Visible && !fixture.Card(direct).Visible,
            "Archive navigation shows the archived group and hides the unarchived direct conversation");
        Require(form._filterButtons["archived"].AccessibleDescription?.Contains("1 arşivlenmiş", StringComparison.Ordinal) == true,
            "Archive navigation exposes its accurate personal room count accessibly");
        var incoming = group with { UnreadCount = 7, LastMessageAt = DateTimeOffset.UtcNow.AddMinutes(1) };
        form.ReconcileConversationCards([incoming, direct]);
        Require(form.IsArchivedConversation(group.Id) && fixture.Card(incoming).Visible &&
            form._filterButtons["archived"].AccessibleDescription?.Contains("7 okunmamış", StringComparison.Ordinal) == true,
            "Incoming summary reconciliation retains archive and updates its unread count without auto-unarchiving");
        var refreshedMenu = fixture.Card(incoming).ContextMenuStrip!;
        refreshedMenu.Show(fixture.Card(incoming), new Point(4, 4)); Application.DoEvents();
        Require(Item(refreshedMenu, "Arşivden çıkar").Enabled, "Reopening an archived card's actual menu offers Unarchive");
        refreshedMenu.Close(); Item(refreshedMenu, "Arşivden çıkar").PerformClick();
        Require(!form.IsArchivedConversation(group.Id) && !fixture.Card(incoming).Visible,
            "Unarchive moves the group out of the archive view without any message deletion");
        form._filterButtons["all"].PerformClick(); Application.DoEvents();
        Require(fixture.Card(incoming).Visible && fixture.Card(direct).Visible,
            "Returning to All restores the existing archived group as well as the direct conversation");
        foreach (var room in new[] { incoming, direct })
        {
            var card = fixture.Card(room);
            var menu = card.ContextMenuStrip!;
            menu.Show(card, new Point(4, 4)); Application.DoEvents(); menu.Close();
            var mute = (ToolStripMenuItem)Item(menu, "Bildirimleri sessize al");
            Require(mute.DropDownItems.Cast<ToolStripItem>().Where(item => item is not ToolStripSeparator).Select(item => item.Text)
                .SequenceEqual(new[] { "8 saat", "1 hafta", "Süresiz", "Sessize almayı kaldır" }),
                $"{room.Kind}: actual themed mute submenu has finite, unlimited and explicit unmute actions");
            foreach (var duration in new[] { "8 saat", "1 hafta", "Süresiz" })
            {
                var before = DateTimeOffset.UtcNow;
                mute.DropDownItems.Cast<ToolStripItem>().Single(item => item.Text == duration).PerformClick();
                var after = DateTimeOffset.UtcNow;
                var policy = form.ChatPreferences()!;
                Require(policy.IsMuted(room.Id) && !form.ShouldNotifyConversation(room.Id),
                    $"{room.Kind}/{duration}: real menu applies a personal mute that gates notification alerts");
                if (duration == "Süresiz")
                    Require(policy.MuteUntil(room.Id) is null && policy.IsMuted(room.Id, DateTimeOffset.MaxValue),
                        $"{room.Kind}: unlimited mute has no expiry sentinel or accidental timeout");
                else
                {
                    var expected = duration == "8 saat" ? TimeSpan.FromHours(8) : TimeSpan.FromDays(7);
                    Require(policy.MuteUntil(room.Id) >= before + expected && policy.MuteUntil(room.Id) <= after + expected,
                        $"{room.Kind}/{duration}: finite mute stores the exact intended deadline");
                    var until = policy.MuteUntil(room.Id)!.Value;
                    Require(form.ShouldNotifyConversation(room.Id, until), $"{room.Kind}/{duration}: notification policy reopens at the finite deadline");
                }
                var indicator = card.Controls.Find("ConversationMuteIndicator", false).Single();
                Require(indicator.Visible && card.ClientRectangle.Contains(indicator.Bounds) &&
                    card.Controls.Find("ConversationUnreadBadge", false).Single().Visible,
                    $"{room.Kind}/{duration}: mute vector indicator fits the card while preserving its unread badge");
                menu.Show(card, new Point(4, 4)); Application.DoEvents(); menu.Close();
                Require(Item(menu, "Sessize alma süresini değiştir") == mute,
                    $"{room.Kind}/{duration}: opening a muted conversation updates its menu status");
                mute.DropDownItems.Cast<ToolStripItem>().Single(item => item.Text == "Sessize almayı kaldır").PerformClick();
                Require(!policy.IsMuted(room.Id) && form.ShouldNotifyConversation(room.Id) && !indicator.Visible,
                    $"{room.Kind}/{duration}: actual Unmute restores alerts and removes the icon immediately");
            }
        }
        var now = DateTimeOffset.UtcNow;
        form.ChatPreferences()!.SetMute(direct.Id, now.AddTicks(1), now);
        form.RefreshPersonalConversationIndicators();
        Require(!fixture.Card(direct).Controls.Find("ConversationMuteIndicator", false).Single().Visible,
            "Expired mute icon is removed on the existing refresh path without rebuilding message rows");
        form.SetArchivedConversation(direct.Id, true);
        form._filterButtons["archived"].PerformClick();
        Require(fixture.Card(direct).Visible && !fixture.Card(incoming).Visible,
            "Direct chats use the same personal archive and navigation policy as groups");
        Require(form._premiumComposer.Text.Contains("korunacak", StringComparison.Ordinal) && !form.ChatPreferences()!.IsPersistent && fixture.Dialogs == 0,
            "All normal archive/mute transitions are non-modal, keep the draft and use no real account file");
        Directory.CreateDirectory(directory);
        foreach (var width in new[] { 1120, 1536 })
        {
            form.Size = new Size(width, 860); form.PerformLayout(); Application.DoEvents();
            foreach (var button in form._filterButtons.Values)
                Require(button.Parent!.ClientRectangle.Contains(button.Bounds), $"Archive filter wraps without clipping at {width}px");
            using var screenshot = CaptureInfoClient(form);
            screenshot.Save(Path.Combine(directory, $"archived-conversations-{width}.png"));
        }
        form.SetArchivedConversation(direct.Id, false);
        form._filterButtons["all"].PerformClick();
        form.SetConversationMute(direct.Id, null); form.SetConversationMute(incoming.Id, TimeSpan.FromDays(7));
        using (var screenshot = CaptureInfoClient(form)) screenshot.Save(Path.Combine(directory, "muted-conversations.png"));
        // A preferences failure cannot pretend to save a local archive/mute.
        form.ApplyLoadedChatPreferences(form._session!.User.Id, form.ChatPreferences()!, unavailable: true);
        try { form.SetArchivedConversation(direct.Id, true); throw new InvalidOperationException("Unavailable preference file was writable."); }
        catch (InvalidOperationException exception) when (exception.Message.StartsWith("Kişisel tercihler", StringComparison.Ordinal))
        { checks.Add("Unreadable preferences reject archive edits and preserve the protected file"); }
        Require(!form.IsArchivedConversation(direct.Id), "An archive persistence failure does not publish an unsaved archive state");
        return checks;
    }
}
