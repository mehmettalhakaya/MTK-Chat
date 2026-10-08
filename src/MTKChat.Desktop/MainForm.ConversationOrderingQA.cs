using MTKChat.Contracts;
using MTKChat.Cryptography;

namespace MTKChat.Desktop;

internal sealed partial class MainForm
{
    // Only synthetic account identities and in-process HTTP replies are used.
    // This verifies sidebar clocks/order independently of decrypted preview text;
    // no server write, real preference file, clipboard or audio device is used.
    internal static IReadOnlyList<string> VerifyConversationOrdering(string directory)
    {
        Directory.CreateDirectory(directory);
        using var context = new HistoryQaUiContext();
        var checks = new List<string>();
        void Require(bool condition, string check)
        {
            HistoryQaAssertUiThread();
            if (!condition) throw new InvalidOperationException("Conversation ordering QA: " + check);
            checks.Add(check);
        }
        static RoundedPanel[] Cards(MainForm form) => form._conversationList.Controls.OfType<RoundedPanel>().ToArray();
        static Guid[] Order(MainForm form, bool visibleOnly = false) => Cards(form)
            .Where(card => !visibleOnly || card.Visible).Select(card => ((ConversationSummary)card.Tag!).Id).ToArray();
        static string Clock(RoundedPanel card) => card.Controls.Find("ConversationLastMessageTime", true).OfType<Label>().Single().Text;
        static string Preview(RoundedPanel card) => card.Controls.Find("ConversationLastMessagePreview", true).OfType<Label>().Single().Text;
        static void Apply(HistoryQaFixture fixture, params ConversationSummary[] rooms)
        {
            fixture.Handler.Rooms.Clear();
            foreach (var room in rooms) fixture.Handler.Rooms[room.Id] = room;
            fixture.Form.ReconcileConversationPreviews(rooms);
            fixture.Form.ReconcileConversationCards(rooms);
            fixture.Form.ApplyConversationOrder();
            Application.DoEvents();
        }
        static ToolStripMenuItem MenuItem(ContextMenuStrip menu, string text) => menu.Items
            .OfType<ToolStripMenuItem>().Single(item => item.Text == text);
        void PinThroughMenu(HistoryQaFixture fixture, ConversationSummary room, bool pinned)
        {
            var card = fixture.Card(room);
            var menu = card.ContextMenuStrip!;
            menu.Show(card, new Point(8, 8)); Application.DoEvents();
            var action = MenuItem(menu, pinned ? "Sohbeti sabitle" : "Sabitlemeyi kaldır");
            Require(action is ModernMenuItem { Icon: ModernMenuIcon.Pin } && action.Enabled,
                room.Kind + ": actual modern context menu exposes the correct personal pin action");
            // Let the native popup finish its loop before testing a card reorder.
            menu.Close(); action.PerformClick();
            HistoryQaUntil(() => fixture.Form.IsPinnedConversation(room.Id) == pinned);
            Require(!menu.IsDisposed && !card.IsDisposed && ReferenceEquals(card, fixture.Card(room)),
                room.Kind + ": pin/unpin keeps the existing card and native menu owner alive");
        }

        using (var fixture = new HistoryQaFixture())
        {
            var form = fixture.Form;
            var at = new DateTimeOffset(2026, 10, 8, 10, 0, 0, TimeSpan.Zero);
            ConversationSummary Room(int id, string title, DateTimeOffset? activity, string kind = "group") =>
                fixture.InitialRoom with
                {
                    Id = Guid.Parse("00000000-0000-0000-0000-" + id.ToString("D12")), Title = title, Kind = kind,
                    LastMessageAt = at.AddDays(10), LastActivityAt = activity, ActivityMetadataAvailable = true, UnreadCount = 0
                };
            var oldGroup = Room(1, "Sabitleme deneme grubu", at.AddHours(-2));
            var oldDirect = Room(2, "Eski kişisel sohbet", at.AddHours(-1), "direct");
            var latest = Room(3, "En yeni mesaj", at.AddHours(2));
            var tieLow = Room(4, "Eşit tarih A", at);
            var tieHigh = Room(5, "Eşit tarih B", at);
            var emptyLow = Room(6, "Mesajsız A", null);
            var emptyHigh = Room(7, "Mesajsız B", null);
            var shuffled = new[] { emptyHigh, tieHigh, oldDirect, latest, emptyLow, oldGroup, tieLow };
            Apply(fixture, shuffled);
            Require(Order(form).SequenceEqual(new[] { latest.Id, tieLow.Id, tieHigh.Id, oldDirect.Id, oldGroup.Id, emptyLow.Id, emptyHigh.Id }),
                "Newest activity sorts first independently of server input order; null dates are last and equal dates use stable IDs");
            Require(Clock(fixture.Card(latest)) == ConversationTime(latest.LastActivityAt) &&
                Clock(fixture.Card(emptyLow)) == "" && form.ConversationActivityAt(emptyLow) is null,
                "The authoritative activity timestamp owns the clock, including an explicit empty date despite stale visible-message metadata");
            var identities = Cards(form).ToDictionary(card => ((ConversationSummary)card.Tag!).Id);
            for (var repeat = 0; repeat < 4; repeat++)
            {
                Apply(fixture, shuffled.Reverse().ToArray());
                Require(Cards(form).All(card => ReferenceEquals(card, identities[((ConversationSummary)card.Tag!).Id])),
                    $"Equivalent reordered server input reuses every existing sidebar control ({repeat + 1})");
            }

            PinThroughMenu(fixture, oldGroup, true);
            PinThroughMenu(fixture, oldDirect, true);
            Require(Order(form).Take(2).ToHashSet().SetEquals(new[] { oldGroup.Id, oldDirect.Id }) &&
                Array.IndexOf(Order(form), latest.Id) == 2,
                "Pinned group and direct conversations stay above even a much newer unpinned chat");
            foreach (var room in new[] { oldGroup, oldDirect })
            {
                var card = fixture.Card(room);
                var pin = card.Controls.Find("ConversationPinIndicator", true).Single();
                Require(pin.Visible && card.ClientRectangle.Contains(pin.Bounds) && !string.IsNullOrWhiteSpace(pin.AccessibleName),
                    room.Kind + ": the vector pin indicator fits its actual card and has an accessible name");
            }
            PinThroughMenu(fixture, oldDirect, false);
            Require(Order(form)[0] == oldGroup.Id && Order(form)[1] == latest.Id &&
                !fixture.Card(oldDirect).Controls.Find("ConversationPinIndicator", true).Single().Visible,
                "Unpinning the direct chat returns it to chronological order and removes only its own indicator");

            form.SetArchivedConversation(oldGroup.Id, true);
            form.SetConversationFilter("all"); form.ApplyConversationOrder();
            Require(!fixture.Card(oldGroup).Visible && Order(form, true)[0] == latest.Id,
                "A pinned archived group remains outside All instead of bypassing its personal archive filter");
            form.SetConversationFilter("archived"); form.ApplyConversationOrder();
            Require(Order(form, true).SequenceEqual(new[] { oldGroup.Id }),
                "The same pinned group is available inside Archive without exposing unrelated cards");
            form.SetArchivedConversation(oldGroup.Id, false);
            form.SetConversationFilter("direct"); form.ApplyConversationOrder();
            Require(Order(form, true).SequenceEqual(new[] { oldDirect.Id }),
                "Pinned groups do not leak into the direct-only filter");
            form.SetConversationFilter("all");
            form._premiumSearch.Text = latest.Title; form.ApplyConversationOrder();
            Require(Order(form, true).SequenceEqual(new[] { latest.Id }),
                "A chronological reorder respects the current title search even when another chat is pinned");
            form._premiumSearch.Text = "";
            form.SetPinnedConversation(oldDirect.Id, true);
            var pinnedWithUnread = oldGroup with { UnreadCount = 125 };
            Apply(fixture, Cards(form).Select(card =>
            {
                var current = (ConversationSummary)card.Tag!;
                return current.Id == oldGroup.Id ? pinnedWithUnread : current;
            }).ToArray());
            var pinnedCard = fixture.Card(pinnedWithUnread);
            var title = pinnedCard.Controls.Find("ConversationTitle", true).Single();
            var pinIndicator = pinnedCard.Controls.Find("ConversationPinIndicator", true).Single();
            var unreadBadge = pinnedCard.Controls.Find("ConversationUnreadBadge", true).Single();
            var originalWidth = pinnedCard.Width;
            try
            {
                foreach (var logicalWidth in new[] { 180, 240, 352 })
                {
                    pinnedCard.Width = (int)Math.Round(logicalWidth * form.DeviceDpi / 96f);
                    pinnedCard.PerformLayout();
                    Require(pinIndicator.Visible && unreadBadge.Visible &&
                        pinnedCard.ClientRectangle.Contains(title.Bounds) &&
                        pinnedCard.ClientRectangle.Contains(pinIndicator.Bounds) &&
                        pinnedCard.ClientRectangle.Contains(unreadBadge.Bounds),
                        $"Pinned card with 125 unread messages keeps its title and both indicators inside {logicalWidth}px logical width");
                    Require(!title.Bounds.IntersectsWith(pinIndicator.Bounds) &&
                        !title.Bounds.IntersectsWith(unreadBadge.Bounds) &&
                        !pinIndicator.Bounds.IntersectsWith(unreadBadge.Bounds),
                        $"Pinned card title, vector pin and 99+ unread badge have separate hit/paint rectangles at {logicalWidth}px logical width");
                }
            }
            finally { pinnedCard.Width = originalWidth; pinnedCard.PerformLayout(); }
            // Native child HWND painting follows queued search/filter/layout
            // changes; capture only after both layout and painting have settled.
            form.PerformLayout(); Application.DoEvents();
            form.Refresh(); Application.DoEvents();
            using (var screenshot = CaptureInfoClient(form))
                screenshot.Save(Path.Combine(directory, "conversation-pins-and-activity.png"));
            Require(!form.ChatPreferences()!.IsPersistent && fixture.Dialogs == 0,
                "Personal pins, archive and filter transitions remain non-modal and write no real account file");
        }

        using (var fixture = new HistoryQaFixture())
        {
            var form = fixture.Form;
            var selected = fixture.InitialRoom;
            var first = fixture.Message(selected, fixture.FirstSender, "Seçili sohbetin korunacak metni.");
            var me = form._session!.User;
            var voiceAt = DateTimeOffset.UtcNow;
            var clientId = Guid.NewGuid();
            var wave = MessageActionsQaWave();
            var voicePayload = MessageCryptography.Encrypt(wave, clientId, selected.Id, me.Id, me.Id, voiceAt,
                form._identity.ExportEncryptionPublicKey(), form._identity.SigningKey);
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(wave);
            var voice = new StoredMessage(Guid.NewGuid(), clientId, selected.Id, me.Id, "audio/wav", voiceAt, null, false, [voicePayload], null);
            fixture.Handler.Keys[me.Id] = new(me.Id, Guid.NewGuid(), form._identity.ExportEncryptionPublicKey(), form._identity.ExportSigningPublicKey());
            selected = selected with { LastMessageAt = voiceAt, LastActivityAt = voiceAt, ActivityMetadataAvailable = true };
            var background = fixture.AddRoom("Canlı sıralanan kişisel sohbet") with
            {
                Kind = "direct", LastMessageAt = null, LastActivityAt = voiceAt.AddHours(-1), ActivityMetadataAvailable = true
            };
            fixture.Handler.Messages[selected.Id] = [first, voice];
            Apply(fixture, selected, background);
            form._selectedConversation = selected; form._selectedConversationCard = fixture.Card(selected);
            HistoryQaPump(form.RefreshMessagesAsync(silent: true));
            var rows = form._messageList.Controls.Cast<Control>().ToArray();
            var voiceRow = form._messageList.Controls.OfType<MessageRow>().Single(row => row.MessageId == voice.Id);
            var voiceControls = HistoryQaControls(voiceRow).ToArray();
            var selectedCard = fixture.Card(selected);
            var backgroundCard = fixture.Card(background);
            var created = form._messageRowsCreated;
            var historyCalls = fixture.Handler.HistoryCalls.GetValueOrDefault(selected.Id);
            var version = form._conversationVersion;
            form._premiumComposer.Text = "Canlı sıralamada korunacak taslak 😊";
            background = background with { LastActivityAt = voiceAt.AddHours(2) };
            fixture.Handler.Rooms[background.Id] = background;
            HistoryQaPump(form.LoadConversationsAsync(silent: true));
            Require(Order(form)[0] == background.Id && form._selectedConversation?.Id == selected.Id &&
                ReferenceEquals(form._selectedConversationCard, selectedCard) && ReferenceEquals(fixture.Card(background), backgroundCard),
                "A real list HTTP polling response moves newer activity to the top without selecting it or replacing either card");
            Require(rows.SequenceEqual(form._messageList.Controls.Cast<Control>()) && rows.All(row => !row.IsDisposed) &&
                voiceControls.SequenceEqual(HistoryQaControls(voiceRow)) && voiceControls.All(control => !control.IsDisposed) &&
                form._messageRowsCreated == created && fixture.Handler.HistoryCalls.GetValueOrDefault(selected.Id) == historyCalls &&
                form._conversationVersion == version && form._premiumComposer.Text == "Canlı sıralamada korunacak taslak 😊",
                "Reordering preserves selected encrypted text/voice controls, player owner, history request count, version and unsent draft");
            form.SetPinnedConversation(selected.Id, true);
            background = background with { LastActivityAt = voiceAt.AddDays(3) };
            fixture.Handler.Rooms[background.Id] = background;
            HistoryQaPump(form.LoadConversationsAsync(silent: true));
            Require(Order(form)[0] == selected.Id && !voiceRow.IsDisposed && form._premiumComposer.Text.Contains("korunacak", StringComparison.Ordinal),
                "Further live polling cannot displace a pinned conversation or interrupt its retained voice owner");
            Require(fixture.Dialogs == 0, "Activity-only polling never opens an error or confirmation dialog");
        }

        using (var fixture = new HistoryQaFixture())
        {
            var form = fixture.Form;
            var room = fixture.InitialRoom;
            var older = fixture.Message(room, fixture.FirstSender, "Silinmeyen daha eski mesaj.");
            var latest = fixture.Message(room, fixture.SecondSender, "Silindikten sonra önizlemeye geri dönmemeli.");
            room = room with
            {
                LastMessageAt = latest.CreatedAt, LastActivityAt = latest.CreatedAt, ActivityMetadataAvailable = true,
                LastDeletedMessageAt = null, DeletedMessageMetadataAvailable = true
            };
            Apply(fixture, room);
            form._selectedConversation = room; form._selectedConversationCard = fixture.Card(room);
            fixture.Handler.Messages[room.Id] = [older, latest];
            HistoryQaPump(form.RefreshMessagesAsync(true));
            Require(Preview(fixture.Card(room)) == "Silindikten sonra önizlemeye geri dönmemeli.",
                "A genuine authenticated latest message initially supplies the preview text");
            fixture.Handler.Messages[room.Id] = [older, latest with { DeletedForEveryone = true }];
            room = room with { LastMessageAt = older.CreatedAt, LastDeletedMessageAt = latest.CreatedAt };
            Apply(fixture, room); form._selectedConversation = room;
            HistoryQaPump(form.RefreshMessagesAsync(true));
            Require(Preview(fixture.Card(room)) == "Bu mesaj silindi" &&
                Clock(fixture.Card(room)) == ConversationTime(latest.CreatedAt) && form.ConversationActivityAt(room) == latest.CreatedAt,
                "A latest deletion tombstone shows the fixed sidebar placeholder and keeps the newest activity clock instead of revealing older text");
            fixture.Handler.Messages[room.Id] = [latest with { DeletedForEveryone = true }];
            room = room with { LastMessageAt = null };
            Apply(fixture, room); form._selectedConversation = room;
            HistoryQaPump(form.RefreshMessagesAsync(true));
            Require(Preview(fixture.Card(room)) == "Bu mesaj silindi" && Clock(fixture.Card(room)) == ConversationTime(latest.CreatedAt),
                "Deleting the only message leaves the fixed sidebar placeholder and its server-supplied activity time");
            // Expiry/access revocation from a new server is authoritative. It is
            // not a legacy metadata omission and cannot keep a stale high-water.
            room = room with { LastActivityAt = null, LastMessageAt = null, LastDeletedMessageAt = null };
            Apply(fixture, room);
            form.UpdateConversationPreviewFromHistory(room.Id, [latest]);
            Require(form.ConversationActivityAt(room) is null && Clock(fixture.Card(room)) == "" && Preview(fixture.Card(room)) == "",
                "Authoritative null activity and deletion metadata clear the clock and placeholder despite cached/late visible history");
            room = room with { LastActivityAt = older.CreatedAt, LastMessageAt = older.CreatedAt };
            Apply(fixture, room);
            Require(form.ConversationActivityAt(room) == older.CreatedAt,
                "An authoritative older activity after expiry/access filtering wins over a previously observed newer timestamp");
        }

        using (var fixture = new HistoryQaFixture())
        {
            var form = fixture.Form;
            var room = fixture.InitialRoom with
            {
                LastMessageAt = null, LastActivityAt = null, ActivityMetadataAvailable = false,
                LastDeletedMessageAt = null, DeletedMessageMetadataAvailable = false
            };
            var latest = fixture.Message(room, fixture.FirstSender, "Bu silinmiş metin saklanmamalı.");
            Apply(fixture, room);
            // First learn metadata from a message explicitly addressed to this
            // account. A stripped tombstone alone is not proof of its audience.
            form.UpdateConversationPreviewFromHistory(room.Id, [latest]);
            form.UpdateConversationPreviewFromHistory(room.Id,
                [latest with { DeletedForEveryone = true, Payloads = [] }]);
            Require(form.ConversationActivityAt(room) == latest.CreatedAt && Preview(fixture.Card(room)) == "Bu mesaj silindi" &&
                Clock(fixture.Card(room)) == ConversationTime(latest.CreatedAt),
                "An older server shows the fixed placeholder for a known addressed message's stripped tombstone without retaining its removed plaintext");
            var unknownTombstone = latest with
            {
                Id = Guid.NewGuid(), ClientMessageId = Guid.NewGuid(), CreatedAt = latest.CreatedAt.AddDays(1),
                DeletedForEveryone = true, Payloads = []
            };
            form.UpdateConversationPreviewFromHistory(room.Id, [unknownTombstone]);
            Require(form.ConversationActivityAt(room) == latest.CreatedAt &&
                (Preview(fixture.Card(room)) == "" || Preview(fixture.Card(room)) == "Bu mesaj silindi") &&
                Clock(fixture.Card(room)) == ConversationTime(latest.CreatedAt),
                "A newer unknown non-addressed stripped tombstone cannot disclose another audience's activity or removed text; a legacy cache may keep only its known marker");
            var rolledBack = room with { LastMessageAt = latest.CreatedAt.AddDays(-1) };
            Apply(fixture, rolledBack);
            Require(form.ConversationActivityAt(rolledBack) == latest.CreatedAt,
                "Legacy timestamp rollback after message deletion cannot move an already observed chat backwards");
            room = rolledBack with { LastMessageAt = null };
            Apply(fixture, room);
            form.InvalidateConversationVisibility(room.Id);
            Require(form.ConversationActivityAt(room) == latest.CreatedAt,
                "Personal message clear erases content but preserves the legacy activity clock by default");
            form.InvalidateConversationVisibility(room.Id, preserveActivity: false);
            Require(form.ConversationActivityAt(room) is null && Clock(fixture.Card(room)) == "",
                "Explicit block/access invalidation clears legacy activity instead of exposing a previously observed blocked timestamp");

            form.UpdateConversationPreviewFromHistory(room.Id, [latest]);
            form.SetPinnedConversation(room.Id, true);
            var originalSession = form._session!;
            form._session = originalSession with { User = originalSession.User with { Id = Guid.NewGuid() } };
            Require(form.ConversationActivityAt(room) is null && !form.IsPinnedConversation(room.Id),
                "Switching accounts isolates both the legacy activity cache and personal conversation pins");
            form._session = originalSession;
            form.UpdateConversationPreviewFromHistory(room.Id, [latest]);
            var other = fixture.AddRoom("Listede kalan sohbet") with { LastMessageAt = null, LastActivityAt = null, ActivityMetadataAvailable = false };
            Apply(fixture, other);
            Apply(fixture, room, other);
            Require(form.ConversationActivityAt(room) is null && Clock(fixture.Card(room)) == "",
                "Removing and re-adding a conversation prunes its legacy observed timestamp instead of reviving stale activity");
            Require(fixture.Dialogs == 0 && !form.ChatPreferences()!.IsPersistent,
                "Compatibility, account-switch and pruning probes never contact a real account or persist plaintext/preferences");
        }
        checks.Add("Limits: synthetic native controls and local HTTP only; no physical multi-monitor DPI, real audio playback or deployed VDS polling was claimed.");
        return checks;
    }
}
