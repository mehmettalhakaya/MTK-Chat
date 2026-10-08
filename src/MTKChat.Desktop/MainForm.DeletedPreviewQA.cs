using System.Security.Cryptography;
using System.Text;
using MTKChat.Contracts;
using MTKChat.Cryptography;

namespace MTKChat.Desktop;

internal sealed partial class MainForm
{
    // These fixtures use synthetic account keys, native controls and in-process
    // HTTP only. A tombstone is tested as metadata, never as decrypted content.
    internal static IReadOnlyList<string> VerifyDeletedConversationPreviews(string directory)
    {
        Directory.CreateDirectory(directory);
        using var context = new HistoryQaUiContext();
        var checks = new List<string>();
        void Require(bool condition, string check)
        {
            HistoryQaAssertUiThread();
            if (!condition) throw new InvalidOperationException("Deleted conversation preview QA: " + check);
            checks.Add(check);
        }
        static string Preview(RoundedPanel card) => card.Controls.Find("ConversationLastMessagePreview", true).OfType<Label>().Single().Text;
        static string Clock(RoundedPanel card) => card.Controls.Find("ConversationLastMessageTime", true).OfType<Label>().Single().Text;
        static void Summary(HistoryQaFixture fixture, ConversationSummary room)
        {
            fixture.Handler.Rooms[room.Id] = room;
            fixture.Card(room).Tag = room;
            if (fixture.Form._selectedConversation?.Id == room.Id) fixture.Form._selectedConversation = room;
            fixture.Form.ReconcileConversationPreviews(fixture.Form._conversationList.Controls.OfType<RoundedPanel>()
                .Select(card => (ConversationSummary)card.Tag!).ToArray());
        }
        static StoredMessage Message(HistoryQaFixture fixture, ConversationSummary room, string text, DateTimeOffset at)
        {
            var form = fixture.Form;
            var me = form._session!.User;
            var clientId = Guid.NewGuid();
            var bytes = Encoding.UTF8.GetBytes(text);
            try
            {
                var payload = MessageCryptography.Encrypt(bytes, clientId, room.Id, me.Id, me.Id, at,
                    form._identity.ExportEncryptionPublicKey(), form._identity.SigningKey);
                fixture.Handler.Keys[me.Id] = new(me.Id, Guid.NewGuid(), form._identity.ExportEncryptionPublicKey(),
                    form._identity.ExportSigningPublicKey());
                return new(Guid.NewGuid(), clientId, room.Id, me.Id, "text", at, null, false, [payload], null);
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }

        foreach (var kind in new[] { "direct", "group" })
        {
            using var fixture = new HistoryQaFixture();
            var form = fixture.Form;
            var at = DateTimeOffset.UtcNow.AddMinutes(-10);
            var room = fixture.InitialRoom with
            {
                Kind = kind, Title = kind == "group" ? "Silinen son mesaj · grup" : "Silinen son mesaj · kişi",
                LastMessageAt = at, LastActivityAt = at, ActivityMetadataAvailable = true,
                LastDeletedMessageAt = null, DeletedMessageMetadataAvailable = true
            };
            var older = Message(fixture, room, "Bir önceki mesaj silinen son mesajın yerini almamalı.", at.AddMinutes(-1));
            var latest = Message(fixture, room, "Herkesten silinen özel içerik geri dönmemeli.", at);
            Summary(fixture, room);
            fixture.Handler.Messages[room.Id] = [older, latest];
            HistoryQaPump(form.RefreshMessagesAsync(silent: true));
            Require(Preview(fixture.Card(room)) == "Herkesten silinen özel içerik geri dönmemeli.",
                kind + ": a genuine latest encrypted message initially supplies its authenticated preview");

            var deleted = latest with { DeletedForEveryone = true, Payloads = [], Attachment = null };
            fixture.Handler.Messages[room.Id] = [older, deleted];
            room = room with { LastMessageAt = older.CreatedAt, LastDeletedMessageAt = deleted.CreatedAt };
            Summary(fixture, room);
            Require(Preview(fixture.Card(room)) == "Bu mesaj silindi" && Clock(fixture.Card(room)) == ConversationTime(deleted.CreatedAt),
                kind + ": authoritative latest deletion metadata immediately replaces cached plaintext and preserves the clock");
            var keyCalls = fixture.Handler.KeyCalls.Values.Sum();
            HistoryQaPump(form.RefreshMessagesAsync(silent: true));
            Require(Preview(fixture.Card(room)) == "Bu mesaj silindi" && !Preview(fixture.Card(room)).Contains("Bir önceki", StringComparison.Ordinal),
                kind + ": committed history keeps the latest tombstone instead of falling back to older plaintext");
            var deletedRow = form._messageList.Controls.OfType<MessageRow>().Single(row => row.MessageId == deleted.Id);
            Require(!deletedRow.CanStar && !deletedRow.CanMarkRead && !deletedRow.CanPin &&
                fixture.Handler.KeyCalls.Values.Sum() == keyCalls &&
                !HistoryQaHasText(deletedRow, "Herkesten silinen özel içerik"),
                kind + ": a payload-free deletion row is not decrypted, starred, pinned or marked read");

            fixture.Handler.Messages[room.Id] = [deleted];
            room = room with { LastMessageAt = null };
            Summary(fixture, room);
            HistoryQaPump(form.RefreshMessagesAsync(silent: true));
            Require(Preview(fixture.Card(room)) == "Bu mesaj silindi" && Clock(fixture.Card(room)) == ConversationTime(at),
                kind + ": deleting the only message leaves the fixed deletion label with its original last activity time");
            form.PerformLayout(); Application.DoEvents(); form.Refresh(); Application.DoEvents();
            using (var screenshot = CaptureInfoClient(form))
                screenshot.Save(Path.Combine(directory, "conversation-deleted-latest-" + kind + ".png"));

            var newer = Message(fixture, room, "Daha yeni silinmemiş mesaj görünür.", at.AddMinutes(2));
            fixture.Handler.Messages[room.Id] = [older, deleted, newer];
            room = room with { LastMessageAt = newer.CreatedAt, LastActivityAt = newer.CreatedAt };
            Summary(fixture, room);
            HistoryQaPump(form.RefreshMessagesAsync(silent: true));
            Require(Preview(fixture.Card(room)) == "Daha yeni silinmemiş mesaj görünür." &&
                Clock(fixture.Card(room)) == ConversationTime(newer.CreatedAt),
                kind + ": a newer live message takes precedence over an older eligible deletion marker");

            var expired = deleted with
            {
                Id = Guid.NewGuid(), ClientMessageId = Guid.NewGuid(), CreatedAt = newer.CreatedAt.AddMinutes(1),
                ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1)
            };
            fixture.Handler.Messages[room.Id] = [older, deleted, newer, expired];
            HistoryQaPump(form.RefreshMessagesAsync(silent: true));
            Require(Preview(fixture.Card(room)) == "Daha yeni silinmemiş mesaj görünür.",
                kind + ": an expired newer tombstone cannot conceal an unexpired latest message");

            // Personal clear is different from everyone-deletion: the server
            // supplies no visible message and no visible tombstone afterwards.
            room = room with { LastMessageAt = null, LastDeletedMessageAt = null };
            fixture.Handler.Messages[room.Id] = [];
            Summary(fixture, room);
            HistoryQaPump(form.RefreshMessagesAsync(silent: true));
            Require(Preview(fixture.Card(room)) == "" && Clock(fixture.Card(room)) == ConversationTime(newer.CreatedAt),
                kind + ": personal clear leaves an empty preview while preserving only the separate activity clock");
            Require(fixture.Dialogs == 0 && !form.ChatPreferences()!.IsPersistent,
                kind + ": deletion previews stay non-modal and never persist private plaintext or real account preferences");
        }

        foreach (var kind in new[] { "direct", "group" })
        {
            using var fixture = new HistoryQaFixture();
            var form = fixture.Form;
            var at = DateTimeOffset.UtcNow.AddMinutes(-5);
            var room = fixture.AddRoom("Açılmadan silinen son mesaj · " + kind) with
            {
                Kind = kind, LastMessageAt = null, LastMessagePreview = "Şifreli mesaj",
                LastActivityAt = at, ActivityMetadataAvailable = true,
                LastDeletedMessageAt = at, DeletedMessageMetadataAvailable = true
            };
            // A local stale plaintext cache is deliberately hostile here. The
            // typed deletion metadata must win without fetching/decrypting it.
            form._conversationPreviews[room.Id] = new(at, null, "Eski önizleme geri dönmemeli.", null, DateTimeOffset.UtcNow.AddMinutes(1));
            Summary(fixture, room);
            fixture.Handler.FailedHistories.Add(room.Id);
            var selected = form._selectedConversation;
            var selectedRows = form._messageList.Controls.Cast<Control>().ToArray();
            form._premiumComposer.Text = "Gönderilmemiş taslak korunur 😊";
            var keyCalls = fixture.Handler.KeyCalls.Values.Sum();
            form.ScheduleConversationPreviews(); HistoryQaPump(form._previewRefreshTask);
            Require(Preview(fixture.Card(room)) == "Bu mesaj silindi" && Clock(fixture.Card(room)) == ConversationTime(at),
                kind + ": an unopened deletion-only conversation shows its fixed preview and clock from typed metadata");
            Require(fixture.Handler.HistoryCalls.GetValueOrDefault(room.Id) == 0 && fixture.Handler.KeyCalls.Values.Sum() == keyCalls,
                kind + ": a deletion-only summary performs no history/key GET and needs no remaining encryption envelope");
            Require(form._selectedConversation == selected && selectedRows.SequenceEqual(form._messageList.Controls.Cast<Control>()) &&
                form._premiumComposer.Text == "Gönderilmemiş taslak korunur 😊" && form._readAcknowledged.Count == 0,
                kind + ": background deletion preview does not select a chat, dispose history, erase a draft or acknowledge reading");

            room = room with { LastDeletedMessageAt = null };
            Summary(fixture, room);
            form.ScheduleConversationPreviews(); HistoryQaPump(form._previewRefreshTask);
            Require(Preview(fixture.Card(room)) == "" && Clock(fixture.Card(room)) == ConversationTime(at),
                kind + ": authoritative tombstone removal after private hiding/blocking removes the placeholder without erasing activity");
            Require(fixture.Handler.HistoryCalls.GetValueOrDefault(room.Id) == 0,
                kind + ": an empty authoritative preview does not revive a hidden deletion with an unnecessary history read");

            fixture.Card(room).Dispose();
            form.ReconcileConversationPreviews([fixture.InitialRoom]);
            Require(!form._conversationPreviews.ContainsKey(room.Id) && !form._previewRequests.ContainsKey(room.Id),
                kind + ": removing or leaving the conversation prunes its private deletion-preview cache and pending ownership");
        }

        using (var fixture = new HistoryQaFixture())
        {
            var form = fixture.Form;
            var at = DateTimeOffset.UtcNow.AddMinutes(-3);
            var room = fixture.AddRoom("Geç gelen silinmiş önizleme") with
            {
                Kind = "direct", LastMessageAt = at, LastActivityAt = at, ActivityMetadataAvailable = true,
                LastDeletedMessageAt = null, DeletedMessageMetadataAvailable = true
            };
            var latest = Message(fixture, room, "Geç gelen silinmiş özel metin.", at);
            fixture.Handler.Messages[room.Id] = [latest];
            Summary(fixture, room);
            form.ScheduleConversationPreviews(); HistoryQaPump(form._previewRefreshTask);
            Require(Preview(fixture.Card(room)) == "Geç gelen silinmiş özel metin.",
                "A valid background message can first populate the authenticated preview cache");
            form._conversationPreviews[room.Id] = form._conversationPreviews[room.Id] with { RefreshAfter = DateTimeOffset.MinValue };
            var beforeCalls = fixture.Handler.HistoryCalls.GetValueOrDefault(room.Id);
            var hold = fixture.Handler.Hold(room.Id, honorCancellation: false);
            form.ScheduleConversationPreviews();
            HistoryQaUntil(() => fixture.Handler.HistoryCalls.GetValueOrDefault(room.Id) > beforeCalls);
            var live = room;
            room = room with { LastDeletedMessageAt = at };
            Summary(fixture, room);
            Require(!ConversationVisualEquals(live, room) && Preview(fixture.Card(room)) == "Bu mesaj silindi",
                "A same-timestamp deletion-status change is visual metadata and immediately hides the old cached plaintext");
            hold.Response.SetResult(HistoryQaHandler.Json(new[] { latest }));
            HistoryQaPump(form._previewRefreshTask);
            Require(Preview(fixture.Card(room)) == "Bu mesaj silindi" && Clock(fixture.Card(room)) == ConversationTime(at) &&
                !form._conversationPreviews.Values.Any(preview => preview.Text == "Geç gelen silinmiş özel metin."),
                "A late pre-deletion GET cannot overwrite a same-timestamp deletion marker or retain its removed plaintext");
            room = room with { LastMessageAt = null, LastDeletedMessageAt = null };
            Summary(fixture, room);
            Require(Preview(fixture.Card(room)) == "", "Clearing the tombstone after a late GET cannot fall back to removed plaintext");
            Require(fixture.Dialogs == 0, "Late-deletion races remain non-modal");
        }

        using (var fixture = new HistoryQaFixture())
        {
            var form = fixture.Form;
            var at = DateTimeOffset.UtcNow.AddMinutes(-2);
            var room = fixture.InitialRoom with
            {
                LastMessageAt = at, LastActivityAt = at, ActivityMetadataAvailable = true,
                LastDeletedMessageAt = null, DeletedMessageMetadataAvailable = true
            };
            Summary(fixture, room);
            var latest = Message(fixture, room, "Özel temizlikten önce başlayan gecikmiş geçmiş.", at);
            var hold = fixture.Handler.Hold(room.Id, honorCancellation: false);
            var historyCalls = fixture.Handler.HistoryCalls.GetValueOrDefault(room.Id);
            var pending = form.RefreshMessagesAsync(silent: true);
            HistoryQaUntil(() => fixture.Handler.HistoryCalls.GetValueOrDefault(room.Id) > historyCalls);
            form._premiumComposer.Text = "Temizlik sırasında korunan taslak.";
            var cleared = room with { LastMessageAt = null, LastDeletedMessageAt = null };
            fixture.Handler.Rooms[room.Id] = cleared;
            // Run the real summary GET/update while the selected-room history
            // GET is held. Same room id and preserved activity are not enough
            // to make the previous visible-content metadata current again.
            HistoryQaPump(form.LoadConversationsAsync(silent: true));
            var rowsAfterSummary = form._messageList.Controls.Cast<Control>().ToArray();
            Require(form._selectedConversation is { LastMessageAt: null, LastDeletedMessageAt: null } &&
                form._selectedConversation.LastActivityAt == at && Preview(fixture.Card(cleared)) == "",
                "A real authoritative null/null summary clears selected content metadata without discarding its separate activity time");
            hold.Response.SetResult(HistoryQaHandler.Json(new[] { latest }));
            HistoryQaPump(pending);
            Require(rowsAfterSummary.SequenceEqual(form._messageList.Controls.Cast<Control>()) &&
                !HistoryQaHasText(form._messageList, "Özel temizlikten önce başlayan gecikmiş geçmiş.") &&
                Preview(fixture.Card(cleared)) == "" &&
                !form._conversationPreviews.Values.Any(preview => preview.Text == "Özel temizlikten önce başlayan gecikmiş geçmiş."),
                "A selected history GET started before authoritative personal clear cannot commit its previous rows or plaintext afterwards");
            Require(Clock(fixture.Card(cleared)) == ConversationTime(at) &&
                form._premiumComposer.Text == "Temizlik sırasında korunan taslak." && fixture.Dialogs == 0,
                "Discarding a stale selected-history response preserves its activity clock and unsent draft without a warning dialog");
        }

        using (var fixture = new HistoryQaFixture())
        {
            var form = fixture.Form;
            var latest = fixture.Message(fixture.InitialRoom, fixture.FirstSender, "Liste yanıtından önce silinen bilinen mesaj.");
            var room = fixture.InitialRoom with
            {
                LastMessageAt = latest.CreatedAt, LastActivityAt = latest.CreatedAt, ActivityMetadataAvailable = true,
                LastDeletedMessageAt = null, DeletedMessageMetadataAvailable = true
            };
            Summary(fixture, room);
            fixture.Handler.Messages[room.Id] = [latest];
            HistoryQaPump(form.RefreshMessagesAsync(silent: true));
            Require(Preview(fixture.Card(room)) == "Liste yanıtından önce silinen bilinen mesaj." &&
                form._conversationPreviews[room.Id].MessageId == latest.Id,
                "A canonical live summary learns the exact authenticated id of a message addressed by another sender");
            var keyCalls = fixture.Handler.KeyCalls.Values.Sum();
            fixture.Handler.Messages[room.Id] = [latest with { DeletedForEveryone = true, Payloads = [] }];
            // Poll history before the next list GET: the summary still describes
            // the same live timestamp and has no canonical deletion date yet.
            HistoryQaPump(form.RefreshMessagesAsync(silent: true));
            Require(room.LastDeletedMessageAt is null && room.LastMessageAt == latest.CreatedAt &&
                Preview(fixture.Card(room)) == "Bu mesaj silindi" && Clock(fixture.Card(room)) == ConversationTime(latest.CreatedAt),
                "A known addressed stripped tombstone immediately replaces plaintext while the canonical list still lags with a null deletion marker");
            Require(form._conversationPreviews[room.Id] is { Deleted: true, DeletedSummaryAt: null, DeletedMetadataAvailable: true } &&
                fixture.Handler.KeyCalls.Values.Sum() == keyCalls && fixture.Dialogs == 0,
                "The history-before-list deletion keeps its metadata stamp without attempting to decrypt a removed envelope");
            room = room with { LastMessageAt = null };
            Summary(fixture, room);
            Require(Preview(fixture.Card(room)) == "",
                "A later authoritative null/null summary suppresses the known local tombstone instead of reviving a privately hidden deletion");
        }

        using (var fixture = new HistoryQaFixture())
        {
            var form = fixture.Form;
            var at = DateTimeOffset.UtcNow.AddMinutes(-1);
            var room = fixture.AddRoom("Yerelde süresi dolan silinmiş mesaj") with
            {
                LastMessageAt = null, LastActivityAt = at, ActivityMetadataAvailable = true,
                LastDeletedMessageAt = at, DeletedMessageMetadataAvailable = true
            };
            Summary(fixture, room);
            var marker = new ConversationPreview(at, null, "Bu mesaj silindi", DateTimeOffset.UtcNow.AddMinutes(1),
                DateTimeOffset.UtcNow.AddMinutes(1), Guid.NewGuid(), true, at, true);
            form._conversationPreviews[room.Id] = marker;
            form.ApplyConversationPreviewLabel(room);
            Require(Preview(fixture.Card(room)) == "Bu mesaj silindi",
                "An unexpired known local tombstone agrees with the canonical date-only deletion summary");
            form._conversationPreviews[room.Id] = marker with { ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1) };
            form.ApplyConversationPreviewLabel(room);
            Require(Preview(fixture.Card(room)) == "" && form.DeletedPreviewAt(room) is null,
                "A known local tombstone expiry suppresses its stale canonical deletion marker before another summary poll");
            form.ScheduleConversationPreviews(); HistoryQaPump(form._previewRefreshTask);
            Require(Preview(fixture.Card(room)) == "" && fixture.Handler.HistoryCalls.GetValueOrDefault(room.Id) == 0,
                "A background preview pass cannot revive an expired deletion-only marker or start a pointless history GET");
            var expired = Message(fixture, room, "Süresi dolan silinmiş içerik geri dönmemeli.", at) with
            {
                DeletedForEveryone = true, Payloads = [], ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1)
            };
            form.UpdateConversationPreviewFromHistory(room.Id, [expired]);
            form.UpdateConversationPreviewFromHistory(room.Id, []);
            Require(Preview(fixture.Card(room)) == "" && form.DeletedPreviewAt(room) is null,
                "Expired tombstone metadata survives a later empty history so a stale summary cannot recreate its deletion label");
            room = room with { LastDeletedMessageAt = null, LastActivityAt = null };
            Summary(fixture, room);
            Require(Preview(fixture.Card(room)) == "" && Clock(fixture.Card(room)) == "" && fixture.Dialogs == 0,
                "Fresh authoritative expiry metadata clears both deletion marker and activity clock non-modally");
        }

        using (var fixture = new HistoryQaFixture())
        {
            var form = fixture.Form;
            var at = DateTimeOffset.UtcNow.AddMinutes(-1);
            var room = fixture.InitialRoom with
            {
                LastMessageAt = at, LastActivityAt = null, ActivityMetadataAvailable = false,
                LastDeletedMessageAt = null, DeletedMessageMetadataAvailable = false
            };
            var latest = Message(fixture, room, "Eski sunucuda önce bilinen mesaj.", at);
            Summary(fixture, room);
            fixture.Handler.Messages[room.Id] = [latest];
            HistoryQaPump(form.RefreshMessagesAsync(silent: true));
            fixture.Handler.Messages[room.Id] = [latest with { DeletedForEveryone = true, Payloads = [] }];
            HistoryQaPump(form.RefreshMessagesAsync(silent: true));
            Require(Preview(fixture.Card(room)) == "Bu mesaj silindi" && Clock(fixture.Card(room)) == ConversationTime(at),
                "A legacy server's known addressed tombstone still replaces its old plaintext with the fixed deletion label");
            var unknown = latest with
            {
                Id = Guid.NewGuid(), ClientMessageId = Guid.NewGuid(), SenderId = fixture.FirstSender.Id,
                CreatedAt = at.AddMinutes(1), DeletedForEveryone = true, Payloads = []
            };
            form.UpdateConversationPreviewFromHistory(room.Id, [unknown]);
            Require(Preview(fixture.Card(room)) == "" && form.ConversationActivityAt(room) == at,
                "A newer unknown stripped tombstone cannot disclose another audience's activity or manufacture a deletion preview");
            form.UpdateConversationPreviewFromHistory(room.Id, []);
            Require(Preview(fixture.Card(room)) == "", "Empty legacy visible history never manufactures a deletion placeholder");
        }
        checks.Add("Limits: deletion-only preview, native-control and local race probes only; no real account write, deployed VDS polling or physical multi-monitor rendering was claimed.");
        return checks;
    }
}
