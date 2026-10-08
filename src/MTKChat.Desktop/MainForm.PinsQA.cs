using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using MTKChat.Contracts;
using MTKChat.Cryptography;

namespace MTKChat.Desktop;

internal sealed partial class MainForm
{
    // All HTTP is handled in memory. Rows take the genuine signed E2EE decrypt
    // path; no production account, message, VDS, microphone or clipboard is used.
    internal static IReadOnlyList<string> VerifyPinnedMessagesIntegration(string directory)
    {
        Directory.CreateDirectory(directory);
        using var context = new HistoryQaUiContext();
        using var senderIdentity = DeviceIdentity.Create();
        using var handler = new PinsQaHandler();
        using var api = new ChatApiClient("https://pins-qa.invalid/", handler, new ChatRequestPolicy { RetryDelay = TimeSpan.Zero });
        using var form = new MainForm(snapshotMode: true, api);
        form.Show(); form.PopulateSnapshot(); Application.DoEvents();
        var checks = new List<string>(); var dialogs = 0;
        void Require(bool value, string check)
        {
            HistoryQaAssertUiThread();
            if (!value) throw new InvalidOperationException("Pinned messages QA: " + check);
            checks.Add(check);
        }
        form._errorObserverForQa = _ => dialogs++;
        var me = form._session!.User;
        var normal = me with { Role = "user" };
        var peer = new ChatUser(Guid.NewGuid(), "Sabitlenen QA", "pins@example.invalid", false, null);
        var room = form._selectedConversation! with { Participants = [me, peer] };
        form._selectedConversation = room; form._selectedConversationCard!.Tag = room;
        handler.Keys[peer.Id] = new DeviceKeyBundle(peer.Id, Guid.NewGuid(), senderIdentity.ExportEncryptionPublicKey(), senderIdentity.ExportSigningPublicKey());
        handler.Actor = me.Id;
        Require(CanManagePins(me, room), "Current site-admin member can manage group pins");
        Require(!CanManagePins(normal, room) && !CanManagePins(me with { IsAgent = true }, room),
            "Normal group members and bots cannot manage pins");
        Require(CanManagePins(normal, room with { GroupRoles = new Dictionary<Guid, string> { [me.Id] = "admin" } }) &&
            CanManagePins(normal, room with { GroupRoles = new Dictionary<Guid, string> { [me.Id] = "mod" } }),
            "Group admins and mods can pin without gaining site administration");
        Require(CanManagePins(normal, room with { Kind = "direct" }) &&
            !CanManagePins(me, room with { Participants = [peer] }), "DM participants may pin; an absent site admin cannot");

        StoredMessage Encrypted(string text, DateTimeOffset? expires = null)
        {
            var client = Guid.NewGuid(); var at = DateTimeOffset.UtcNow;
            var payload = MessageCryptography.Encrypt(Encoding.UTF8.GetBytes(text), client, room.Id, peer.Id,
                me.Id, at, form._identity.ExportEncryptionPublicKey(), senderIdentity.SigningKey);
            return new StoredMessage(Guid.NewGuid(), client, room.Id, peer.Id, "text", at, expires, false, [payload], null);
        }
        var messages = new[] { Encrypted("İlk sabitlenen mesaj 😊"), Encrypted("İkinci sabitlenen mesaj"),
            Encrypted("Üçüncü sabitlenen mesaj") };
        handler.Messages[room.Id] = messages;
        HistoryQaPump(form.RefreshMessagesAsync(true));
        var rows = form._messageList.Controls.OfType<MessageRow>().ToArray();
        Require(rows.Length == 3 && rows.All(row => row.CanPin), "Only the real authenticated/decrypted history supplies pin previews");
        var realMenu = HistoryQaControls(rows[0]).Select(control => control.ContextMenuStrip).First(menu => menu is not null)!;
        var realPin = realMenu.Items.OfType<ModernMenuItem>().Single(item => item.Icon == ModernMenuIcon.Pin);
        realMenu.Show(rows[0], new Point(12, 12)); Application.DoEvents();
        Require(realPin.Available && realPin.Enabled && realPin.Text == "Mesajı sabitle", "The actual message context menu exposes the modern pin action for an authorized member");
        realMenu.Close(); Application.DoEvents();
        form._session = form._session with { User = normal };
        realMenu.Show(rows[0], new Point(12, 12)); Application.DoEvents();
        Require(!realPin.Available, "The same real context menu rechecks permissions and hides pinning from a normal group member");
        realMenu.Close(); Application.DoEvents(); form._session = form._session with { User = me };
        var historyCalls = handler.HistoryCalls; var createdRows = form._messageRowsCreated;
        var draft = "Sabitleme sırasında korunacak taslak ✨";
        form._premiumComposer.Text = draft; form._premiumComposer.Focus(); Application.DoEvents();
        HistoryQaPump(form.SetMessagePinnedAsync(rows[0], 168));
        Application.DoEvents();
        Require(handler.Posts == 1 && handler.LastDuration == 168 && handler.LastRequestProperties?.SequenceEqual(["messageId", "durationHours"]) == true,
            "Pin POST contains only message ID and chosen expiry, never plaintext or media");
        Require(form._pinnedBanner.MessageId == messages[0].Id && form._pinnedBanner.PreviewForQa == "İlk sabitlenen mesaj 😊" && form._pinBannerHeight!.Height > 0,
            "Successful shared pin opens a themed compact banner beneath the header");
        Require(handler.HistoryCalls == historyCalls && form._messageRowsCreated == createdRows && ReferenceEquals(rows[0], form._messageList.Controls.OfType<MessageRow>().First()),
            "Pinning does not fetch/decrypt history or recreate message controls");
        Require(form._premiumComposer.Text == draft && form._premiumComposer.ContainsFocus && dialogs == 0,
            "Pinning preserves the draft and composer focus without a success MessageBox");
        for (var index = 1; index < rows.Length; index++) HistoryQaPump(form.SetMessagePinnedAsync(rows[index], index == 1 ? 24 : 720));
        Require(form.AccessiblePinnedRows().Count == 3, "Three accessible messages share one compact navigable banner");
        var current = form._pinnedBanner.MessageId;
        var next = HistoryQaControls(form._pinnedBanner).OfType<ModernButton>().Single();
        next.PerformClick(); Application.DoEvents();
        Require(form._pinnedBanner.MessageId != current, "The 1/3 button advances to the next pinned message");
        var bannerTop = form._pinnedBanner.PointToScreen(Point.Empty).Y;
        Require(bannerTop >= form._conversationTitle.PointToScreen(Point.Empty).Y + form._conversationTitle.Height &&
            form._pinnedBanner.Width > 200 && form._pinnedBanner.Height >= form._pinnedBanner.RequiredTextHeight,
            "Banner text and actions fit below the header rather than overlapping it");
        using (var image = new Bitmap(form.Width, form.Height))
        { form.DrawToBitmap(image, new Rectangle(Point.Empty, form.Size)); image.Save(Path.Combine(directory, "pinned-messages-three.png")); }
        using (var chooser = new PinDurationForm())
        {
            chooser.Show(form); Application.DoEvents();
            Require(chooser.DurationHours == 168, "Pin duration chooser defaults to seven days");
            var durationButton = HistoryQaControls(chooser).OfType<ModernButton>().Single(button => button.AccessibleName == "30 gün sabitleme süresi");
            durationButton.PerformClick(); Require(chooser.DurationHours == 720, "Thirty-day duration is selectable without modifying message expiry");
            using var image = new Bitmap(chooser.Width, chooser.Height);
            chooser.DrawToBitmap(image, new Rectangle(Point.Empty, chooser.Size)); image.Save(Path.Combine(directory, "pin-duration-chooser.png"));
            chooser.Close();
        }
        HistoryQaPump(form.SetMessagePinnedAsync(rows[1], null));
        Require(handler.Deletes == 1 && form.AccessiblePinnedRows().Count == 2 && !rows[1].IsDisposed,
            "Unpin removes only shared metadata, not the underlying message");
        var readsBeforeUnchanged = handler.HistoryCalls; var rowCountBefore = form._messageRowsCreated;
        for (var i = 0; i < 5; i++) HistoryQaPump(form.RefreshPinnedMessagesAsync(force: true));
        Require(handler.HistoryCalls == readsBeforeUnchanged && form._messageRowsCreated == rowCountBefore && form._premiumComposer.Text == draft,
            "Repeated unchanged pin polls do not decrypt, rebuild, scroll-reset or erase a draft");

        // Remote actor removes a pin. The same currently loaded row supplies the
        // new banner, without fetching another copy of its plaintext.
        handler.Pins[room.Id] = [new PinnedMessageView(messages[1].Id, room.Id, peer.Id, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(24))];
        HistoryQaPump(form.RefreshPinnedMessagesAsync(force: true));
        Require(form._pinnedBanner.MessageId == messages[1].Id, "Another participant's pin change arrives through metadata-only refresh");
        handler.Messages[room.Id] = [messages[0], messages[2]];
        HistoryQaPump(form.RefreshMessagesAsync(true));
        Require(form._pinBannerHeight!.Height == 0 && form._pinnedBanner.MessageId is null,
            "Personally hidden or removed history rows cannot be resurrected by shared pin metadata");
        handler.Messages[room.Id] = [messages[1] with { DeletedForEveryone = true }];
        HistoryQaPump(form.RefreshMessagesAsync(true));
        Require(form._pinBannerHeight.Height == 0 && !form._messageList.Controls.OfType<MessageRow>().Single().CanPin,
            "A delete-for-everyone tombstone never exposes or offers a pin preview");
        handler.Messages[room.Id] = [messages[1] with { Payloads = [] }];
        HistoryQaPump(form.RefreshMessagesAsync(true));
        Require(!form._messageList.Controls.OfType<MessageRow>().Single().CanPin && form._pinBannerHeight.Height == 0,
            "Unavailable or invalid encrypted envelopes cannot become pinned previews");
        handler.Messages[room.Id] = [messages[1] with { ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1) }];
        HistoryQaPump(form.RefreshMessagesAsync(true));
        Require(form.AccessiblePinnedRows().Count == 0, "Expired disappearing messages remain absent even if stale pin metadata exists");
        handler.Messages[room.Id] = messages;
        HistoryQaPump(form.RefreshMessagesAsync(true));
        handler.Pins[room.Id] = [new PinnedMessageView(messages[0].Id, room.Id, me.Id, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddSeconds(-1))];
        HistoryQaPump(form.RefreshPinnedMessagesAsync(force: true));
        Require(form._pinBannerHeight.Height == 0, "Expired pins collapse without deleting message content");

        // Do not fetch HTTP or depend on the background retry schedule to remove
        // the already copied banner text when either kind of deadline expires.
        form._nextNetworkPoll = DateTimeOffset.UtcNow.AddMinutes(5);
        handler.Pins[room.Id] = [new PinnedMessageView(messages[0].Id, room.Id, me.Id,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMilliseconds(1400))];
        HistoryQaPump(form.RefreshPinnedMessagesAsync(force: true));
        var readsBeforeLocalExpiry = handler.PinGets; var historyBeforeLocalExpiry = handler.HistoryCalls;
        Require(form._pinnedBanner.MessageId == messages[0].Id && form._pinExpiryTimer.Enabled,
            "An active copied pin preview starts its independent local expiry timer");
        HistoryQaUntil(() => form._pinnedBanner.MessageId is null);
        Require(form._pinBannerHeight.Height == 0 && form._pinnedBanner.PreviewForQa.Length == 0 && !form._pinExpiryTimer.Enabled &&
            handler.PinGets == readsBeforeLocalExpiry && handler.HistoryCalls == historyBeforeLocalExpiry &&
            DateTimeOffset.UtcNow < form._nextNetworkPoll,
            "A pin deadline erases copied text offline during network backoff without HTTP or history reload");

        handler.Pins[room.Id] = [new PinnedMessageView(messages[0].Id, room.Id, me.Id,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1))];
        handler.Messages[room.Id] = [messages[0] with { ExpiresAt = DateTimeOffset.UtcNow.AddMilliseconds(1400) }];
        HistoryQaPump(form.RefreshMessagesAsync(true));
        HistoryQaPump(form.RefreshPinnedMessagesAsync(force: true));
        Require(form._pinnedBanner.MessageId == messages[0].Id, "A pin can briefly reference an accessible disappearing message before its own deadline");
        readsBeforeLocalExpiry = handler.PinGets; historyBeforeLocalExpiry = handler.HistoryCalls;
        HistoryQaUntil(() => form._pinnedBanner.MessageId is null);
        Require(form._pinnedBanner.PreviewForQa.Length == 0 && form._pinBannerHeight.Height == 0 &&
            handler.PinGets == readsBeforeLocalExpiry && handler.HistoryCalls == historyBeforeLocalExpiry,
            "The original message deadline independently clears a still-valid pin preview offline");
        handler.Messages[room.Id] = messages;
        HistoryQaPump(form.RefreshMessagesAsync(true));
        HistoryQaPump(form.RefreshPinnedMessagesAsync(force: true));
        var popupRow = form._messageList.Controls.OfType<MessageRow>().First();
        var popup = HistoryQaControls(popupRow).Select(control => control.ContextMenuStrip).First(menu => menu is not null)!;
        // Model the production owned dialog, then wait for real native focus.
        // A foreground-sensitive ToolStrip must not be tested on a background
        // fixture after a previously unowned chooser relinquished activation.
        form.Activate(); form._premiumComposer.Focus();
        HistoryQaUntil(() => form.ContainsFocus);
        var popupOpened = 0;
        ToolStripDropDownCloseReason? popupCloseReason = null;
        popup.Opened += (_, _) => popupOpened++;
        popup.Closed += (_, e) => popupCloseReason = e.CloseReason;
        popup.Show(popupRow, new Point(12, 12)); Application.DoEvents();
        Require(form._pinnedBanner.MessageId == messages[0].Id && popup.Visible,
            $"An actual message popup is open beside an already copied pin preview; bannerMatches={form._pinnedBanner.MessageId == messages[0].Id}, popupVisible={popup.Visible}, rowDisposed={popupRow.IsDisposed}, opened={popupOpened}, closeReason={popupCloseReason}, ownerFocused={form.ContainsFocus}");
        form.InvalidateConversationVisibility(room.Id);
        Require(form._pinnedBanner.MessageId is null && form._pinnedBanner.PreviewForQa.Length == 0 && form._pinBannerHeight.Height == 0 &&
            form._pinnedMessages.Count == 0 && !form._pinExpiryTimer.Enabled && !popupRow.IsDisposed,
            "Block/private-delete visibility invalidation clears the copied pin before its open-popup row-disposal guard");
        popup.Close(); Application.DoEvents();
        form._nextNetworkPoll = default;
        HistoryQaPump(form.RefreshMessagesAsync(true));

        handler.PinStatus = HttpStatusCode.NotFound;
        HistoryQaPump(form.RefreshPinnedMessagesAsync(force: true)); var callsAfterMissing = handler.PinGets;
        HistoryQaPump(form.RefreshPinnedMessagesAsync(force: true));
        Require(handler.PinGets == callsAfterMissing && form._pinBannerHeight.Height == 0 && dialogs == 0,
            "Older VDS 404 is quiet and backs off instead of poll-loop error dialogs");
        handler.PinStatus = HttpStatusCode.OK; form._pinsUnsupportedUntil = default;
        handler.Pins[room.Id] = [new PinnedMessageView(messages[0].Id, room.Id, me.Id, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(24))];
        HistoryQaPump(form.RefreshPinnedMessagesAsync(force: true));
        handler.PinStatus = HttpStatusCode.Forbidden;
        HistoryQaPump(form.RefreshPinnedMessagesAsync(force: true));
        Require(form._pinBannerHeight.Height == 0 && dialogs == 0, "Lost read authorization immediately clears pin previews quietly");

        handler.PinStatus = HttpStatusCode.OK;
        var secondRoom = room with { Id = Guid.NewGuid(), Title = "Başka QA sohbeti" };
        handler.Messages[secondRoom.Id] = [];
        var secondCard = form.CreateConversationCard(secondRoom); form._conversationList.Controls.Add(secondCard);
        handler.HoldPinGet = true;
        var pending = form.RefreshPinnedMessagesAsync(force: true);
        HistoryQaUntil(() => handler.HeldResponse is not null);
        HistoryQaPump(form.SelectConversationAsync(secondRoom, secondCard, silent: true));
        Require(form._pinBannerHeight.Height == 0 && handler.Cancellations > 0,
            "Changing rooms immediately hides the banner and cancels an outstanding metadata GET");
        handler.HeldResponse!.TrySetResult(PinsQaHandler.Json(handler.Pins[room.Id]));
        HistoryQaPump(pending);
        Require(form._selectedConversation!.Id == secondRoom.Id && form._pinnedMessages.Count == 0 && form._pinnedBanner.MessageId is null,
            "Late cancellation-ignoring old-room HTTP results never restore old pins in a new room");
        form.ResetConversationSelection();
        Require(form._pinnedBanner.MessageId is null && form._pinBannerHeight.Height == 0,
            "Clearing selection also clears pin text and routing metadata");
        Require(dialogs == 0, "All read-only metadata failure and room-change cases remain non-modal");
        checks.Add("Scope: fake HTTP and native WinForms; live VDS deployment and physical multi-device synchronization were not exercised.");
        return checks;
    }

    private sealed class PinsQaHandler : HttpMessageHandler
    {
        internal readonly Dictionary<Guid, StoredMessage[]> Messages = new();
        internal readonly Dictionary<Guid, DeviceKeyBundle> Keys = new();
        internal readonly Dictionary<Guid, List<PinnedMessageView>> Pins = new();
        internal Guid Actor;
        internal int HistoryCalls, PinGets, Posts, Deletes, LastDuration, Cancellations;
        internal string[]? LastRequestProperties;
        internal HttpStatusCode PinStatus = HttpStatusCode.OK;
        internal bool HoldPinGet;
        internal TaskCompletionSource<HttpResponseMessage>? HeldResponse;
        internal static HttpResponseMessage Json<T>(T value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var parts = request.RequestUri!.AbsolutePath.Trim('/').Split('/');
            var leaf = parts[^1];
            if (leaf == "device") return Json(Keys[Guid.Parse(parts[^2])]);
            if (leaf == "messages" && request.Method == HttpMethod.Get)
            { HistoryCalls++; return Json(Messages.GetValueOrDefault(Guid.Parse(parts[^2])) ?? []); }
            if (leaf == "presence") return Json(Array.Empty<PresenceView>());
            var pinIndex = Array.IndexOf(parts, "pins");
            if (pinIndex < 0) throw new InvalidOperationException("Pins QA forbids live/unrelated route: " + request.RequestUri.AbsolutePath);
            var room = Guid.Parse(parts[pinIndex - 1]);
            if (!Pins.TryGetValue(room, out var pins)) Pins.Add(room, pins = []);
            if (request.Method == HttpMethod.Get)
            {
                PinGets++;
                if (HoldPinGet)
                {
                    HoldPinGet = false;
                    HeldResponse = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    using var registration = ct.Register(() => Interlocked.Increment(ref Cancellations));
                    return await HeldResponse.Task.ConfigureAwait(false); // deliberately ignore cancellation to test version fencing
                }
                if (PinStatus != HttpStatusCode.OK) return new HttpResponseMessage(PinStatus) { Content = JsonContent.Create(new ApiError("pins_qa", "Scripted optional pins endpoint")) };
                return Json(pins.ToArray());
            }
            if (request.Method == HttpMethod.Post)
            {
                Posts++;
                var body = await request.Content!.ReadAsStringAsync(ct);
                using var json = JsonDocument.Parse(body);
                LastRequestProperties = json.RootElement.EnumerateObject().Select(property => property.Name).ToArray();
                var input = JsonSerializer.Deserialize<PinMessageRequest>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
                LastDuration = input.DurationHours;
                var pin = new PinnedMessageView(input.MessageId, room, Actor, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(input.DurationHours));
                pins.RemoveAll(old => old.MessageId == pin.MessageId); pins.Add(pin);
                return Json(pin);
            }
            if (request.Method == HttpMethod.Delete)
            { Deletes++; pins.RemoveAll(pin => pin.MessageId == Guid.Parse(leaf)); return new HttpResponseMessage(HttpStatusCode.NoContent); }
            throw new InvalidOperationException("Pins QA forbids request method.");
        }
    }
}
