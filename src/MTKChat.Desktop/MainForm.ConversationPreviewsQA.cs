using System.Security.Cryptography;
using System.Text;
using MTKChat.Contracts;
using MTKChat.Cryptography;

namespace MTKChat.Desktop;

internal sealed partial class MainForm
{
    internal static IReadOnlyList<string> VerifyConversationPreviews(string directory)
    {
        using var context = new HistoryQaUiContext();
        var checks = new List<string>();
        void Require(bool condition, string check)
        {
            HistoryQaAssertUiThread();
            if (!condition) throw new InvalidOperationException("Conversation preview QA: " + check);
            checks.Add(check);
        }
        static Label Preview(RoundedPanel card) => card.Controls.Find("ConversationLastMessagePreview", true).OfType<Label>().Single();
        static string Clock(RoundedPanel card) => card.Controls.Find("ConversationLastMessageTime", true).OfType<Label>().Single().Text;

        using (var fixture = new HistoryQaFixture())
        {
            var form = fixture.Form;
            var room = fixture.InitialRoom;
            var latest = fixture.Message(room, fixture.FirstSender, "Son yazılan mesaj 😊\r\nikinci satır\t devamı");
            fixture.Handler.Messages[room.Id] = [latest];
            var card = fixture.Card(room);
            Require(Preview(card).Text == "", "An uncached card never renders the server's encrypted placeholder");
            HistoryQaPump(form.RefreshMessagesAsync(silent: true));
            Require(Preview(card).Text == "Son yazılan mesaj 😊 ikinci satır devamı",
                "Selected history supplies authenticated real text as a single-line preview");
            Require(Clock(card) == ConversationTime(latest.CreatedAt), "The selected history clock follows its actual last decrypted message");
            Require(!card.IsDisposed && ReferenceEquals(card, fixture.Card(room)),
                "Updating decrypted text reuses the same conversation card and its popup owner");
            form.ReconcileConversationPreviews([room]);
            Require(Preview(card).Text == "Son yazılan mesaj 😊 ikinci satır devamı",
                "An unchanged older summary cannot erase a newer committed history preview");
            room = room with { LastMessageAt = latest.CreatedAt };
            card.Tag = room; fixture.Handler.Rooms[room.Id] = room;
            form.ReconcileConversationPreviews([room]);
            Require(Preview(card).Text == "Son yazılan mesaj 😊 ikinci satır devamı" &&
                form._conversationPreviews[room.Id].SummaryAt == room.LastMessageAt,
                "Summary catch-up preserves the authenticated line and advances its metadata stamp");

            var other = fixture.AddRoom("Arka plandaki birebir sohbet") with { Kind = "direct" };
            fixture.Card(other).Tag = other;
            var older = fixture.Message(other, fixture.FirstSender, "Eski mesaj");
            var newest = fixture.Message(other, fixture.SecondSender, "Merhaba! En son yazılan gerçek mesaj ✨");
            other = other with { LastMessageAt = newest.CreatedAt };
            fixture.Card(other).Tag = other; fixture.Handler.Rooms[other.Id] = other;
            fixture.Handler.Messages[other.Id] = [older, newest];
            form._premiumComposer.Text = "Taslak silinmemeli";
            var selected = form._selectedConversation;
            var rows = form._messageList.Controls.Cast<Control>().ToArray();
            form.ScheduleConversationPreviews(); HistoryQaPump(form._previewRefreshTask);
            Require(Preview(fixture.Card(other)).Text == "Merhaba! En son yazılan gerçek mesaj ✨",
                "An unopened direct conversation decrypts its latest text in the background");
            Require(fixture.Handler.PreviewAfter[other.Id] == newest.CreatedAt.AddTicks(-1),
                "The preview GET uses the existing timestamp filter instead of downloading all history");
            Require(form._selectedConversation == selected && rows.SequenceEqual(form._messageList.Controls.Cast<Control>()) &&
                form._premiumComposer.Text == "Taslak silinmemeli" && form._readAcknowledged.Count == 0,
                "Background preview cannot select a chat, rebuild history, erase drafts or mark it read");
            var calls = fixture.Handler.HistoryCalls.GetValueOrDefault(other.Id);
            form.ScheduleConversationPreviews(); HistoryQaPump(form._previewRefreshTask);
            Require(fixture.Handler.HistoryCalls.GetValueOrDefault(other.Id) == calls,
                "Repeated unchanged metadata uses the bounded in-memory preview cache");

            var advanced = fixture.Message(other, fixture.SecondSender, "Özetten daha yeni arka plan mesajı");
            fixture.Handler.Messages[other.Id] = [older, newest, advanced];
            form._conversationPreviews[other.Id] = form._conversationPreviews[other.Id] with { RefreshAfter = DateTimeOffset.MinValue };
            form.ScheduleConversationPreviews(); HistoryQaPump(form._previewRefreshTask);
            Require(Preview(fixture.Card(other)).Text == "Özetten daha yeni arka plan mesajı" &&
                form._conversationPreviews[other.Id].LastAt == advanced.CreatedAt &&
                form._conversationPreviews[other.Id].SummaryAt == newest.CreatedAt,
                "A preview response newer than its requested timestamp keeps the actual authenticated last-message date");
            Require(Clock(fixture.Card(other)) == ConversationTime(advanced.CreatedAt),
                "An unopened conversation clock follows newer history rather than the lagging summary");
            other = other with { LastMessageAt = advanced.CreatedAt };
            fixture.Card(other).Tag = other; fixture.Handler.Rooms[other.Id] = other;
            form.ReconcileConversationPreviews([room, other]);
            Require(Preview(fixture.Card(other)).Text == "Özetten daha yeni arka plan mesajı" &&
                form._conversationPreviews[other.Id].SummaryAt == advanced.CreatedAt,
                "Background summary catch-up does not erase the newer line or require another decrypt");

            var empty = fixture.AddRoom("Boş sohbet") with { LastMessageAt = null, LastMessagePreview = "Henüz mesaj yok" };
            fixture.Card(empty).Tag = empty;
            form.ReconcileConversationPreviews([room, other, empty]);
            form.ScheduleConversationPreviews(); HistoryQaPump(form._previewRefreshTask);
            Require(Preview(fixture.Card(empty)).Text == "" && !fixture.Handler.PreviewAfter.ContainsKey(empty.Id),
                "A genuinely empty chat has a blank preview without a history request");
            Require(Clock(fixture.Card(empty)) == "", "A genuinely empty conversation has no clock");

            Directory.CreateDirectory(directory);
            using (var image = new Bitmap(form.ClientSize.Width, form.ClientSize.Height))
            {
                form.DrawToBitmap(image, form.ClientRectangle);
                image.Save(Path.Combine(directory, "conversation-preview-local.png"));
            }

            // Simulate the next authoritative list after a private clear/block/expiry.
            var cleared = other with { LastMessageAt = null };
            fixture.Card(other).Tag = cleared;
            form.ReconcileConversationPreviews([room, cleared, empty]);
            Require(Preview(fixture.Card(other)).Text == "" && !form._conversationPreviews.ContainsKey(other.Id),
                "An empty authoritative summary immediately removes previously decrypted cached text");
            Require(Clock(fixture.Card(other)) == "", "Clearing the authoritative last-message summary also removes the clock");
            // A timestamp rollback is not an unchanged lagging summary: it removes
            // a newer deleted/blocked line even if an older message remains.
            var rolledBack = room with { LastMessageAt = room.LastMessageAt!.Value.AddDays(-1) };
            card.Tag = rolledBack;
            form.ReconcileConversationPreviews([rolledBack, cleared, empty]);
            Require(Preview(card).Text == "" && !form._conversationPreviews.ContainsKey(room.Id),
                "A changed summary rolling back to an older last message invalidates deleted/blocked preview content");
        }

        using (var fixture = new HistoryQaFixture())
        {
            var form = fixture.Form;
            var room = fixture.AddRoom("İmza doğrulaması");
            var message = fixture.Message(room, fixture.FirstSender, "Geçersiz imza görünmemeli");
            var payload = message.Payloads.Single() with { Signature = Convert.ToBase64String(new byte[64]) };
            fixture.Handler.Messages[room.Id] = [message with { Payloads = [payload] }];
            room = room with { LastMessageAt = message.CreatedAt };
            fixture.Card(room).Tag = room;
            form.ScheduleConversationPreviews(); HistoryQaPump(form._previewRefreshTask);
            Require(Preview(fixture.Card(room)).Text == "" && fixture.Dialogs == 0,
                "A forged signature produces neither plaintext nor a modal/decrypt-error preview");
            fixture.Handler.Messages[room.Id] = [message];
            form._conversationPreviews[room.Id] = form._conversationPreviews[room.Id] with { RefreshAfter = DateTimeOffset.MinValue };
            form.ScheduleConversationPreviews(); HistoryQaPump(form._previewRefreshTask);
            Require(Preview(fixture.Card(room)).Text == "Geçersiz imza görünmemeli",
                "A retryable blank preview recovers after valid ciphertext returns");

            fixture.Handler.FailedHistories.Add(room.Id);
            form._conversationPreviews[room.Id] = form._conversationPreviews[room.Id] with { RefreshAfter = DateTimeOffset.MinValue };
            form.ScheduleConversationPreviews(); HistoryQaPump(form._previewRefreshTask);
            Require(Preview(fixture.Card(room)).Text == "Geçersiz imza görünmemeli" && fixture.Dialogs == 0,
                "A temporary network failure preserves an unexpired previously authenticated preview without a dialog");
            fixture.Handler.FailedHistories.Clear();
            fixture.Handler.Messages[room.Id] = [message with { Payloads = [payload] }];
            form._conversationPreviews[room.Id] = form._conversationPreviews[room.Id] with { RefreshAfter = DateTimeOffset.MinValue };
            form.ScheduleConversationPreviews(); HistoryQaPump(form._previewRefreshTask);
            Require(Preview(fixture.Card(room)).Text == "", "A bad signature never retains plaintext from the previous refresh");
            fixture.Handler.Messages[room.Id] = [message];
            form._conversationPreviews[room.Id] = form._conversationPreviews[room.Id] with { RefreshAfter = DateTimeOffset.MinValue };
            form.ScheduleConversationPreviews(); HistoryQaPump(form._previewRefreshTask);

            var before = form._conversationPreviews[room.Id];
            form._conversationPreviews[room.Id] = before with { ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1) };
            form.ApplyConversationPreviewLabel(room);
            Require(Preview(fixture.Card(room)).Text == "", "A locally expired preview disappears even before the next metadata response");
            Require(Clock(fixture.Card(room)) == "", "A locally expired message cannot leave its clock visible");
            fixture.Handler.FailedHistories.Add(room.Id);
            form.ScheduleConversationPreviews(); HistoryQaPump(form._previewRefreshTask);
            Require(Preview(fixture.Card(room)).Text == "" && Clock(fixture.Card(room)) == "",
                "A failed optional refresh cannot resurrect a known expired message's preview or clock");
            fixture.Handler.FailedHistories.Clear();
            form._conversationPreviews[room.Id] = before;
            form._session = form._session! with { User = form._session.User with { Id = Guid.NewGuid() } };
            form.ApplyConversationPreviewLabel(room);
            Require(Preview(fixture.Card(room)).Text == "" && form._conversationPreviews.Count == 0,
                "Changing the account clears all local preview plaintext");
        }

        using (var fixture = new HistoryQaFixture())
        {
            var form = fixture.Form;
            var room = fixture.AddRoom("Geç gelen istek");
            var old = fixture.Message(room, fixture.FirstSender, "Eski yanıt");
            room = room with { LastMessageAt = old.CreatedAt };
            fixture.Card(room).Tag = room;
            var hold = fixture.Handler.Hold(room.Id, honorCancellation: false);
            form.ScheduleConversationPreviews();
            HistoryQaUntil(() => fixture.Handler.HistoryCalls.GetValueOrDefault(room.Id) > 0);
            // A newly committed authenticated history supersedes an earlier GET.
            form._previewRequests.Remove(room.Id);
            form._conversationPreviews[room.Id] = new(room.LastMessageAt, room.LastMessageAt, "Yeni yerel geçmiş", null, DateTimeOffset.UtcNow.AddMinutes(1));
            form.ApplyConversationPreviewLabel(room);
            hold.Response.SetResult(HistoryQaHandler.Json(new[] { old }));
            HistoryQaPump(form._previewRefreshTask);
            Require(Preview(fixture.Card(room)).Text == "Yeni yerel geçmiş",
                "A late background response cannot overwrite a newer committed local preview");
        }

        using (var fixture = new HistoryQaFixture())
        {
            var form = fixture.Form;
            var room = fixture.AddRoom("Çıkılmış grup");
            var message = fixture.Message(room, fixture.FirstSender, "Gruptan çıkınca görünmemeli");
            room = room with { LastMessageAt = message.CreatedAt };
            fixture.Card(room).Tag = room;
            var hold = fixture.Handler.Hold(room.Id, honorCancellation: false);
            form.ScheduleConversationPreviews();
            HistoryQaUntil(() => fixture.Handler.HistoryCalls.GetValueOrDefault(room.Id) > 0);
            fixture.Card(room).Dispose();
            form.ReconcileConversationPreviews([fixture.InitialRoom]);
            hold.Response.SetResult(HistoryQaHandler.Json(new[] { message }));
            HistoryQaPump(form._previewRefreshTask);
            Require(!form._conversationPreviews.ContainsKey(room.Id),
                "A late GET cannot restore preview text for a removed or left conversation");
        }

        foreach (var allRooms in new[] { false, true })
        {
            using var fixture = new HistoryQaFixture();
            var form = fixture.Form;
            var room = fixture.InitialRoom;
            var old = fixture.Message(room, fixture.FirstSender, "Silinmiş geçmiş geri dönmemeli");
            var hold = fixture.Handler.Hold(room.Id, honorCancellation: false);
            var pending = form.RefreshMessagesAsync(true);
            HistoryQaUntil(() => fixture.Handler.HistoryCalls.GetValueOrDefault(room.Id) > 0);
            var version = form._conversationVersion;
            form._premiumComposer.Text = "Korunan taslak";
            form.InvalidateConversationVisibility(allRooms ? null : room.Id);
            hold.Response.SetResult(HistoryQaHandler.Json(new[] { old }));
            HistoryQaPump(pending);
            Require(form._conversationVersion > version && hold.Cancellations > 0 &&
                !HistoryQaHasText(form._messageList, "Silinmiş geçmiş") && Preview(fixture.Card(room)).Text == "" &&
                form._premiumComposer.Text == "Korunan taslak",
                $"Visibility invalidation cancels/discards a late selected history without losing the draft (all rooms: {allRooms})");
        }

        using (var fixture = new HistoryQaFixture())
        {
            var form = fixture.Form;
            // Remove purely illustrative snapshot cards so the request order is
            // exactly these four authorized synthetic conversations.
            foreach (var card in form._conversationList.Controls.OfType<RoundedPanel>().ToArray())
                if (card.Tag is ConversationSummary r && r.Id != fixture.InitialRoom.Id) card.Dispose();
            var rooms = new List<ConversationSummary>();
            for (var index = 0; index < 4; index++)
            {
                var room = fixture.AddRoom("Oturum QA " + index);
                var message = fixture.Message(room, fixture.FirstSender, "401 ile açılmamalı");
                room = room with { LastMessageAt = message.CreatedAt };
                fixture.Card(room).Tag = room;
                fixture.Handler.Messages[room.Id] = [message];
                rooms.Add(room);
            }
            fixture.Handler.UnauthorizedKeys.Add(fixture.FirstSender.Id);
            form.ScheduleConversationPreviews(); HistoryQaPump(form._previewRefreshTask);
            Require(form._previewSessionExpired && rooms.Skip(2).All(r => !fixture.Handler.HistoryCalls.ContainsKey(r.Id)) &&
                rooms.All(r => Preview(fixture.Card(r)).Text == "") && fixture.Dialogs == 0,
                "A 401 key response stops the remaining bounded queue without showing a dialog or plaintext");
        }

        using (var fixture = new HistoryQaFixture())
        {
            var form = fixture.Form;
            var room = fixture.InitialRoom;
            var me = form._session!.User;
            var earlier = fixture.Message(room, fixture.FirstSender, "Silinen mesajdan önceki gerçek mesaj");
            var id = Guid.NewGuid(); var at = DateTimeOffset.UtcNow;
            var payload = MessageCryptography.Encrypt("Kendi son mesajım"u8, id, room.Id, me.Id, me.Id, at,
                form._identity.ExportEncryptionPublicKey(), form._identity.SigningKey);
            fixture.Handler.Keys[me.Id] = new(me.Id, Guid.NewGuid(), form._identity.ExportEncryptionPublicKey(), form._identity.ExportSigningPublicKey());
            var own = new StoredMessage(Guid.NewGuid(), id, room.Id, me.Id, "text", at, null, false, [payload], null);
            fixture.Handler.Messages[room.Id] = [earlier, own];
            HistoryQaPump(form.RefreshMessagesAsync(true));
            Require(Preview(fixture.Card(room)).Text == "Kendi son mesajım", "The sender's own encrypted last message is previewed too");
            fixture.Handler.Messages[room.Id] = [earlier, own with { DeletedForEveryone = true }];
            HistoryQaPump(form.RefreshMessagesAsync(true));
            Require(Preview(fixture.Card(room)).Text == "Silinen mesajdan önceki gerçek mesaj",
                "After the latest message is deleted, the preceding authenticated message is shown even before metadata catches up");
            Require(Clock(fixture.Card(room)) == ConversationTime(earlier.CreatedAt),
                "Deleting the latest message rolls the clock back with the preceding visible message");
            fixture.Handler.Messages[room.Id] = [own with { DeletedForEveryone = true }];
            HistoryQaPump(form.RefreshMessagesAsync(true));
            Require(Preview(fixture.Card(room)).Text == "", "Deleted-for-everyone content is never resurrected as a latest preview");
            Require(Clock(fixture.Card(room)) == "", "Deleting the only remaining visible message clears the clock despite stale metadata");
        }
        Require(PreviewLine("image/png", "") == "Fotoğraf" && PreviewLine("audio/wav", "") == "Sesli mesaj" &&
            PreviewLine("file", "▤  rapor.pdf\n123 KB\n↓ Kaydet") == "rapor.pdf",
            "Photo, voice and file previews use meaningful compact labels rather than encrypted placeholders");
        Require(PreviewLine("text", new string('x', 279) + "😊" + new string('x', 100)).Length <= 281 &&
            !char.IsHighSurrogate(PreviewLine("text", new string('x', 279) + "😊" + new string('x', 100))[^2]),
            "A bounded long preview never cuts a surrogate-pair emoji in half");
        return checks;
    }
}
