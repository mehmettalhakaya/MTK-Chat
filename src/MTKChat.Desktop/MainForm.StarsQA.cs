using MTKChat.Contracts;

namespace MTKChat.Desktop;

internal sealed partial class MainForm
{
    internal static IReadOnlyList<string> VerifyStarredMessages(string directory)
    {
        using var context = new HistoryQaUiContext();
        using var fixture = new HistoryQaFixture();
        var checks = new List<string>();
        void Require(bool condition, string check)
        {
            if (!condition) throw new InvalidOperationException("Starred message QA: " + check);
            checks.Add(check);
        }
        var form = fixture.Form;
        var room = fixture.InitialRoom;
        var first = fixture.Message(room, fixture.FirstSender, "Yıldızlanacak doğrulanmış mesaj 😊");
        var second = fixture.Message(room, fixture.SecondSender, "Başka bir mesaj");
        fixture.Handler.Messages[room.Id] = [first, second];
        HistoryQaPump(form.RefreshMessagesAsync(false));
        var row = form._messageList.Controls.OfType<MessageRow>().Single(r => r.MessageId == first.Id);
        Require(row.CanStar && !row.IsStarred, "A successfully authenticated/decrypted message starts unstarred");
        var menu = row.Controls.OfType<RoundedPanel>().Single().ContextMenuStrip!;
        var item = menu.Items.Cast<ToolStripItem>().Single(i => i.Text == "Yıldızla");
        item.PerformClick();
        Require(row.IsStarred && form.ChatPreferences()!.IsStarred(room.Id, first.Id),
            "The real message menu stars the selected row with a personal room/message ID bookmark");
        Require(form.CurrentStarredMessages().Single().Preview.Contains("doğrulanmış", StringComparison.Ordinal),
            "Starred view obtains its preview only from currently decrypted history");
        foreach (var width in new[] { 340, 650, 1100 })
        {
            row.Width = width; row.VerifyLayout();
            Require(row.IsStarred, $"Star/time/read-receipt metadata does not overlap at {width}px");
        }
        form.ResizeBubbles();
        foreach (var kind in new[] { "image/png", "image/jpeg", "audio/wav" })
        {
            using var media = new MessageRow(fixture.FirstSender, false, first with { Kind = kind }, "", null);
            Require(media.StarredPreview == (kind == "audio/wav" ? "Sesli mesaj" : "Fotoğraf"),
                $"Real production MIME kind {kind} has a meaningful starred preview");
        }
        using (var mine = new MessageRow(form._session!.User, true, first with { SenderId = form._session.User.Id }, "Merhaba 😊", null))
        {
            foreach (var width in new[] { 340, 650, 1100 })
            {
                mine.Width = width; mine.SetStarred(true); mine.VerifyLayout();
                Require(mine.IsStarred, $"Outgoing star/time/double-check metadata is non-overlapping at {width}px");
            }
        }
        HistoryQaPump(form.RefreshMessagesAsync(false));
        Require(ReferenceEquals(row, form._messageList.Controls.OfType<MessageRow>().Single(r => r.MessageId == first.Id)) && row.IsStarred,
            "An unchanged history poll preserves the starred row without rebuilding it");
        var other = fixture.AddRoom("Diğer grup");
        fixture.Handler.Messages[other.Id] = [];
        HistoryQaPump(form.SelectConversationAsync(other, fixture.Card(other)));
        Require(form.CurrentStarredMessages().Count == 0, "Another chat cannot display this chat's starred message");
        HistoryQaPump(form.SelectConversationAsync(room, fixture.Card(room)));
        row = form._messageList.Controls.OfType<MessageRow>().Single(r => r.MessageId == first.Id);
        Require(row.IsStarred && form.CurrentStarredMessages().Count == 1, "Reopening the chat restores the star on its newly verified row");
        form.ToggleMessageStar(row);
        Require(!row.IsStarred && form.CurrentStarredMessages().Count == 0, "Unstarring immediately removes the personal bookmark and visible indicator");
        form.ToggleMessageStar(row);
        using (var dialog = form.StarredSnapshotForm())
        {
            dialog.Show(form); Application.DoEvents();
            Require(HistoryQaControls(dialog).Any(c => c.Text.Contains("Yıldızlanacak doğrulanmış", StringComparison.Ordinal)),
                "The themed starred-message view renders the real authorized preview");
            Require(HistoryQaControls(dialog).OfType<ModernButton>().Any(b => b.Text == "Sohbette göster  →"),
                "A starred item offers navigation back to its original message");
            Directory.CreateDirectory(directory);
            using var bitmap = new Bitmap(dialog.Width, dialog.Height);
            dialog.DrawToBitmap(bitmap, dialog.ClientRectangle);
            bitmap.Save(Path.Combine(directory, "starred-messages.png"));
            var visible = form.CurrentStarredMessages();
            Require(dialog.MessageCountForQa == 1 && !dialog.UpdateMessages(visible),
                "An unchanged authorized starred set does not rebuild the open view");
            Require(dialog.UpdateMessages([]) && dialog.MessageCountForQa == 0 &&
                !HistoryQaControls(dialog).Any(c => c.Text.Contains("Yıldızlanacak doğrulanmış", StringComparison.Ordinal)),
                "An already-open starred view removes its old plaintext controls when the authorized set is cleared");
            Require(HistoryQaControls(dialog).Any(c => c.Text == "Henüz yıldızlı mesaj yok"),
                "A live-cleared starred view renders its themed empty state");
            Require(dialog.UpdateMessages(visible) && dialog.MessageCountForQa == 1,
                "The open starred view can render a refreshed authorized set without reopening");
            dialog.Hide();
        }
        fixture.Handler.Messages[room.Id] = [first with { DeletedForEveryone = true }, second];
        HistoryQaPump(form.RefreshMessagesAsync(false));
        row = form._messageList.Controls.OfType<MessageRow>().Single(r => r.MessageId == first.Id);
        Require(!row.CanStar && !row.IsStarred && form.CurrentStarredMessages().Count == 0,
            "A deleted message is neither starred visually nor resurrected as a saved plaintext preview");
        fixture.Handler.Messages[room.Id] = [second];
        HistoryQaPump(form.RefreshMessagesAsync(false));
        Require(form.CurrentStarredMessages().Count == 0,
            "Expired/hidden/removed history entries are absent even if their ID bookmark remains");
        var corrupt = first with { Payloads = [first.Payloads[0] with { Signature = Convert.ToBase64String(new byte[64]) }] };
        fixture.Handler.Messages[room.Id] = [corrupt, second];
        HistoryQaPump(form.RefreshMessagesAsync(false));
        row = form._messageList.Controls.OfType<MessageRow>().Single(r => r.MessageId == first.Id);
        Require(!row.CanStar && form.CurrentStarredMessages().Count == 0,
            "Invalid signatures cannot put unverified or formerly decrypted text into the starred view");
        var longHistory = Enumerable.Range(0, 40).Select(index => fixture.Message(room, fixture.FirstSender,
            $"Uzun geçmiş mesajı {index:00}. " + string.Join(" ", Enumerable.Repeat("Yıldızlı mesaja dönüldüğünde doğru içerik görünür.", 4)))).ToArray();
        fixture.Handler.Messages[room.Id] = longHistory;
        HistoryQaPump(form.RefreshMessagesAsync(false));
        var target = form._messageList.Controls.OfType<MessageRow>().Single(r => r.MessageId == longHistory[15].Id);
        form._messageList.ScrollToOffset(form._messageList.MaximumOffset);
        Application.DoEvents();
        Require(form._messageList.Offset > 0 && target.Bottom < 0, "Navigation fixture starts at a genuinely scrolled long history with its target offscreen");
        form.ScrollToStarredMessage(target.MessageId);
        Application.DoEvents();
        Require(target.Top >= 0 && target.Top < form._messageList.ClientSize.Height && target.IsContentVisible(form._messageList.ClientRectangle),
            "Show-in-chat navigation converts scrolled child coordinates and actually reveals the original message");
        var account = form._session!;
        var originalPreferences = form.ChatPreferences()!;
        Require(form.ApplyLoadedChatPreferences(account.User.Id, originalPreferences, unavailable: true),
            "A failed preference load is attached only to its authenticated account");
        try
        {
            form.EnsurePreferencesWritable();
            throw new InvalidOperationException("An unavailable preference file unexpectedly became writable.");
        }
        catch (InvalidOperationException exception) when (exception.Message.StartsWith("Kişisel tercihler", StringComparison.Ordinal))
        { checks.Add("Unavailable account preferences reject edits without overwriting the protected file"); }
        form._session = account with { User = account.User with { Id = Guid.NewGuid() } };
        form.EnsurePreferencesWritable();
        Require(!form.ChatPreferences()!.IsStarred(room.Id, first.Id), "Changing authenticated account creates an isolated preference store");
        Require(!form._preferencesUnavailable, "A new account does not inherit the previous account's unreadable-file lockout");
        var newPreferences = form.ChatPreferences()!;
        Require(!form.ApplyLoadedChatPreferences(account.User.Id, originalPreferences, unavailable: true) &&
            ReferenceEquals(form.ChatPreferences(), newPreferences) && !form._preferencesUnavailable,
            "A delayed load or failure from the previous account cannot publish bookmarks or disable writes for the current account");
        Require(!form.ApplyLoadedChatPreferences(form._session.User.Id, originalPreferences, unavailable: false),
            "Loaded bookmark data with a mismatched owner is rejected even when the requested account is current");
        var currentAccountId = form._session.User.Id;
        Require(form.ApplyLoadedChatPreferences(currentAccountId, newPreferences, unavailable: true) &&
            form.ApplyLoadedChatPreferences(currentAccountId, newPreferences, unavailable: false) && !form._preferencesUnavailable,
            "A successful retry clears only the current account's temporary unavailable state");
        Require(!form.ChatPreferences()!.IsPersistent && fixture.Dialogs == 0,
            "Synthetic GUI QA writes no real account file and all normal star actions remain non-modal");
        return checks;
    }
}
