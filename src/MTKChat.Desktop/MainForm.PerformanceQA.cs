using MTKChat.Contracts;

namespace MTKChat.Desktop;

internal sealed partial class MainForm
{
    // Exercise the production HTTP/decrypt/row/reconciliation paths on the UI
    // thread. These fixtures contain no real account or remote request, and report
    // operation counts rather than a machine-dependent "fast enough" threshold.
    internal static IReadOnlyList<string> VerifyPerformance()
    {
        using var context = new HistoryQaUiContext();
        var checks = new List<string>();
        void Require(bool valid, string check)
        {
            HistoryQaAssertUiThread();
            if (!valid) throw new InvalidOperationException("Performance UI QA: " + check);
            checks.Add(check);
        }

        using var fixture = new HistoryQaFixture();
        var form = fixture.Form;
        var room = fixture.InitialRoom;
        var body = string.Join('\n', Enumerable.Range(0, 42).Select(index =>
            $"Tam satır {index:00}: Türkçe karakterler ğüşiöç, emoji 😊 ve eksiksiz korunacak uzun mesaj içeriği."));
        var messages = Enumerable.Range(0, 48).Select(index => fixture.Message(room, fixture.FirstSender,
            $"Geçmiş mesajı {index:00}\n{body}\nSON SATIR {index:00}")).ToArray();
        fixture.Handler.Messages[room.Id] = messages;
        var progress = 0;
        var postedInputObserved = false;
        form._historyProgressObserverForQa = count =>
        {
            progress = count;
            if (count != 1) return;
            form.BeginInvoke((Action)(() => postedInputObserved = form._refreshing && progress < messages.Length));
        };
        var createdBefore = form._messageRowsCreated;
        var yieldedBefore = form._historyUiYieldCount;
        HistoryQaPump(form.RefreshMessagesAsync(silent: true));
        Application.DoEvents();
        form._historyProgressObserverForQa = null;
        var rows = form._messageList.Controls.OfType<MessageRow>().ToDictionary(row => row.MessageId);
        Require(rows.Count == messages.Length && form._messageRowsCreated - createdBefore == messages.Length,
            "Initial encrypted history creates each message row exactly once");
        Require(HistoryQaHasText(rows[messages[^1].Id], "SON SATIR 47") && rows.Values.All(row => row.CanMarkRead),
            "Long messages retain their final lines and successful decrypt/read state without truncation");
        Require(form._historyUiYieldCount - yieldedBefore >= 6 && postedInputObserved,
            "Long first history loads yield at least every eight rows and service posted UI input before completion");
        var day = form._messageList.Controls.OfType<MessageDateDivider>().Single();
        var scroll = form._messageList.MaximumOffset / 3;
        form._messageList.ScrollToOffset(scroll);
        form._premiumComposer.Text = "Korunacak taslak 😊";
        Application.DoEvents();
        var layouts = 0;
        LayoutEventHandler countLayout = (_, _) => layouts++;
        form._messageList.Layout += countLayout;
        createdBefore = form._messageRowsCreated;
        var resizeBefore = form._bubbleResizePasses;
        yieldedBefore = form._historyUiYieldCount;
        for (var poll = 0; poll < 10; poll++) HistoryQaPump(form.RefreshMessagesAsync(silent: true));
        Require(rows.All(pair => ReferenceEquals(pair.Value,
            form._messageList.Controls.OfType<MessageRow>().Single(row => row.MessageId == pair.Key))) &&
            form._messageRowsCreated == createdBefore && form._historyUiYieldCount == yieldedBefore,
            "Ten identical history polls preserve every row and do not decrypt/build/yield an unchanged batch");
        Require(layouts == 0 && form._bubbleResizePasses == resizeBefore,
            "Unchanged history polling triggers no message layout or bubble measurement pass");
        Require(form._messageList.Offset == scroll && form._premiumComposer.Text == "Korunacak taslak 😊",
            "Unchanged polling preserves the non-bottom scroll position and composer draft");

        var appended = fixture.Message(room, fixture.SecondSender, "Yeni mesaj: yalnızca bu satır oluşturulmalı.");
        messages = [.. messages, appended];
        fixture.Handler.Messages[room.Id] = messages;
        HistoryQaPump(form.RefreshMessagesAsync(silent: true));
        Require(form._messageRowsCreated == createdBefore + 1 && rows.Values.All(row => !row.IsDisposed) &&
            rows.All(pair => ReferenceEquals(pair.Value,
                form._messageList.Controls.OfType<MessageRow>().Single(row => row.MessageId == pair.Key))),
            "Appending one message creates one row and preserves all existing message controls and media owners");
        Require(ReferenceEquals(day, form._messageList.Controls.OfType<MessageDateDivider>().Single()) &&
            form._messageList.Offset == scroll,
            "Appending to the same day reuses its divider and keeps a reader's prior scroll offset");

        var receipt = new MessageDelivery("read", [new RecipientReceipt(form._session!.User.Id,
            DateTimeOffset.UtcNow.AddSeconds(-1), DateTimeOffset.UtcNow)]);
        messages[0] = messages[0] with { Delivery = receipt };
        createdBefore = form._messageRowsCreated;
        HistoryQaPump(form.RefreshMessagesAsync(silent: true));
        Require(ReferenceEquals(rows[messages[0].Id].Delivery, receipt) ||
            rows[messages[0].Id].Delivery?.Status == "read",
            "Receipt-only polling updates delivery metadata on the existing message row");
        Require(form._messageRowsCreated == createdBefore && !rows[messages[0].Id].IsDisposed,
            "Receipt metadata does not rebuild content or stop an existing row's player");

        var removed = messages[1].Id;
        messages = messages.Where(message => message.Id != removed).ToArray();
        fixture.Handler.Messages[room.Id] = messages;
        HistoryQaPump(form.RefreshMessagesAsync(silent: true));
        Require(rows[removed].IsDisposed && form._messageRowsCreated == createdBefore &&
            form._renderedMessageRows.Count == messages.Length &&
            !form._messageList.Controls.OfType<MessageRow>().Any(row => row.MessageId == removed),
            "Removing a message disposes only its row and bounds the reuse cache to live selected-history rows");
        Require(rows.Where(pair => pair.Key != removed).All(pair => !pair.Value.IsDisposed),
            "Deleting one row retains all unrelated history controls");

        var changed = messages[2];
        var oldChanged = rows[changed.Id];
        var altered = fixture.Message(room, fixture.FirstSender, "Yenilenen şifreli metnin son satırı.") with { Id = changed.Id };
        messages[2] = altered;
        fixture.Handler.Messages[room.Id] = messages;
        HistoryQaPump(form.RefreshMessagesAsync(silent: true));
        var replacement = form._messageList.Controls.OfType<MessageRow>().Single(row => row.MessageId == changed.Id);
        Require(oldChanged.IsDisposed && !ReferenceEquals(oldChanged, replacement) &&
            HistoryQaHasText(replacement, "Yenilenen şifreli metnin son satırı"),
            "Changed encrypted payload/client signature inputs are reverified even when ID and payload count stay the same");
        Require(form._messageRowsCreated == createdBefore + 1,
            "Replacing one encrypted message rebuilds only that message");

        var signature = Convert.FromBase64String(altered.Payloads.Single().Signature);
        signature[0] ^= 1;
        var tampered = altered with
        {
            Payloads = [altered.Payloads.Single() with { Signature = Convert.ToBase64String(signature) }]
        };
        messages[2] = tampered;
        fixture.Handler.Messages[room.Id] = messages;
        HistoryQaPump(form.RefreshMessagesAsync(silent: true));
        var tamperedRow = form._messageList.Controls.OfType<MessageRow>().Single(row => row.MessageId == altered.Id);
        Require(replacement.IsDisposed && !tamperedRow.CanMarkRead && HistoryQaHasText(tamperedRow, "Mesaj açılamadı") &&
            !HistoryQaHasText(tamperedRow, "Yenilenen şifreli metnin son satırı"),
            "A signature-only mutation with the same message/client/time/payload count re-verifies and never reuses plaintext or read eligibility");
        messages[2] = altered;
        fixture.Handler.Messages[room.Id] = messages;
        HistoryQaPump(form.RefreshMessagesAsync(silent: true));
        replacement = form._messageList.Controls.OfType<MessageRow>().Single(row => row.MessageId == altered.Id);
        Require(tamperedRow.IsDisposed && replacement.CanMarkRead &&
            HistoryQaHasText(replacement, "Yenilenen şifreli metnin son satırı"),
            "Restoring the verified payload with unchanged message/client/time re-decrypts the row successfully");

        messages[2] = altered with { DeletedForEveryone = true };
        fixture.Handler.Messages[room.Id] = messages;
        HistoryQaPump(form.RefreshMessagesAsync(silent: true));
        Require(replacement.IsDisposed && HistoryQaHasText(
            form._messageList.Controls.OfType<MessageRow>().Single(row => row.MessageId == altered.Id), "Mesaj silindi"),
            "Delete-for-everyone replaces its content promptly instead of reusing stale plaintext");

        form.ResizeBubbles();
        resizeBefore = form._bubbleResizePasses;
        for (var tick = 0; tick < 100; tick++) form.ResizeBubbles();
        Require(form._bubbleResizePasses == resizeBefore,
            "A hundred repeated geometry notifications at identical width/height skip bubble remeasurement");
        form._messageList.Layout -= countLayout;

        var summaries = fixture.Handler.Rooms.Values.ToArray();
        form.ReconcileConversationCards(summaries);
        var cards = form._conversationList.Controls.OfType<RoundedPanel>().ToArray();
        var reconciliationBefore = form._conversationReconcilePasses;
        for (var poll = 0; poll < 100; poll++)
            form.ReconcileConversationCards(summaries.Select(summary => summary with
            {
                Participants = summary.Participants.ToArray(),
                GroupRoles = summary.GroupRoles?.ToDictionary(pair => pair.Key, pair => pair.Value)
            }).ToArray());
        Require(form._conversationReconcilePasses == reconciliationBefore && cards.SequenceEqual(
            form._conversationList.Controls.OfType<RoundedPanel>()),
            "A hundred logically identical deserialized sidebar polls perform no card reconciliation or JSON serialization");
        summaries[0] = summaries[0] with { UnreadCount = summaries[0].UnreadCount + 1 };
        var changedCard = cards.Single(card => ((ConversationSummary)card.Tag!).Id == summaries[0].Id);
        form.ReconcileConversationCards(summaries);
        Require(form._conversationReconcilePasses == reconciliationBefore + 1 && changedCard.IsDisposed &&
            form._conversationList.Controls.OfType<RoundedPanel>().Select(card => (ConversationSummary)card.Tag!)
                .Single(room => room.Id == summaries[0].Id).UnreadCount == summaries[0].UnreadCount,
            "A real unread-count change still replaces the affected sidebar card exactly once");

        var previousControls = form._messageList.Controls.Cast<Control>().ToArray();
        var previousCacheCount = form._renderedMessageRows.Count;
        var postponed = fixture.Message(room, fixture.SecondSender, "İptal edilen küçük yeni satır sonra sağlıklı yüklenecek.");
        var canceledHistory = new[] { postponed }.Concat(messages).ToArray();
        fixture.Handler.Messages[room.Id] = canceledHistory;
        form._historyProgressObserverForQa = count =>
        {
            if (count == 1) form.BeginInvoke((Action)form.CancelHistoryLoad);
        };
        HistoryQaPump(form.RefreshMessagesAsync(silent: true));
        form._historyProgressObserverForQa = null;
        Require(previousControls.All(control => !control.IsDisposed && control.Parent == form._messageList) &&
            form._renderedMessageRows.Count == previousCacheCount,
            "Canceling a partially reused history batch never disposes existing rows or commits a partial cache");
        HistoryQaPump(form.RefreshMessagesAsync(silent: true));
        Require(form._messageList.Controls.OfType<MessageRow>().Count() == canceledHistory.Length &&
            HistoryQaHasText(form._messageList, "İptal edilen küçük yeni satır sonra sağlıklı yüklenecek"),
            "A canceled history batch can be retried without stranded loading state or a missing row");
        messages = canceledHistory;

        var popupOwnerMessage = messages.First(message => !message.DeletedForEveryone && message.Id != messages[0].Id);
        var popupOwner = form._messageList.Controls.OfType<MessageRow>().Single(row => row.MessageId == popupOwnerMessage.Id);
        var popup = popupOwner.Controls.Cast<Control>().Select(control => control.ContextMenuStrip)
            .First(menu => menu is not null)!;
        var popupExtra = fixture.Message(room, fixture.SecondSender, "Menü kapandıktan sonra eklenecek yeni mesaj.");
        var popupHistory = messages.Select(message => message.Id == popupOwnerMessage.Id
            ? message with { DeletedForEveryone = true } : message).Append(popupExtra).ToArray();
        fixture.Handler.Messages[room.Id] = popupHistory;
        var fingerprintBeforePopup = form._renderFingerprint;
        var popupOpenedCount = 0;
        var popupClosedReasons = new List<ToolStripDropDownCloseReason>();
        var popupShowDispatched = false;
        var popupShowDuringRefresh = false;
        var popupYieldBefore = form._historyUiYieldCount;
        EventHandler popupOpened = (_, _) => popupOpenedCount++;
        ToolStripDropDownClosedEventHandler popupClosed = (_, args) => popupClosedReasons.Add(args.CloseReason);
        popup.Opened += popupOpened;
        popup.Closed += popupClosed;
        try
        {
            form._historyProgressObserverForQa = count =>
            {
                if (count == 1) form.BeginInvoke((Action)(() =>
                {
                    popupShowDispatched = true;
                    popupShowDuringRefresh = form._refreshing;
                    popup.Show(form._messageList, new Point(12, 12));
                }));
            };
            HistoryQaPump(form.RefreshMessagesAsync(silent: true));
            form._historyProgressObserverForQa = null;
            var extraCommitted = form._messageList.Controls.OfType<MessageRow>().Any(row => row.MessageId == popupExtra.Id);
            var fingerprintUnchanged = form._renderFingerprint == fingerprintBeforePopup;
            var commitDeferred = popup.Visible && !popup.IsDisposed && !popupOwner.IsDisposed &&
                fingerprintUnchanged && !extraCommitted;
            // Native dropdowns may also close because the desktop changes focus.
            // Keep the original assertion strict, but report which predicate and
            // native lifecycle failed rather than mislabeling any close as a commit bug.
            Require(commitDeferred,
                "A message popup opened during an awaited UI slice defers the commit and keeps its existing owner alive" +
                (commitDeferred ? "" : $" [showDispatched={popupShowDispatched}; showDuringRefresh={popupShowDuringRefresh}; " +
                    $"opened={popupOpenedCount}; closed={string.Join(",", popupClosedReasons)}; visible={popup.Visible}; " +
                    $"popupDisposed={popup.IsDisposed}; ownerDisposed={popupOwner.IsDisposed}; detectedOpen={form.MessagePopupOpen()}; " +
                    $"fingerprintUnchanged={fingerprintUnchanged}; extraCommitted={extraCommitted}; yields={form._historyUiYieldCount - popupYieldBefore}]"));
            popup.Close(); Application.DoEvents();
            HistoryQaPump(form.RefreshMessagesAsync(silent: true));
            Require(popupOwner.IsDisposed && popup.IsDisposed &&
                HistoryQaHasText(form._messageList.Controls.OfType<MessageRow>().Single(row => row.MessageId == popupOwnerMessage.Id), "Mesaj silindi") &&
                form._messageList.Controls.OfType<MessageRow>().Any(row => row.MessageId == popupExtra.Id),
                "After closing the mid-batch popup, the next poll replaces only its changed owner and applies the queued history");
        }
        finally
        {
            form._historyProgressObserverForQa = null;
            popup.Opened -= popupOpened;
            popup.Closed -= popupClosed;
        }
        messages = popupHistory;

        var stableRows = form._messageList.Controls.OfType<MessageRow>().ToDictionary(row => row.MessageId);
        var heartbeatObserved = 0;
        createdBefore = form._messageRowsCreated;
        for (var cycle = 0; cycle < 100; cycle++)
        {
            var transient = fixture.Message(room, fixture.SecondSender, $"Kararlılık çevrimi {cycle:000}: yalnızca yeni satır.");
            fixture.Handler.Messages[room.Id] = [.. messages, transient];
            form.BeginInvoke((Action)(() => heartbeatObserved++));
            HistoryQaPump(form.RefreshMessagesAsync(silent: true));
            var transientRow = form._messageList.Controls.OfType<MessageRow>().Single(row => row.MessageId == transient.Id);
            if (form._renderedMessageRows.Count != messages.Length + 1)
                throw new InvalidOperationException($"Performance soak cache grew unexpectedly in cycle {cycle}.");
            fixture.Handler.Messages[room.Id] = messages;
            HistoryQaPump(form.RefreshMessagesAsync(silent: true));
            if (!transientRow.IsDisposed || form._renderedMessageRows.Count != messages.Length ||
                stableRows.Values.Any(row => row.IsDisposed || row.Parent != form._messageList))
                throw new InvalidOperationException($"Performance soak retained/disposed the wrong row in cycle {cycle}.");
        }
        Require(form._messageRowsCreated - createdBefore == 100 &&
            stableRows.All(pair => ReferenceEquals(pair.Value,
                form._messageList.Controls.OfType<MessageRow>().Single(row => row.MessageId == pair.Key))),
            "One hundred append/remove cycles build exactly one hundred new rows and retain every unchanged history row");
        Require(form._renderedMessageRows.Count == messages.Length && form._renderedDateDividers.Count == 1,
            "Two hundred changing-history refreshes keep row/date caches bounded to the current history");
        Require(heartbeatObserved == 100,
            "Posted UI heartbeat callbacks are serviced throughout all one hundred append/remove cycles");
        Require(fixture.Dialogs == 0, "Performance/history reconciliation never opens a modal error dialog");

        using (var closingHandler = new SendCloseQaHandler())
        {
            var closingApi = new ChatApiClient("https://send-close-qa.invalid/", closingHandler,
                new ChatRequestPolicy { RetryDelay = TimeSpan.Zero });
            using var closingForm = new MainForm(snapshotMode: true, closingApi);
            closingForm.Show(); closingForm.PopulateSnapshot(); Application.DoEvents();
            closingForm._premiumComposer.Text = "Kapanan pencerenin sentetik gönderim taslağı.";
            var pendingSend = closingForm.SendTextAsync();
            HistoryQaUntil(() => closingHandler.Requested);
            Require(closingForm._sending && !closingForm._premiumComposer.Enabled && !pendingSend.IsCompleted,
                "Synthetic send remains pending while the editor is disabled, without a real HTTP endpoint or POST");
            closingForm.Dispose();
            closingHandler.Response.TrySetResult(HistoryQaHandler.Json(Array.Empty<ChatUser>()));
            HistoryQaPump(pendingSend);
            Require(closingForm.IsDisposed && !closingForm._sending && !closingForm._premiumComposer.Enabled &&
                pendingSend.IsCompletedSuccessfully && closingHandler.Calls == 1,
                "A send finishing after window disposal releases busy state without re-enabling/focusing the disposed editor");
        }
        return checks;
    }

    private sealed class SendCloseQaHandler : HttpMessageHandler
    {
        internal readonly TaskCompletionSource<HttpResponseMessage> Response = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Requested;
        internal int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            if (!request.RequestUri!.AbsolutePath.EndsWith("/members", StringComparison.Ordinal))
                throw new InvalidOperationException("The close-during-send fixture must not send a real or synthetic message POST.");
            Requested = true;
            // Deliberately model a handler completing after disposal/cancellation.
            // The fixture always releases this one response before it is destroyed.
            return Response.Task;
        }
    }
}
