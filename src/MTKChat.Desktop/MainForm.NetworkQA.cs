using System.Net;
using System.Net.Http.Json;
using System.Text;
using MTKChat.Contracts;
using MTKChat.Cryptography;

namespace MTKChat.Desktop;

internal sealed partial class MainForm
{
    // App-owned scripted HTTP + genuine crypto/editor/row paths. No account login,
    // real network, microphone, clipboard or send operation is used by this probe.
    internal static IReadOnlyList<string> VerifyNetworkRecovery()
    {
        // Explicitly own the dispatcher across the full multi-form QA suite.
        // UI slicing now performs genuine async continuations; a synchronization
        // context left by a previously disposed helper window is not a valid pump.
        using var context = new HistoryQaUiContext();
        using var senderIdentity = DeviceIdentity.Create();
        using var handler = new NetworkQaHandler();
        using var api = new ChatApiClient("https://qa.invalid/", handler, new ChatRequestPolicy { RetryDelay = TimeSpan.Zero });
        using var form = new MainForm(snapshotMode: true, api);
        form.Show(); form.PopulateSnapshot(); Application.DoEvents();
        var checks = new List<string>();
        var dialogs = 0;
        form._errorObserverForQa = _ => dialogs++;
        void Require(bool valid, string check)
        {
            HistoryQaAssertUiThread();
            if (!valid) throw new InvalidOperationException("Network UI QA: " + check);
            checks.Add(check);
        }
        static void Pump(Task task)
        {
            HistoryQaAssertUiThread();
            var limit = System.Diagnostics.Stopwatch.StartNew();
            while (!task.IsCompleted)
            {
                if (limit.Elapsed > TimeSpan.FromSeconds(8)) throw new InvalidOperationException("Network QA task did not finish.");
                Application.DoEvents(); Thread.Sleep(1);
            }
            task.GetAwaiter().GetResult();
            HistoryQaAssertUiThread();
        }
        var room = form._selectedConversation!;
        var sender = room.Participants.First(user => !user.IsAgent && user.Id != form._session!.User.Id);
        var at = DateTimeOffset.UtcNow;
        const string body = "Bağlantı geri geldi; mesaj çözüldü.";
        handler.Room = room;
        handler.Messages = Enumerable.Range(0, 30).Select(_ =>
        {
            var client = Guid.NewGuid();
            var payload = MessageCryptography.Encrypt(Encoding.UTF8.GetBytes(body), client, room.Id,
                sender.Id, form._session!.User.Id, at, form._identity.ExportEncryptionPublicKey(), senderIdentity.SigningKey);
            return new StoredMessage(Guid.NewGuid(), client, room.Id, sender.Id, "text", at, null, false, [payload], null);
        }).ToArray();
        handler.Device = new DeviceKeyBundle(sender.Id, Guid.NewGuid(), senderIdentity.ExportEncryptionPublicKey(), senderIdentity.ExportSigningPublicKey());
        form._premiumComposer.Text = "Kaybolmaması gereken taslak 😊";
        var previousSecurityLabel = form._securityStatus.Text;
        handler.FailKeys = true;
        Pump(form.RefreshBackgroundAsync());
        Require(dialogs == 0, "Idle device-key failure never opens a modal warning");
        Require(form._messageList.Controls.OfType<MessageRow>().Count() == 30 &&
            form._messageList.Controls.OfType<MessageRow>().All(row => !row.CanMarkRead) &&
            form._renderFingerprint is null, "Failed sender key becomes retryable unread rows and does not commit its fingerprint");
        Require(handler.KeyRequests == 2, "One failed sender lookup uses only the bounded read retry, not one failure per history row");
        Require(form._premiumComposer.Text.Contains("taslak 😊", StringComparison.Ordinal), "Background failure preserves the composer draft");
        Require(form.Text.Contains("Bağlantı yeniden", StringComparison.Ordinal) && form._securityStatus.Text == previousSecurityLabel,
            "Reconnect caption does not overwrite E2EE's security label");
        var requestCount = handler.Calls;
        Pump(form.RefreshBackgroundAsync());
        Require(handler.Calls == requestCount, "Backoff skips additional requests until its next deadline");
        Require(Enumerable.Range(1, 6).Select(NetworkRetryDelay).SequenceEqual(new[] { 3, 6, 12, 24, 30, 30 }.Select(seconds => TimeSpan.FromSeconds(seconds))),
            "Retry delays grow 3/6/12/24 seconds and cap at 30 seconds");

        handler.FailKeys = false; form._nextNetworkPoll = default;
        Pump(form.RefreshBackgroundAsync());
        Require(form._messageList.Controls.OfType<MessageRow>().Count() == 30 &&
            form._messageList.Controls.OfType<MessageRow>().All(row => row.CanMarkRead) && ContainsText(form._messageList, body),
            "Unchanged encrypted message retries successfully after the key lookup recovers");
        Require(form.Text == "MTK Chat" && form._networkFailures == 0, "A complete successful polling cycle clears reconnect status");

        // Keep the first real inbox request suspended; a second timer tick must skip.
        handler.HoldInbox = new TaskCompletionSource<HttpResponseMessage>();
        var pending = form.RefreshBackgroundAsync();
        requestCount = handler.Calls;
        Pump(form.RefreshBackgroundAsync());
        Require(form._pollCycleRunning && handler.Calls == requestCount, "Concurrent timer ticks cannot overlap a pending polling cycle");
        handler.HoldInbox.SetResult(NetworkQaHandler.Json(Array.Empty<StoredMessage>()));
        Pump(pending); handler.HoldInbox = null;

        // Reproduce timer-driven auto-selection: the room changed while no user
        // clicked anything. Fail its history GET and count any ShowError invocation.
        handler.Room = room with { Id = Guid.NewGuid(), Title = "Başka sohbet" };
        handler.FailHistory = true;
        Pump(form.LoadConversationsAsync(silent: true));
        Require(dialogs == 0, "Timer auto-selection carries silent mode into history loading");
        Require(!form._messageList.Controls.OfType<MessageRow>().Any(), "New-room failure never shows the previous room's message rows");

        handler.FailHistory = false;
        handler.Messages = []; // The newly selected room has no old-room history.
        handler.DenyInbox = true; form._nextNetworkPoll = default;
        Pump(form.RefreshBackgroundAsync());
        Require(form.Text.Contains("Yeniden giriş gerekli", StringComparison.Ordinal) &&
            !form._refreshTimer.Enabled && !form._readTimer.Enabled && dialogs == 0,
            "A 401 is not reported as a healthy connection or retried as an idle popup");
        form.Hide();
        return checks;
    }

    private static bool ContainsText(Control root, string text) =>
        root.Text.Contains(text, StringComparison.Ordinal) || root.Controls.Cast<Control>().Any(child => ContainsText(child, text));

    private sealed class NetworkQaHandler : HttpMessageHandler
    {
        internal ConversationSummary Room = null!;
        internal StoredMessage[] Messages = [];
        internal DeviceKeyBundle Device = null!;
        internal bool FailKeys, FailHistory, DenyInbox;
        internal int Calls, KeyRequests;
        internal TaskCompletionSource<HttpResponseMessage>? HoldInbox;

        internal static HttpResponseMessage Json<T>(T value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/inbox", StringComparison.Ordinal))
            {
                if (DenyInbox) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
                    { Content = JsonContent.Create(new ApiError("unauthorized", "Oturum süresi doldu.")) });
                if (HoldInbox is { } held) return held.Task.WaitAsync(ct);
                return Task.FromResult(Json(Array.Empty<StoredMessage>()));
            }
            if (path.EndsWith("/messages", StringComparison.Ordinal))
            {
                if (FailHistory) throw new HttpRequestException("QA offline history");
                return Task.FromResult(Json(Messages));
            }
            if (path.EndsWith("/device", StringComparison.Ordinal))
            {
                KeyRequests++;
                if (FailKeys) throw new HttpRequestException("QA offline key lookup");
                return Task.FromResult(Json(Device));
            }
            if (path.EndsWith("/conversations", StringComparison.Ordinal)) return Task.FromResult(Json(new[] { Room }));
            if (path.EndsWith("/calls", StringComparison.Ordinal)) return Task.FromResult(Json(Array.Empty<CallView>()));
            if (path.EndsWith("/presence", StringComparison.Ordinal)) return Task.FromResult(Json(Array.Empty<PresenceView>()));
            throw new InvalidOperationException("Unexpected QA route: " + path);
        }
    }
}
