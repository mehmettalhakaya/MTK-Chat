using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MTKChat.Contracts;
using MTKChat.Cryptography;

namespace MTKChat.Desktop;

internal sealed partial class MainForm
{
    // Execute the genuine send method against an in-memory HTTP handler and
    // decrypt its accepted envelopes with temporary recipient keys. The handler
    // never persists or forwards a message; the probe prints only check names.
    internal static IReadOnlyList<string> VerifySendEncryptionRecipients()
    {
        using var context = new HistoryQaUiContext();
        using var peerIdentity = DeviceIdentity.Create();
        using var handler = new SendEncryptionQaHandler();
        using var api = new ChatApiClient("https://qa.invalid/", handler,
            new ChatRequestPolicy { RetryDelay = TimeSpan.Zero });
        using var form = new MainForm(snapshotMode: true, api);
        form.Show(); form.PopulateSnapshot(); Application.DoEvents();
        var account = form._session!.User;
        var ready = new ChatUser(Guid.NewGuid(), "Hazır alıcı", "", false, null);
        var absent = new ChatUser(Guid.NewGuid(), "Henüz giriş yapmamış alıcı", "", false, null);
        handler.Actor = account.Id;
        handler.Members = [account, ready, absent];
        handler.Devices[account.Id] = new(account.Id, Guid.NewGuid(),
            form._identity.ExportEncryptionPublicKey(), form._identity.ExportSigningPublicKey());
        handler.Devices[ready.Id] = new(ready.Id, Guid.NewGuid(),
            peerIdentity.ExportEncryptionPublicKey(), peerIdentity.ExportSigningPublicKey());
        var errors = new List<string>();
        form._errorObserverForQa = errors.Add;
        var checks = new List<string>();
        void Require(bool valid, string description)
        {
            HistoryQaAssertUiThread();
            if (!valid) throw new InvalidOperationException("Send encryption UI QA: " + description);
            checks.Add(description);
        }
        void Select(ConversationSummary room)
        {
            form._conversationVersion++;
            form._selectedConversation = room;
            form.RefreshEncryptionRecipientStatus();
        }
        bool Send(byte[] bytes)
        {
            var task = form.SendBytesAsync(bytes, "text");
            HistoryQaPump(task);
            return task.GetAwaiter().GetResult();
        }
        var normal = form._selectedConversation! with
        {
            Id = Guid.NewGuid(), Title = "Normal grup", Participants = handler.Members
        };
        Select(normal);
        var blocked = Encoding.UTF8.GetBytes("Gönderilmeyecek sentetik mesaj 😊");
        Require(!Send(blocked) && handler.Posts == 0,
            "A regular group with a missing recipient key refuses the actual send before POST");
        Require(blocked.All(value => value == 0) && errors.Single().Contains(absent.DisplayName, StringComparison.Ordinal),
            "Refused send clears its plaintext buffer and identifies the missing recipient without a secret");
        Require(form._securityStatus.Text == "Alıcı eksik" && !form._recipientKeyReports[normal.Id].SendAccepted,
            "Rejected regular-group send keeps a truthful not-accepted recipient status");
        Select(normal with { Id = Guid.NewGuid(), Kind = "direct", Title = "Özel sohbet" });
        Require(!Send(Encoding.UTF8.GetBytes("Özel sohbet sentetik mesaj")) && handler.Posts == 0,
            "A direct conversation also refuses a missing recipient rather than bypassing encryption");

        var lounge = normal with { Id = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), Title = "Lounge" };
        Select(lounge);
        const string body = "Şifreli sentetik emoji mesajı 😊❤️";
        var bytes = Encoding.UTF8.GetBytes(body);
        var errorsBefore = errors.Count;
        Require(Send(bytes) && handler.Posts == 1 && errors.Count == errorsBefore,
            "The existing Lounge exception accepts encrypted envelopes only for registered recipients");
        var accepted = handler.LastSend!;
        Require(accepted.ConversationId == lounge.Id && accepted.Payloads.Count == 2 &&
            accepted.Payloads.Select(payload => payload.RecipientId).Order().SequenceEqual(new[] { account.Id, ready.Id }.Order()) &&
            accepted.Payloads.All(payload => payload.Algorithm == MessageCryptography.Algorithm),
            "Lounge payload recipients exclude the missing user and every included envelope uses the E2EE algorithm");
        Require(bytes.All(value => value == 0) && !handler.LastJson!.Contains(body, StringComparison.Ordinal) &&
            !handler.LastJson.Contains(JsonSerializer.Serialize(body), StringComparison.Ordinal),
            "The actual HTTP DTO contains no plaintext message and the send input is zeroed");
        foreach (var payload in accepted.Payloads)
        {
            var identity = payload.RecipientId == account.Id ? form._identity : peerIdentity;
            var plaintext = MessageCryptography.Decrypt(payload, accepted.ClientMessageId, accepted.ConversationId,
                account.Id, accepted.CreatedAt, identity.EncryptionKey, form._identity.ExportSigningPublicKey());
            try
            {
                Require(Encoding.UTF8.GetString(plaintext) == body,
                    payload.RecipientId == account.Id
                        ? "The sender's accepted envelope authenticates and decrypts to exact original Unicode"
                        : "The other user's accepted envelope authenticates and decrypts to exact original Unicode");
            }
            finally { CryptographicOperations.ZeroMemory(plaintext); }
        }
        Require(form._securityStatus.Text == "Alıcı eksik" &&
            form._recipientKeyReports[lounge.Id].OmittedFromAcceptedSend.Single().Id == absent.Id,
            "Accepted Lounge send reports the actual omitted recipient rather than claiming delivery to all");

        // A room switch during POST acceptance must not apply the old result to
        // the current header or replace a report after a newer selection version.
        handler.HoldPost = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var lateBytes = Encoding.UTF8.GetBytes("Geç kabul edilen sentetik mesaj");
        var lateSend = form.SendBytesAsync(lateBytes, "text");
        HistoryQaUntil(() => handler.Posts == 2);
        var heldRequest = handler.LastSend!;
        var pendingReport = form._recipientKeyReports[lounge.Id];
        Select(normal with { Id = Guid.NewGuid(), Title = "Yeni seçilen sohbet", Participants = [account, ready] });
        var newDescription = form._securityStatus.AccessibleDescription;
        handler.HoldPost.SetResult(SendEncryptionQaHandler.Stored(heldRequest, account.Id));
        HistoryQaPump(lateSend); handler.HoldPost = null;
        Require(lateSend.GetAwaiter().GetResult() && form._securityStatus.AccessibleDescription == newDescription &&
            ReferenceEquals(form._recipientKeyReports[lounge.Id], pendingReport),
            "A real send accepted after room selection cannot replace the active or stored scoped status");
        Require(lateBytes.All(value => value == 0), "Late accepted send still clears its independent plaintext buffer");

        // A changed account must stop work even if the membership read finishes
        // late; it may never sign/send under the newly selected account ID.
        Select(normal with { Participants = [account, ready] });
        handler.Members = [account, ready];
        handler.HoldMembers = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldAccountBytes = Encoding.UTF8.GetBytes("Eski oturum sentetik mesajı");
        var oldAccountSend = form.SendBytesAsync(oldAccountBytes, "text");
        HistoryQaUntil(() => handler.HeldMembersStarted);
        var priorPosts = handler.Posts;
        form._session = form._session with { User = account with { Id = Guid.NewGuid() } };
        form.RefreshEncryptionRecipientStatus();
        handler.HoldMembers.SetResult(SendEncryptionQaHandler.Json(handler.Members));
        HistoryQaPump(oldAccountSend); handler.HoldMembers = null;
        Require(!oldAccountSend.GetAwaiter().GetResult() && handler.Posts == priorPosts &&
            oldAccountBytes.All(value => value == 0),
            "An account change while membership is loading aborts the real send without a cross-account POST");
        Require(form._recipientKeyReports.Count == 0 && form._securityStatus.Text == "● Şifreli",
            "Aborted old-account send cannot restore old recipient names into the new account");
        Require(handler.UnexpectedRequests == 0, "The encryption probe uses only synthetic members, public-device, history and message endpoints");
        form.Hide();
        return checks;
    }

    private sealed class SendEncryptionQaHandler : HttpMessageHandler
    {
        internal Guid Actor;
        internal ChatUser[] Members = [];
        internal readonly Dictionary<Guid, DeviceKeyBundle> Devices = new();
        internal int Posts, UnexpectedRequests;
        internal string? LastJson;
        internal SendMessageRequest? LastSend;
        internal TaskCompletionSource<HttpResponseMessage>? HoldPost, HoldMembers;
        internal bool HeldMembersStarted;
        internal static HttpResponseMessage Json<T>(T value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
        internal static HttpResponseMessage Stored(SendMessageRequest input, Guid actor) => Json(new StoredMessage(
            Guid.NewGuid(), input.ClientMessageId, input.ConversationId, actor, input.Kind, input.CreatedAt,
            input.ExpiresAt, false, input.Payloads, input.Attachment));
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var parts = request.RequestUri!.AbsolutePath.Trim('/').Split('/');
            if (request.Method == HttpMethod.Get && parts[^1] == "members")
            {
                if (HoldMembers is { } held) { HeldMembersStarted = true; return await held.Task.ConfigureAwait(false); }
                return Json(Members);
            }
            if (request.Method == HttpMethod.Get && parts[^1] == "device")
                return Devices.TryGetValue(Guid.Parse(parts[^2]), out var device)
                    ? Json(device) : new HttpResponseMessage(HttpStatusCode.NotFound);
            if (request.Method == HttpMethod.Get && parts[^1] == "messages") return Json(Array.Empty<StoredMessage>());
            if (request.Method == HttpMethod.Post && parts[^1] == "messages")
            {
                LastJson = await request.Content!.ReadAsStringAsync(token).ConfigureAwait(false);
                LastSend = JsonSerializer.Deserialize<SendMessageRequest>(LastJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
                Posts++;
                if (HoldPost is { } held) return await held.Task.ConfigureAwait(false);
                return Stored(LastSend, Actor);
            }
            UnexpectedRequests++;
            throw new InvalidOperationException("Encryption QA forbids unrelated HTTP requests.");
        }
    }
}
