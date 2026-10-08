using System.Net;
using System.Net.Http.Json;
using System.Text;
using MTKChat.Contracts;
using MTKChat.Cryptography;

namespace MTKChat.Desktop;

internal sealed partial class MainForm
{
    // These probes run the real selection, decrypt, editor, row and retry-button paths
    // against an in-process HTTP handler. No real account, remote request, send,
    // clipboard or microphone is used. Each sender signs genuinely encrypted text.
    internal static IReadOnlyList<string> VerifyHistoryLoading()
    {
        // The CLI probe uses DoEvents instead of Application.Run. Own its context
        // for the entire fixture sequence: disposing the last test form must not
        // make a later awaited continuation create controls on a pool thread.
        using var uiContext = new HistoryQaUiContext();
        var checks = new List<string>();
        void Require(bool valid, string check)
        {
            HistoryQaAssertUiThread();
            if (!valid) throw new InvalidOperationException("History UI QA: " + check);
            checks.Add(check);
        }

        using (var fixture = new HistoryQaFixture())
        {
            var form = fixture.Form;
            var room = fixture.InitialRoom;
            var message = fixture.Message(room, fixture.FirstSender, "Inbox çalışmasa da seçili sohbet açılır.");
            fixture.Handler.Messages[room.Id] = [message];
            fixture.Handler.FailInbox = true;
            form._premiumComposer.Text = "Korunacak taslak 😊";
            HistoryQaPump(form.RefreshBackgroundAsync());
            Require(HistoryQaHasText(form._messageList, "Inbox çalışmasa"),
                "A failed delivery inbox cannot starve the selected conversation's history");
            Require(fixture.Handler.HistoryCalls.GetValueOrDefault(room.Id) > 0 && fixture.Handler.InboxCalls > 0,
                "History is loaded and the failing auxiliary inbox is still attempted");
            Require(fixture.Dialogs == 0 && form._premiumComposer.Text == "Korunacak taslak 😊",
                "Auxiliary failure stays non-modal and preserves the message draft");
        }

        using (var fixture = new HistoryQaFixture())
        {
            var form = fixture.Form;
            var room = fixture.AddRoom("İlk yükleme");
            var message = fixture.Message(room, fixture.FirstSender, "Yeniden dene düğmesi mesajı gerçekten yükledi.");
            fixture.Handler.Messages[room.Id] = [message];
            fixture.Handler.FailedHistories.Add(room.Id);
            HistoryQaPump(form.SelectConversationAsync(room, fixture.Card(room), silent: true));
            Require(!HistoryQaHasText(form._messageList, "Mesajlar yükleniyor"),
                "An initial history GET failure cannot leave the loading state indefinitely");
            Require(HistoryQaHasText(form._messageList, "Mesajlar yüklenemedi"),
                "Initial loading failure is replaced by an explicit history error state");
            var retry = HistoryQaControls(form._messageList).OfType<Button>().SingleOrDefault(button => button.Text == "Yeniden dene");
            Require(retry is { Enabled: true }, "The history error state has an enabled retry button");
            fixture.Handler.FailedHistories.Remove(room.Id);
            var before = fixture.Handler.HistoryCalls.GetValueOrDefault(room.Id);
            retry!.PerformClick();
            HistoryQaUntil(() => HistoryQaHasText(form._messageList, "düğmesi mesajı gerçekten yükledi"));
            Require(fixture.Handler.HistoryCalls.GetValueOrDefault(room.Id) > before &&
                form._messageList.Controls.OfType<MessageRow>().Single().CanMarkRead,
                "Clicking the real retry button makes a new request and decrypts recovered history");
            Require(fixture.Dialogs == 0, "Initial failure and retry recovery do not show a modal dialog");
        }

        using (var fixture = new HistoryQaFixture())
        {
            var form = fixture.Form;
            var room = fixture.AddRoom("İki gönderen");
            var unavailable = fixture.Message(room, fixture.FirstSender, "Anahtarı sonra dönen mesaj.");
            var healthy = fixture.Message(room, fixture.SecondSender, "Diğer gönderenin sağlıklı mesajı görünür.");
            // Multiple messages by the failed sender must not create N separate
            // network deadlines. The independent sender must still be decrypted.
            var unavailableAgain = fixture.Message(room, fixture.FirstSender, "Aynı gönderenin ikinci mesajı.");
            fixture.Handler.Messages[room.Id] = [unavailable, healthy, unavailableAgain];
            fixture.Handler.FailedKeys.Add(fixture.FirstSender.Id);
            HistoryQaPump(form.SelectConversationAsync(room, fixture.Card(room), silent: true));
            var rows = form._messageList.Controls.OfType<MessageRow>().ToDictionary(row => row.MessageId);
            Require(rows.Count == 3 && rows[healthy.Id].CanMarkRead && HistoryQaHasText(rows[healthy.Id], "sağlıklı mesajı görünür"),
                "One sender's failed key lookup does not hide another sender's healthy message");
            Require(!rows[unavailable.Id].CanMarkRead && !rows[unavailableAgain.Id].CanMarkRead &&
                HistoryQaHasText(rows[unavailable.Id], "Mesajın anahtarı yüklenemedi"),
                "Retryable key placeholders cannot generate read acknowledgements");
            Require(form._renderFingerprint is null,
                "A history containing retryable key placeholders is not marked completely rendered");
            Require(fixture.Handler.KeyCalls.GetValueOrDefault(fixture.FirstSender.Id) <= 2,
                "Repeated messages of one unavailable sender share one bounded key attempt per rendering pass");
            fixture.Handler.FailedKeys.Clear();
            form._nextNetworkPoll = default;
            HistoryQaPump(form.RefreshBackgroundAsync());
            rows = form._messageList.Controls.OfType<MessageRow>().ToDictionary(row => row.MessageId);
            Require(rows.Count == 3 && rows.Values.All(row => row.CanMarkRead) &&
                HistoryQaHasText(rows[unavailable.Id], "Anahtarı sonra dönen mesaj") &&
                HistoryQaHasText(rows[unavailableAgain.Id], "ikinci mesajı"),
                "Unchanged history replaces retryable key placeholders after the key endpoint recovers");
            Require(form._renderFingerprint is not null && fixture.Dialogs == 0,
                "Recovered mixed-sender history commits its fingerprint without a modal dialog");
        }

        using (var fixture = new HistoryQaFixture())
        {
            var form = fixture.Form;
            var room = fixture.AddRoom("Aynı sohbet tıklaması");
            fixture.Handler.Messages[room.Id] = [fixture.Message(room, fixture.FirstSender, "Tek devam eden yükleme tamamlandı.")];
            var hold = fixture.Handler.Hold(room.Id, honorCancellation: true);
            var pending = form.SelectConversationAsync(room, fixture.Card(room), silent: true);
            HistoryQaUntil(() => fixture.Handler.HistoryCalls.GetValueOrDefault(room.Id) == 1);
            var version = form._conversationVersion;
            HistoryQaPump(form.SelectConversationAsync(room, fixture.Card(room), silent: true));
            Require(form._conversationVersion == version,
                "Clicking the selected card during a pending load does not invalidate its conversation version");
            Require(fixture.Handler.HistoryCalls.GetValueOrDefault(room.Id) == 1 && !pending.IsCompleted,
                "Clicking the same pending card neither duplicates nor discards its history request");
            hold.Response.SetResult(HistoryQaHandler.Json(fixture.Handler.Messages[room.Id]));
            HistoryQaPump(pending);
            Require(HistoryQaHasText(form._messageList, "Tek devam eden yükleme tamamlandı"),
                "The original in-flight history can still finish after a same-card click");
        }

        // Cover cooperative cancellation and a server/handler returning success or
        // failure late after ignoring cancellation. B must load before A is released.
        foreach (var completion in new[] { "cancel", "late_success", "late_failure" })
        {
            using var fixture = new HistoryQaFixture();
            var form = fixture.Form;
            var roomA = fixture.AddRoom("Yavaş A");
            var roomB = fixture.AddRoom("Yeni B");
            fixture.Handler.Messages[roomA.Id] = [fixture.Message(roomA, fixture.FirstSender, "Eski A içeriği gösterilmemeli.")];
            fixture.Handler.Messages[roomB.Id] = [fixture.Message(roomB, fixture.SecondSender, "B hemen açıldı ve doğru kaldı.")];
            var hold = fixture.Handler.Hold(roomA.Id, honorCancellation: completion == "cancel");
            var pendingA = form.SelectConversationAsync(roomA, fixture.Card(roomA), silent: true);
            HistoryQaUntil(() => fixture.Handler.HistoryCalls.GetValueOrDefault(roomA.Id) == 1);
            HistoryQaPump(form.SelectConversationAsync(roomB, fixture.Card(roomB), silent: true));
            Require(form._selectedConversation?.Id == roomB.Id && HistoryQaHasText(form._messageList, "B hemen açıldı") &&
                !HistoryQaHasText(form._messageList, "Eski A içeriği"),
                $"Selecting B starts its history immediately while A is still pending ({completion})");
            var nextPoll = form._nextNetworkPoll;
            var failureCount = form._networkFailures;
            if (completion == "late_success") hold.Response.SetResult(HistoryQaHandler.Json(fixture.Handler.Messages[roomA.Id]));
            else if (completion == "late_failure") hold.Response.SetException(new HttpRequestException("Scripted late history failure"));
            HistoryQaPump(pendingA);
            Require(hold.Cancellations > 0 && form._selectedConversation?.Id == roomB.Id &&
                HistoryQaHasText(form._messageList, "B hemen açıldı") &&
                form._messageList.Controls.OfType<MessageRow>().All(row => row.ConversationId == roomB.Id),
                $"A is cancelled and its late completion cannot replace B's message rows ({completion})");
            Require(form._nextNetworkPoll == nextPoll && form._networkFailures == failureCount && fixture.Dialogs == 0,
                $"A stale cancellation or failure cannot apply reconnect backoff or a popup to B ({completion})");
        }

        using (var fixture = new HistoryQaFixture())
        {
            var form = fixture.Form;
            var room = fixture.AddRoom("Toplam yükleme süresi");
            form._historyLoadTimeout = TimeSpan.FromMilliseconds(150);
            fixture.Handler.Hold(room.Id, honorCancellation: true);
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            HistoryQaPump(form.SelectConversationAsync(room, fixture.Card(room), silent: true));
            Require(HistoryQaHasText(form._messageList, "Mesajlar yüklenemedi") &&
                !HistoryQaHasText(form._messageList, "Mesajlar yükleniyor") && fixture.Dialogs == 0 &&
                elapsed.Elapsed < TimeSpan.FromSeconds(1),
                "A stalled initial history load has a total deadline and reaches a retryable non-modal error state");
        }

        using (var fixture = new HistoryQaFixture())
        {
            var form = fixture.Form;
            var room = fixture.AddRoom("Bozuk imzalı geçmiş");
            // Start with the correct key already cached, so every further key GET
            // is a forced refresh rather than the initial device discovery.
            form._deviceCache[fixture.FirstSender.Id] = fixture.Handler.Keys[fixture.FirstSender.Id];
            fixture.Handler.Messages[room.Id] = Enumerable.Range(0, 10).Select(index =>
            {
                var message = fixture.Message(room, fixture.FirstSender, $"Doğrulanmadan gösterilmemesi gereken içerik {index}.");
                var payload = message.Payloads.Single();
                var signature = Convert.FromBase64String(payload.Signature);
                signature[0] ^= 1; // Valid encoding and length, invalid ECDSA signature.
                return message with { Payloads = [payload with { Signature = Convert.ToBase64String(signature) }] };
            }).ToArray();
            HistoryQaPump(form.SelectConversationAsync(room, fixture.Card(room), silent: true));
            var rows = form._messageList.Controls.OfType<MessageRow>().ToArray();
            Require(fixture.Handler.KeyCalls.GetValueOrDefault(fixture.FirstSender.Id) == 1,
                "Ten invalid signatures from one cached sender trigger only one forced key refresh in the batch");
            Require(rows.Length == 10 && rows.All(row => !row.CanMarkRead && HistoryQaHasText(row, "Mesaj açılamadı")) &&
                !HistoryQaHasText(form._messageList, "Doğrulanmadan gösterilmemesi"),
                "Invalid signatures never expose plaintext or mark their ten messages as read");
        }

        using (var fixture = new HistoryQaFixture())
        {
            var form = fixture.Form;
            var room = fixture.AddRoom("Anahtar isteğinde oturum kaybı");
            fixture.Handler.Messages[room.Id] = [fixture.Message(room, fixture.FirstSender, "Yetkisiz anahtar mesajı.")];
            fixture.Handler.UnauthorizedKeys.Add(fixture.FirstSender.Id);
            form._refreshTimer.Start(); form._readTimer.Start();
            HistoryQaPump(form.SelectConversationAsync(room, fixture.Card(room), silent: true));
            Require(form.Text.Contains("Yeniden giriş gerekli", StringComparison.Ordinal) &&
                !form._refreshTimer.Enabled && !form._readTimer.Enabled && fixture.Dialogs == 0,
                "A device-key 401 stops global polling and read timers without an idle warning dialog");
            Require(fixture.Handler.KeyCalls.GetValueOrDefault(fixture.FirstSender.Id) == 1 &&
                !form._messageList.Controls.OfType<MessageRow>().Any(row => row.CanMarkRead) &&
                !HistoryQaHasText(form._messageList, "Mesajın anahtarı yüklenemedi") &&
                HistoryQaHasText(form._messageList, "Yeniden giriş gerekli"),
                "A key 401 is not retried or converted to a retry-key placeholder or a readable message");
        }

        using (var fixture = new HistoryQaFixture())
        {
            var form = fixture.Form;
            var room = fixture.AddRoom("Anahtar bekleme süresi");
            fixture.Handler.Messages[room.Id] = [fixture.Message(room, fixture.FirstSender, "Bekleyen anahtarın içeriği.")];
            form._historyLoadTimeout = TimeSpan.FromMilliseconds(150);
            var hold = fixture.Handler.HoldKey(fixture.FirstSender.Id, honorCancellation: true);
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            HistoryQaPump(form.SelectConversationAsync(room, fixture.Card(room), silent: true));
            Require(fixture.Handler.HistoryCalls.GetValueOrDefault(room.Id) == 1 &&
                fixture.Handler.KeyCalls.GetValueOrDefault(fixture.FirstSender.Id) == 1 && hold.Cancellations > 0 &&
                elapsed.Elapsed < TimeSpan.FromSeconds(1),
                "The total history deadline propagates to a stalled sender-key GET after a successful history GET");
            Require(HistoryQaHasText(form._messageList, "Mesajlar yüklenemedi") &&
                !HistoryQaHasText(form._messageList, "Mesajlar yükleniyor") &&
                !form._messageList.Controls.OfType<MessageRow>().Any(row => row.CanMarkRead) && fixture.Dialogs == 0,
                "A stalled sender-key load reaches the retryable history error state within the total load deadline");
        }
        return checks;
    }

    private static void HistoryQaPump(Task task)
    {
        HistoryQaAssertUiThread();
        HistoryQaUntil(() => task.IsCompleted);
        task.GetAwaiter().GetResult();
        HistoryQaAssertUiThread();
    }

    private static void HistoryQaUntil(Func<bool> ready)
    {
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        while (!ready())
        {
            HistoryQaAssertUiThread();
            if (elapsed.Elapsed > TimeSpan.FromSeconds(8))
                throw new InvalidOperationException("History QA task did not finish within its bounded probe deadline.");
            Application.DoEvents();
            Thread.Sleep(1);
        }
        Application.DoEvents();
        HistoryQaAssertUiThread();
    }

    private static void HistoryQaAssertUiThread()
    {
        if (SynchronizationContext.Current is not HistoryQaUiContext context)
            throw new InvalidOperationException($"History QA lost its owned UI context on thread {Environment.CurrentManagedThreadId}.");
        context.AssertOwner();
    }

    // A dedicated dispatch control avoids disposing the Application thread's
    // shared marshalling control when this scoped probe restores an earlier
    // WinForms context. No production UI or production scheduling is changed.
    private sealed class HistoryQaUiContext : SynchronizationContext, IDisposable
    {
        private readonly SynchronizationContext? _previousContext = Current;
        private readonly bool _previousAutoInstall = WindowsFormsSynchronizationContext.AutoInstall;
        private readonly int _ownerThread = Environment.CurrentManagedThreadId;
        private readonly Control _dispatcher;
        private bool _disposed;

        internal HistoryQaUiContext()
        {
            WindowsFormsSynchronizationContext.AutoInstall = false;
            try
            {
                _dispatcher = new Control();
                _ = _dispatcher.Handle;
                SetSynchronizationContext(this);
                AssertOwner();
            }
            catch
            {
                SetSynchronizationContext(_previousContext);
                try { _dispatcher?.Dispose(); }
                finally { WindowsFormsSynchronizationContext.AutoInstall = _previousAutoInstall; }
                throw;
            }
        }

        internal void AssertOwner()
        {
            if (_disposed || Environment.CurrentManagedThreadId != _ownerThread || _dispatcher.InvokeRequired ||
                !ReferenceEquals(Current, this))
                throw new InvalidOperationException($"History QA UI context owner is thread {_ownerThread}; current thread is {Environment.CurrentManagedThreadId}.");
        }

        public override void Post(SendOrPostCallback callback, object? state)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(HistoryQaUiContext));
            _dispatcher.BeginInvoke((Action)(() => InvokeOwned(callback, state)));
        }

        public override void Send(SendOrPostCallback callback, object? state)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(HistoryQaUiContext));
            if (Environment.CurrentManagedThreadId == _ownerThread) InvokeOwned(callback, state);
            else _dispatcher.Invoke((Action)(() => InvokeOwned(callback, state)));
        }

        public override SynchronizationContext CreateCopy() => this;

        private void InvokeOwned(SendOrPostCallback callback, object? state)
        {
            var previous = Current;
            SetSynchronizationContext(this);
            try { AssertOwner(); callback(state); }
            finally { SetSynchronizationContext(previous); }
        }

        public void Dispose()
        {
            if (_disposed) return;
            AssertOwner();
            _disposed = true;
            SetSynchronizationContext(_previousContext);
            try { _dispatcher.Dispose(); }
            finally { WindowsFormsSynchronizationContext.AutoInstall = _previousAutoInstall; }
        }
    }

    private static bool HistoryQaHasText(Control root, string text) =>
        root.Text.Contains(text, StringComparison.Ordinal) || root.Controls.Cast<Control>().Any(child => HistoryQaHasText(child, text));

    private static IEnumerable<Control> HistoryQaControls(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (var descendant in HistoryQaControls(child)) yield return descendant;
        }
    }

    private sealed class HistoryQaFixture : IDisposable
    {
        internal readonly HistoryQaHandler Handler = new();
        internal readonly MainForm Form;
        internal readonly ChatUser FirstSender = new(Guid.NewGuid(), "İlk QA gönderen", "first@example.invalid", false, null);
        internal readonly ChatUser SecondSender = new(Guid.NewGuid(), "İkinci QA gönderen", "second@example.invalid", false, null);
        private readonly DeviceIdentity _firstIdentity = DeviceIdentity.Create();
        private readonly DeviceIdentity _secondIdentity = DeviceIdentity.Create();
        internal readonly ConversationSummary InitialRoom;
        internal int Dialogs;

        internal HistoryQaFixture()
        {
            HistoryQaAssertUiThread();
            var api = new ChatApiClient("https://history-qa.invalid/", Handler, new ChatRequestPolicy
            {
                RetryDelay = TimeSpan.Zero,
                MetadataTimeout = TimeSpan.FromSeconds(2),
                HistoryTimeout = TimeSpan.FromSeconds(4)
            });
            Form = new MainForm(snapshotMode: true, api);
            Form.Show(); Form.PopulateSnapshot(); Application.DoEvents();
            HistoryQaAssertUiThread();
            if (Form.InvokeRequired) throw new InvalidOperationException("History QA form is not owned by its probe UI thread.");
            Form._refreshTimer.Stop(); Form._readTimer.Stop();
            Form._errorObserverForQa = _ => Dialogs++;
            InitialRoom = Form._selectedConversation! with { Participants = [Form._session!.User, FirstSender, SecondSender] };
            Form._selectedConversation = InitialRoom;
            Form._selectedConversationCard!.Tag = InitialRoom;
            Handler.Rooms[InitialRoom.Id] = InitialRoom;
            Handler.Keys[FirstSender.Id] = new DeviceKeyBundle(FirstSender.Id, Guid.NewGuid(),
                _firstIdentity.ExportEncryptionPublicKey(), _firstIdentity.ExportSigningPublicKey());
            Handler.Keys[SecondSender.Id] = new DeviceKeyBundle(SecondSender.Id, Guid.NewGuid(),
                _secondIdentity.ExportEncryptionPublicKey(), _secondIdentity.ExportSigningPublicKey());
        }

        internal ConversationSummary AddRoom(string title)
        {
            var room = InitialRoom with { Id = Guid.NewGuid(), Title = title };
            Handler.Rooms[room.Id] = room;
            Form._conversationList.Controls.Add(Form.CreateConversationCard(room));
            Form.ResizeConversationCards();
            return room;
        }

        internal RoundedPanel Card(ConversationSummary room) => Form._conversationList.Controls.OfType<RoundedPanel>()
            .Single(card => ((ConversationSummary)card.Tag!).Id == room.Id);

        internal StoredMessage Message(ConversationSummary room, ChatUser sender, string text)
        {
            var client = Guid.NewGuid();
            var at = DateTimeOffset.UtcNow;
            var identity = sender.Id == FirstSender.Id ? _firstIdentity : _secondIdentity;
            var payload = MessageCryptography.Encrypt(Encoding.UTF8.GetBytes(text), client, room.Id, sender.Id,
                Form._session!.User.Id, at, Form._identity.ExportEncryptionPublicKey(), identity.SigningKey);
            return new StoredMessage(Guid.NewGuid(), client, room.Id, sender.Id, "text", at, null, false, [payload], null);
        }

        public void Dispose()
        {
            HistoryQaAssertUiThread();
            Form.Hide(); Form.Dispose();
            _firstIdentity.Dispose(); _secondIdentity.Dispose();
            HistoryQaAssertUiThread();
        }
    }

    private sealed class HistoryQaHold(bool honorCancellation)
    {
        internal readonly TaskCompletionSource<HttpResponseMessage> Response = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Cancellations;
        internal async Task<HttpResponseMessage> Await(CancellationToken ct)
        {
            using var registration = ct.Register(() =>
            {
                Interlocked.Increment(ref Cancellations);
                if (honorCancellation) Response.TrySetCanceled(ct);
            });
            return await Response.Task.ConfigureAwait(false);
        }
    }

    private sealed class HistoryQaHandler : HttpMessageHandler
    {
        internal readonly Dictionary<Guid, ConversationSummary> Rooms = new();
        internal readonly Dictionary<Guid, StoredMessage[]> Messages = new();
        internal readonly Dictionary<Guid, DeviceKeyBundle> Keys = new();
        internal readonly Dictionary<Guid, int> HistoryCalls = new();
        internal readonly Dictionary<Guid, DateTimeOffset> PreviewAfter = new();
        internal readonly Dictionary<Guid, int> KeyCalls = new();
        internal readonly HashSet<Guid> FailedHistories = new();
        internal readonly HashSet<Guid> FailedKeys = new();
        internal readonly HashSet<Guid> UnauthorizedKeys = new();
        private readonly Dictionary<Guid, HistoryQaHold> _holds = new();
        private readonly Dictionary<Guid, HistoryQaHold> _keyHolds = new();
        internal bool FailInbox;
        internal int InboxCalls;

        internal HistoryQaHold Hold(Guid room, bool honorCancellation)
        {
            var hold = new HistoryQaHold(honorCancellation);
            _holds[room] = hold;
            return hold;
        }

        internal HistoryQaHold HoldKey(Guid user, bool honorCancellation)
        {
            var hold = new HistoryQaHold(honorCancellation);
            _keyHolds[user] = hold;
            return hold;
        }

        internal static HttpResponseMessage Json<T>(T value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var segments = request.RequestUri!.AbsolutePath.Trim('/').Split('/');
            var leaf = segments[^1];
            // Presence POST is intentionally answered locally as a harmless fixture.
            // Any message/admin/device-registration/file write is rejected outright.
            if (request.Method != HttpMethod.Get && !(request.Method == HttpMethod.Post && leaf == "presence"))
                throw new InvalidOperationException("History QA forbids this request method/route.");
            if (leaf == "inbox")
            {
                InboxCalls++;
                if (FailInbox) throw new HttpRequestException("Scripted delivery inbox failure");
                return Task.FromResult(Json(Array.Empty<StoredMessage>()));
            }
            if (leaf == "messages" && Guid.TryParse(segments[^2], out var room))
            {
                HistoryCalls[room] = HistoryCalls.GetValueOrDefault(room) + 1;
                if (FailedHistories.Contains(room)) throw new HttpRequestException("Scripted history failure");
                if (_holds.TryGetValue(room, out var hold)) return hold.Await(ct);
                var messages = Messages.GetValueOrDefault(room) ?? [];
                if (request.RequestUri.Query.StartsWith("?after=", StringComparison.Ordinal))
                {
                    var after = DateTimeOffset.Parse(Uri.UnescapeDataString(request.RequestUri.Query[7..]),
                        System.Globalization.CultureInfo.InvariantCulture);
                    PreviewAfter[room] = after;
                    messages = messages.Where(message => message.CreatedAt > after).ToArray();
                }
                return Task.FromResult(Json(messages));
            }
            if (leaf == "device" && Guid.TryParse(segments[^2], out var user))
            {
                KeyCalls[user] = KeyCalls.GetValueOrDefault(user) + 1;
                if (UnauthorizedKeys.Contains(user)) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
                    { Content = JsonContent.Create(new ApiError("unauthorized", "Fixture session expired.")) });
                if (FailedKeys.Contains(user)) throw new HttpRequestException("Scripted sender key failure");
                if (_keyHolds.TryGetValue(user, out var hold)) return hold.Await(ct);
                return Task.FromResult(Keys.TryGetValue(user, out var key) ? Json(key) :
                    new HttpResponseMessage(HttpStatusCode.NotFound) { Content = JsonContent.Create(new ApiError("no_device", "Fixture has no device.")) });
            }
            if (leaf == "conversations") return Task.FromResult(Json(Rooms.Values.ToArray()));
            if (leaf == "calls") return Task.FromResult(Json(Array.Empty<CallView>()));
            if (leaf == "presence" && Guid.TryParse(segments[^2], out room))
            {
                var people = Rooms[room].Participants.Select(user => new PresenceView(user, false, true, false, null)).ToArray();
                return Task.FromResult(Json(people));
            }
            throw new InvalidOperationException("Unexpected in-process history QA route.");
        }
    }
}
