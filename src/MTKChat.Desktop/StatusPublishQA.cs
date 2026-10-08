using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using MTKChat.Contracts;
using MTKChat.Cryptography;
using DevExpress.XtraEditors;

namespace MTKChat.Desktop;

internal static class StatusPublishQA
{
    internal static IReadOnlyList<string> Verify(string directory) => MainForm.VerifyStatusPublish(directory);
}

internal sealed partial class MainForm
{
    internal static IReadOnlyList<string> VerifyStatusPublish(string directory)
    {
        Directory.CreateDirectory(directory);
        using var context = new HistoryQaUiContext();
        using var authorIdentity = DeviceIdentity.Create();
        using var peerIdentity = DeviceIdentity.Create();
        var author = new ChatUser(Guid.NewGuid(), "Durum yazarı", "", false, null);
        var peer = new ChatUser(Guid.NewGuid(), "Seçilen kişi", "", false, null);
        var checks = new List<string>();
        void Require(bool condition, string name)
        { if (!condition) throw new InvalidOperationException("Status publish QA: " + name); checks.Add(name); }
        DeviceKeyBundle Bundle(ChatUser person, DeviceIdentity identity) => new(person.Id, Guid.NewGuid(), identity.ExportEncryptionPublicKey(), identity.ExportSigningPublicKey());
        var recipients = new[] { Bundle(author, authorIdentity), Bundle(peer, peerIdentity) };
        SendStatusRequest EncryptDraft(StatusDraft draft) => StatusCryptography.Encrypt(draft.Content, draft.Kind, Guid.NewGuid(), author.Id,
            DateTimeOffset.UtcNow, recipients, authorIdentity.SigningKey);
        var prepareCalls = 0;
        var plaintextBuffers = new List<byte[]>();
        Task<SendStatusRequest> Prepare(StatusDraft draft, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); prepareCalls++; plaintextBuffers.Add(draft.Content);
            Require(draft.Audience.SequenceEqual([peer.Id]), "Preparation freezes only the explicitly selected peer, not the author or directory");
            return Task.FromResult(EncryptDraft(draft));
        }
        using var handler = new StatusPublishQaHandler(author.Id);
        using var api = new ChatApiClient("https://status-publish-qa.invalid/chat/", handler);

        // First upload is held, then fails: neither duplicate clicks nor a retry
        // may prepare a different client ID or replay a POST automatically.
        using (var composer = new StatusComposerForm(api, [peer], null, Prepare) { ShowInTaskbar = false })
        {
            composer.Show(); Pump(composer);
            composer.SelectForQa(peer.Id, true); composer.TextForQa("  İlk paylaşım 😊  ");
            handler.Hold = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            var posting = composer.PublishForQa();
            HistoryQaUntil(() => handler.Requests.Count == 1); Pump(composer);
            Require(composer.BusyForQa && !composer.PublishEnabledForQa && prepareCalls == 1,
                "One publish freezes the composer while one real encrypted POST is pending");
            Require(composer.PeopleForQa.Enabled == false && Controls(composer).OfType<MemoEdit>().Single().Enabled == false &&
                Controls(composer).OfType<ModernButton>().Where(button => button.Visible && button.Text is "Vazgeç" or "Görsel seç" or "Paylaşılıyor...").All(button => !button.Enabled),
                "Pending publish disables audience, draft, photo, cancel and actual publish controls");
            var frozenRequest = composer.PreparedForQa;
            var frozenAudience = composer.AudienceForQa.ToArray();
            var frozenText = Controls(composer).OfType<MemoEdit>().Single().Text;
            Controls(composer).OfType<ModernButton>().Single(button => button.Text == "Paylaşılıyor...").PerformClick();
            HistoryQaPump(composer.PublishForQa()); composer.SelectForQa(peer.Id, false); composer.Close(); Pump(composer);
            Require(handler.Requests.Count == 1 && prepareCalls == 1 && composer.Visible && composer.BusyForQa &&
                composer.AudienceForQa.SequenceEqual(frozenAudience) && Controls(composer).OfType<MemoEdit>().Single().Text == frozenText,
                "A second actual click/direct call, audience change and Close cannot duplicate or mutate a pending upload");
            Require(frozenRequest is not null && frozenRequest.Payloads.Count == 2 && frozenRequest.Payloads.All(payload =>
                payload.RecipientId == author.Id || payload.RecipientId == peer.Id) && !handler.RawBodies[0].Contains("İlk paylaşım", StringComparison.Ordinal),
                "Wire JSON contains only one encrypted body and author/selected-peer envelopes, never clear text");
            VerifyDecryption(handler.Requests[0], "İlk paylaşım 😊");
            handler.Hold.SetResult(StatusPublishQaHandler.Failure(HttpStatusCode.ServiceUnavailable));
            HistoryQaPump(posting); handler.Hold = null; Pump(composer);
            Require(!composer.Published && !composer.BusyForQa && composer.Visible && composer.PublishEnabledForQa &&
                composer.FeedbackForQa.Length > 0 && Controls(composer).OfType<MemoEdit>().Single().Text == frozenText &&
                composer.AudienceForQa.SequenceEqual(frozenAudience) && ReferenceEquals(composer.PreparedForQa, frozenRequest),
                "503 failure preserves draft/audience/prepared encrypted request and offers an explicit retry without claiming success");
            Require(handler.Requests.Count == 1 && plaintextBuffers[0].All(value => value == 0),
                "POST is never automatically retried and the temporary plaintext buffer is zeroed after failure");
            handler.Status = HttpStatusCode.OK;
            HistoryQaPump(composer.PublishForQa());
            Require(composer.Published && !composer.Visible && handler.Requests.Count == 2 && prepareCalls == 1 &&
                handler.Requests[0].ClientStatusId == handler.Requests[1].ClientStatusId && handler.RawBodies[0] == handler.RawBodies[1],
                "A successful explicit retry uses the exact same client ID/body/envelopes and closes after one acknowledged publish");
            VerifyDecryption(handler.Requests[1], "İlk paylaşım 😊");
        }

        var beforeEdited = handler.Requests.Count;
        handler.Status = HttpStatusCode.ServiceUnavailable;
        using (var edited = new StatusComposerForm(api, [peer], null, Prepare) { ShowInTaskbar = false })
        {
            edited.Show(); edited.SelectForQa(peer.Id, true); edited.TextForQa("Başlangıç taslağı");
            HistoryQaPump(edited.PublishForQa());
            var oldClientId = edited.PreparedForQa!.ClientStatusId;
            edited.TextForQa("Kullanıcı açıkça yeni bir taslak yazdı");
            Require(edited.PreparedForQa is null && edited.PublishEnabledForQa,
                "An explicit draft edit after a failed upload invalidates only that cached encrypted request");
            HistoryQaPump(edited.PublishForQa());
            Require(!edited.Published && handler.Requests.Count == beforeEdited + 2 && handler.Requests[^1].ClientStatusId != oldClientId,
                "A changed draft gets a fresh client ID rather than conflicting with the failed original upload");
            VerifyDecryption(handler.Requests[^1], "Kullanıcı açıkça yeni bir taslak yazdı");
            Require(plaintextBuffers.All(bytes => bytes.All(value => value == 0)),
                "All completed publish attempts zero their temporary plaintext buffers even when the network rejects them");
            edited.Hide();
        }

        handler.Status = HttpStatusCode.OK;
        var corruptions = new (string Name, Func<StoredStatus, SendStatusRequest, StoredStatus> Change)[]
        {
            ("wrong cryptographic scope", (status, _) => status with { ScopeId = Guid.NewGuid() }),
            ("peer envelope under author ID", (status, request) => status with { Payload = request.Payloads.Single(payload => payload.RecipientId == peer.Id) }),
            ("selected peer falsely returned as author", (status, request) => status with { SenderId = peer.Id, Payload = request.Payloads.Single(payload => payload.RecipientId == peer.Id) }),
            ("changed shared ciphertext", (status, _) => status with { Ciphertext = Convert.ToBase64String([1, 2, 3]) }),
            ("different client ID", (status, _) => status with { ClientStatusId = Guid.NewGuid() })
        };
        foreach (var corruption in corruptions)
        {
            handler.Transform = corruption.Change;
            using var malformed = new StatusComposerForm(api, [peer], null, Prepare) { ShowInTaskbar = false };
            malformed.Show(); malformed.SelectForQa(peer.Id, true); malformed.TextForQa("Yanıtın sahibi ve şifreli bağlamı doğrulanmalı");
            HistoryQaPump(malformed.PublishForQa()); Pump(malformed);
            Require(!malformed.Published && malformed.Visible && !malformed.BusyForQa && malformed.FeedbackForQa.Contains("doğrulanamadı", StringComparison.Ordinal),
                "Malformed success response is rejected without discarding the draft: " + corruption.Name);
            malformed.Hide();
        }
        handler.Transform = null;

        handler.Hold = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (var disposing = new StatusComposerForm(api, [peer], null, Prepare) { ShowInTaskbar = false })
        {
            disposing.Show(); disposing.SelectForQa(peer.Id, true); disposing.TextForQa("Kapatılan pencereye geç yanıt dönmemeli");
            var pending = disposing.PublishForQa(); HistoryQaUntil(() => disposing.BusyForQa && handler.Pending > 0);
            var requestCount = handler.Requests.Count;
            disposing.Dispose(); HistoryQaPump(pending);
            Require(!disposing.Published && handler.Requests.Count == requestCount && handler.Canceled > 0,
                "Disposing a composer during POST cancels completion and cannot publish success to a closed window");
        }
        handler.Hold.TrySetResult(new HttpResponseMessage(HttpStatusCode.OK)); handler.Hold = null;

        var longText = string.Join("\r\n", Enumerable.Range(1, 45).Select(number => $"Satır {number:00}: erişilebilir uzun durum."));
        Require(longText.Length <= 2000, "Long-text scrolling fixture stays inside the actual status text limit");
        using (var scrollComposer = new StatusComposerForm(api, [peer], null, Prepare) { ShowInTaskbar = false })
        {
            scrollComposer.Show(); scrollComposer.TextForQa(longText); Pump(scrollComposer);
            VerifyMemoScroll(Controls(scrollComposer).OfType<MemoEdit>().Single(), "Composer"); scrollComposer.Hide();
        }
        var viewerMetadata = new StoredStatus(Guid.NewGuid(), Guid.NewGuid(), StatusProtocol.ScopeId, author.Id, "text",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(24), "", new EncryptedPayload("synthetic", "", "", "", "", "", author.Id));
        using (var scrollViewer = new StatusViewerForm(author, viewerMetadata, Encoding.UTF8.GetBytes(longText)) { ShowInTaskbar = false })
        {
            scrollViewer.Show(); Pump(scrollViewer);
            VerifyMemoScroll(Controls(scrollViewer).OfType<MemoEdit>().Single(), "Viewer"); scrollViewer.Hide();
        }
        Require(handler.UnexpectedRequests == 0 && handler.Requests.All(request => request.Payloads.Count == 2) && handler.Pending == 0,
            "Publish QA uses only in-process POST fixtures, ephemeral keys and the explicitly selected two-person encrypted audience");
        return checks;

        void VerifyDecryption(SendStatusRequest request, string expected)
        {
            foreach (var (person, identity) in new[] { (author, authorIdentity), (peer, peerIdentity) })
            {
                var stored = handler.Stored(request, person.Id);
                var plaintext = StatusCryptography.Decrypt(stored, person.Id, identity.EncryptionKey, authorIdentity.ExportSigningPublicKey());
                try { Require(Encoding.UTF8.GetString(plaintext) == expected, "Actual encrypted POST round-trips for " + (person.Id == author.Id ? "author" : "selected peer")); }
                finally { CryptographicOperations.ZeroMemory(plaintext); }
            }
        }
        void Pump(Form form)
        { for (var i = 0; i < 3; i++) { context.AssertOwner(); form.PerformLayout(); Application.DoEvents(); form.Update(); } }
        void VerifyMemoScroll(MemoEdit memo, string scope)
        {
            Require(memo.Properties.ScrollBars == ScrollBars.None && memo.Text == longText,
                scope + " removes white native rails without truncating long status text");
            var boxProperty = memo.GetType().GetProperty("MaskBox", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var native = boxProperty?.GetValue(memo) as Control ?? Controls(memo).OfType<TextBoxBase>().FirstOrDefault();
            Require(native is not null, scope + " exposes its owned native text control for the accessibility scroll probe");
            memo.Focus(); memo.SelectionStart = 0; memo.SelectionLength = 0;
            StatusMemoSend(native!.Handle, 0x00b1, 0, 0); StatusMemoSend(native.Handle, 0x00b7, 0, 0); Application.DoEvents();
            var first = (int)StatusMemoSend(native.Handle, 0x00ce, 0, 0);
            memo.SelectionStart = memo.Text.Length; memo.SelectionLength = 0;
            StatusMemoSend(native.Handle, 0x00b1, memo.Text.Length, memo.Text.Length); StatusMemoSend(native.Handle, 0x00b7, 0, 0); Application.DoEvents();
            var end = (int)StatusMemoSend(native.Handle, 0x00ce, 0, 0);
            Require(first == 0 && end > first && memo.SelectionStart == memo.Text.Length,
                scope + " caret navigation scrolls to the final offscreen line even while scrollbars are hidden");
            var style = StatusMemoStyle(native.Handle, -16);
            Require((style & 0x00300000) == 0, scope + " owned native text window has neither WS_VSCROLL nor WS_HSCROLL rails");
        }
        static IEnumerable<Control> Controls(Control parent)
        { foreach (Control child in parent.Controls) { yield return child; foreach (var nested in Controls(child)) yield return nested; } }
    }

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern nint StatusMemoSend(nint window, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int StatusMemoStyle(nint window, int index);

    private sealed class StatusPublishQaHandler(Guid author) : HttpMessageHandler
    {
        internal List<SendStatusRequest> Requests { get; } = [];
        internal List<string> RawBodies { get; } = [];
        internal HttpStatusCode Status = HttpStatusCode.OK;
        internal TaskCompletionSource<HttpResponseMessage>? Hold;
        internal Func<StoredStatus, SendStatusRequest, StoredStatus>? Transform;
        internal int Pending, Canceled, UnexpectedRequests;
        internal StoredStatus Stored(SendStatusRequest request, Guid viewer) => new(Guid.NewGuid(), request.ClientStatusId,
            StatusProtocol.ScopeId, author, request.Kind, request.CreatedAt, request.CreatedAt.AddHours(24), request.Ciphertext,
            request.Payloads.Single(payload => payload.RecipientId == viewer));
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.Method != HttpMethod.Post || request.RequestUri!.AbsolutePath != "/chat/api/statuses")
            { UnexpectedRequests++; throw new InvalidOperationException("Publish QA requested an unexpected network route."); }
            RawBodies.Add(await request.Content!.ReadAsStringAsync(token));
            var payload = (await request.Content.ReadFromJsonAsync<SendStatusRequest>(cancellationToken: token))!;
            Requests.Add(payload);
            if (Hold is { } hold)
            {
                Pending++;
                try { return await hold.Task.WaitAsync(token).ConfigureAwait(false); }
                catch (OperationCanceledException) { Canceled++; throw; }
                finally { Pending--; }
            }
            if (Status != HttpStatusCode.OK) return Failure(Status);
            var stored = Stored(payload, author);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(Transform?.Invoke(stored, payload) ?? stored) };
        }
        internal static HttpResponseMessage Failure(HttpStatusCode status) => new(status)
        { Content = JsonContent.Create(new ApiError("synthetic_publish_failure", "Sentetik yayın yanıtı alınamadı.")) };
    }
}
