using System.Net;
using System.Net.Http.Json;
using MTKChat.Contracts;

namespace MTKChat.Desktop;

internal sealed partial class MainForm
{
    // Synthetic public-key endpoint and UI scope tests. No server, credentials,
    // private key export, clipboard, real account login or new timer is needed.
    internal static IReadOnlyList<string> VerifyEncryptionRecipientStatus()
    {
        using var context = new HistoryQaUiContext();
        using var handler = new RecipientKeyQaHandler();
        using var api = new ChatApiClient("https://qa.invalid/", handler,
            new ChatRequestPolicy { RetryDelay = TimeSpan.Zero });
        using var form = new MainForm(snapshotMode: true, api);
        form.Show(); form.PopulateSnapshot(); Application.DoEvents();
        var checks = new List<string>();
        void Require(bool valid, string description)
        {
            HistoryQaAssertUiThread();
            if (!valid) throw new InvalidOperationException("Recipient key UI QA: " + description);
            checks.Add(description);
        }
        var account = form._session!.User;
        var originalRoom = form._selectedConversation!;
        var missing = originalRoom.Participants.First(user => !user.IsAgent && user.Id != account.Id);
        form.RecordRecipientKeyStatus(account.Id, originalRoom.Id, form._conversationVersion, [missing], true);
        Require(form._securityStatus.Text == "Alıcı eksik" && form._securityStatus.ForeColor == Theme.Warning,
            "A missing recipient is a short single-line warning, not a vague wrapped missing-key label");
        Require(form._securityStatus.AutoEllipsis && !form._securityStatus.UseMnemonic,
            "Security badge clips safely rather than overlapping the call actions");
        Require(form._securityStatus.AccessibleDescription!.Contains(missing.DisplayName, StringComparison.Ordinal) &&
            form._securityStatus.AccessibleDescription.Contains("chat uygulamasına", StringComparison.Ordinal) &&
            form._securityStatus.AccessibleDescription.Contains("API anahtarı değildir", StringComparison.Ordinal),
            "Help names the actual recipient and distinguishes device encryption from model API credentials");
        Require(form._recipientKeyTip!.GetToolTip(form._securityStatus) == form._securityStatus.AccessibleDescription,
            "Mouse help and accessibility describe the same recipient readiness");
        Require(form._securityStatus.AccessibleDescription.Contains("gönderilmedi", StringComparison.Ordinal),
            "An accepted partial Lounge send does not claim delivery to the missing recipient");
        var unchangedHelp = form._recipientKeyHelp;
        for (var poll = 0; poll < 8; poll++) form.RefreshEncryptionRecipientStatus();
        Require(ReferenceEquals(form._recipientKeyHelp, unchangedHelp),
            "Unchanged conversation polls preserve the explanation instead of hiding or re-arming its tooltip");

        var otherRoom = originalRoom with { Id = Guid.NewGuid(), Title = "Diğer sohbet" };
        form._selectedConversation = otherRoom; form._conversationVersion++;
        form.RefreshEncryptionRecipientStatus();
        var normalLabel = form._securityStatus.Text;
        Require(normalLabel == "● Şifreli" && !form._securityStatus.AccessibleDescription!.Contains(missing.DisplayName, StringComparison.Ordinal),
            "Switching rooms does not leak an old room's missing-recipient warning");
        var otherRoomHelp = form._recipientKeyHelp;
        form._selectedConversation = otherRoom with { Id = Guid.NewGuid() }; form._conversationVersion++;
        form.RefreshEncryptionRecipientStatus();
        Require(!ReferenceEquals(form._recipientKeyHelp, otherRoomHelp) &&
            form._recipientKeyHelp!.Details == otherRoomHelp!.Details,
            "Changing rooms invalidates a help bubble even when both rooms have identical explanation text");
        form._selectedConversation = otherRoom; form._conversationVersion++;
        form.RefreshEncryptionRecipientStatus();
        form.RecordRecipientKeyStatus(account.Id, originalRoom.Id, form._conversationVersion - 1, [missing], true);
        Require(form._securityStatus.Text == normalLabel,
            "Late completion of a different room cannot repaint the selected room's badge");
        form._selectedConversation = originalRoom; form._conversationVersion++;
        form.RefreshEncryptionRecipientStatus();
        Require(form._securityStatus.Text == "Alıcı eksik", "Returning to a room restores only that room's readiness report");

        var oldVersion = form._conversationVersion;
        form._conversationVersion++;
        form.RefreshEncryptionRecipientStatus();
        var priorDescription = form._securityStatus.AccessibleDescription;
        var priorReport = form._recipientKeyReports[originalRoom.Id];
        form.RecordRecipientKeyStatus(account.Id, originalRoom.Id, oldVersion, [], true);
        Require(form._securityStatus.AccessibleDescription == priorDescription &&
            ReferenceEquals(form._recipientKeyReports[originalRoom.Id], priorReport),
            "An old selection version cannot repaint a reselected room or replace its readiness report");
        form.RecordRecipientKeyStatus(account.Id, originalRoom.Id, form._conversationVersion, [missing], false);
        Require(form._securityStatus.AccessibleDescription!.Contains("gönderim tamamlanmadı", StringComparison.Ordinal),
            "A blocked regular-group send is not described as an accepted send");
        Require(form._recipientKeyReports[originalRoom.Id].OmittedFromAcceptedSend.Count == 1 &&
            form._securityStatus.AccessibleDescription.Contains("Önceki kabul edilmiş", StringComparison.Ordinal),
            "Preparing a new send preserves the previous accepted send's omitted-recipient caveat");

        form._selectedConversation = originalRoom with { Participants = [account] };
        form.RefreshEncryptionRecipientStatus();
        Require(form._securityStatus.Text == "● Şifreli", "A removed member is not retained as a current readiness warning");
        form._selectedConversation = originalRoom;
        form.RecordRecipientKeyStatus(account.Id, originalRoom.Id, form._conversationVersion, [missing], true);
        form.QueueMissingRecipientKeyRefresh();
        Require(handler.DeviceGets == 0, "Fresh send lookup is not repeated during the 30-second readiness cadence");

        // Imported users may have never registered a chat device. Probe only the
        // known absent users, four per cadence, not every member on each poll.
        var users = Enumerable.Range(1, 9).Select(index => new ChatUser(Guid.NewGuid(), $"Alıcı {index}", "", false, null)).ToArray();
        form._selectedConversation = originalRoom with { Participants = [account, .. users] };
        form.RecordRecipientKeyStatus(account.Id, originalRoom.Id, form._conversationVersion, users, true);
        foreach (var user in users)
            handler.Devices[user.Id] = new DeviceKeyBundle(user.Id, Guid.NewGuid(),
                form._identity.ExportEncryptionPublicKey(), form._identity.ExportSigningPublicKey());
        void MakeDue()
        {
            var report = form._recipientKeyReports[originalRoom.Id];
            form._recipientKeyReports[originalRoom.Id] = report with { NextCheck = DateTimeOffset.MinValue };
        }
        void CheckNow()
        {
            MakeDue(); form.QueueMissingRecipientKeyRefresh();
            if (form._recipientKeyRefreshTask is { } pending) HistoryQaPump(pending);
        }
        CheckNow();
        Require(handler.DeviceGets == 4 && form._recipientKeyReports[originalRoom.Id].Missing.Count == 5,
            "One readiness pass probes at most four known missing recipients");
        form.QueueMissingRecipientKeyRefresh();
        Require(handler.DeviceGets == 4, "Repeated poll events before the deadline do not repeat readiness requests");
        CheckNow(); CheckNow();
        Require(handler.DeviceGets == 9 && form._recipientKeyReports[originalRoom.Id].Missing.Count == 0 &&
            form._securityStatus.Text == "● Şifreli", "New device registrations eventually clear readiness without an extra send");
        Require(form._securityStatus.AccessibleDescription!.Contains("otomatik yeniden gönderilmez", StringComparison.Ordinal),
            "Recovered readiness explicitly preserves the previous message's omitted-recipient caveat");
        form.RecordRecipientKeyStatus(account.Id, originalRoom.Id, form._conversationVersion, [], true);
        Require(!form._securityStatus.AccessibleDescription!.Contains("Önceki gönderimde", StringComparison.Ordinal),
            "A new complete send clears the older omission report rather than leaving a stale warning");

        // A successful response arriving after room/account/version changes must
        // neither clear the fresh report nor paint another room's security badge.
        form.RecordRecipientKeyStatus(account.Id, originalRoom.Id, form._conversationVersion, [users[0]], true);
        handler.Hold = new(TaskCreationOptions.RunContinuationsAsynchronously);
        MakeDue(); form.QueueMissingRecipientKeyRefresh();
        Require(form._recipientKeyRefreshRunning, "A held readiness query has one in-flight owner");
        var gets = handler.DeviceGets;
        form.QueueMissingRecipientKeyRefresh();
        Require(handler.DeviceGets == gets, "An in-flight query cannot be overlapped by the next timer tick");
        form._selectedConversation = otherRoom; form._conversationVersion++;
        form.RefreshEncryptionRecipientStatus();
        var otherDetails = form._securityStatus.AccessibleDescription;
        handler.Hold.SetResult(RecipientKeyQaHandler.Json(handler.Devices[users[0].Id]));
        HistoryQaPump(form._recipientKeyRefreshTask!); handler.Hold = null;
        Require(form._securityStatus.AccessibleDescription == otherDetails &&
            form._recipientKeyReports[originalRoom.Id].Missing.Count == 1,
            "A late public-key response cannot commit across a selection boundary");

        // The selected room can remain unchanged while a newer send publishes a
        // new readiness report. Its state must not be overwritten by an older GET.
        form._selectedConversation = originalRoom with { Participants = [account, .. users] };
        form._conversationVersion++;
        form.RecordRecipientKeyStatus(account.Id, originalRoom.Id, form._conversationVersion, [users[0]], true);
        handler.Hold = new(TaskCreationOptions.RunContinuationsAsynchronously);
        MakeDue(); form.QueueMissingRecipientKeyRefresh();
        form.RecordRecipientKeyStatus(account.Id, originalRoom.Id, form._conversationVersion, [users[1]], false);
        var newerSendReport = form._recipientKeyReports[originalRoom.Id];
        var newerSendHelp = form._recipientKeyHelp;
        handler.Hold.SetResult(RecipientKeyQaHandler.Json(handler.Devices[users[0].Id]));
        HistoryQaPump(form._recipientKeyRefreshTask!); handler.Hold = null;
        Require(ReferenceEquals(form._recipientKeyReports[originalRoom.Id], newerSendReport) &&
            ReferenceEquals(form._recipientKeyHelp, newerSendHelp) && newerSendReport.Missing.Single().Id == users[1].Id,
            "An in-flight readiness response cannot overwrite a newer send report in the same room");

        form.RecordRecipientKeyStatus(account.Id, originalRoom.Id, form._conversationVersion, [users[0]], true);
        handler.Hold = new(TaskCreationOptions.RunContinuationsAsynchronously);
        MakeDue(); form.QueueMissingRecipientKeyRefresh();
        form._selectedConversation = otherRoom; form._conversationVersion++;
        form.RefreshEncryptionRecipientStatus();
        var beforeStaleUnauthorized = (form.Text, form._networkFailures, form._nextNetworkPoll);
        var beforeStaleHelp = form._recipientKeyHelp;
        handler.Hold.SetResult(RecipientKeyQaHandler.Unauthorized());
        HistoryQaPump(form._recipientKeyRefreshTask!); handler.Hold = null;
        Require((form.Text, form._networkFailures, form._nextNetworkPoll) == beforeStaleUnauthorized &&
            ReferenceEquals(form._recipientKeyHelp, beforeStaleHelp),
            "A stale-room 401 readiness response does not change the current room's caption, retry state or help");

        form._selectedConversation = originalRoom with { Participants = [account, .. users] };
        form._conversationVersion++;
        form.RecordRecipientKeyStatus(account.Id, originalRoom.Id, form._conversationVersion, [users[0]], true);
        // Give the real timers a safely distant deadline: this probe verifies that
        // 401 stops them without allowing a periodic unrelated HTTP request.
        form._refreshTimer.Interval = form._readTimer.Interval = int.MaxValue;
        form._refreshTimer.Start(); form._readTimer.Start();
        handler.Hold = new(TaskCreationOptions.RunContinuationsAsynchronously);
        MakeDue(); form.QueueMissingRecipientKeyRefresh();
        var currentUnauthorizedReport = form._recipientKeyReports[originalRoom.Id];
        handler.Hold.SetResult(RecipientKeyQaHandler.Unauthorized());
        HistoryQaPump(form._recipientKeyRefreshTask!); handler.Hold = null;
        Require(form.Text.Contains("Yeniden giriş gerekli", StringComparison.Ordinal) &&
            !form._refreshTimer.Enabled && !form._readTimer.Enabled &&
            ReferenceEquals(form._recipientKeyReports[originalRoom.Id], currentUnauthorizedReport) &&
            currentUnauthorizedReport.Missing.Single().Id == users[0].Id,
            "A current readiness 401 follows re-login handling and preserves the last truthful missing-recipient report");

        form._selectedConversation = originalRoom;
        form._session = form._session with { User = account with { Id = Guid.NewGuid() } };
        form.RefreshEncryptionRecipientStatus();
        Require(form._recipientKeyReports.Count == 0 && form._securityStatus.Text == "● Şifreli",
            "An account change clears the prior account's recipient names and readiness reports");
        form.RecordRecipientKeyStatus(account.Id, originalRoom.Id, form._conversationVersion, [missing], true);
        Require(form._recipientKeyReports.Count == 0, "A late old-account send cannot create a report in the new account");
        form._selectedConversation = null; form.RefreshEncryptionRecipientStatus();
        Require(form._securityStatus.Text.Length == 0 && form._securityStatus.AccessibleDescription!.Length == 0 &&
            string.IsNullOrEmpty(form._recipientKeyTip!.GetToolTip(form._securityStatus)),
            "Clearing the room removes both visible and accessible stale security details");
        var changedAccount = form._session!.User;
        form._selectedConversation = originalRoom with { Participants = [changedAccount, users[0]] };
        form._conversationVersion++;
        form.RecordRecipientKeyStatus(changedAccount.Id, originalRoom.Id, form._conversationVersion, [users[0]], true);
        handler.Hold = new(TaskCreationOptions.RunContinuationsAsynchronously);
        MakeDue(); form.QueueMissingRecipientKeyRefresh();
        var disposalTask = form._recipientKeyRefreshTask!;
        var disposalResponse = handler.Hold;
        Require(form._recipientKeyRefreshRunning && !disposalTask.IsCompleted,
            "Disposal probe owns a genuinely pending readiness request");
        form.Dispose(); Application.DoEvents();
        Require(form._recipientKeyTip is null && form._recipientKeyHelp is null && form._recipientKeyReports.Count == 0,
            "Disposal releases the native tooltip, query lifetime and per-room reports");
        disposalResponse.SetResult(RecipientKeyQaHandler.Json(handler.Devices[users[0].Id]));
        HistoryQaPump(disposalTask); handler.Hold = null;
        Require(disposalTask.IsCompletedSuccessfully && !form._recipientKeyRefreshRunning &&
            form._recipientKeyTip is null && form._recipientKeyHelp is null && form._recipientKeyReports.Count == 0,
            "A held readiness response completed after disposal cannot recreate a tooltip or report or fault the UI task");
        Require(handler.NonDeviceRequests == 0, "Readiness QA never sends messages or accesses live/unrelated endpoints");
        return checks;
    }

    private sealed class RecipientKeyQaHandler : HttpMessageHandler
    {
        internal readonly Dictionary<Guid, DeviceKeyBundle> Devices = new();
        internal int DeviceGets, NonDeviceRequests;
        internal TaskCompletionSource<HttpResponseMessage>? Hold;
        internal static HttpResponseMessage Json<T>(T value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
        internal static HttpResponseMessage Unauthorized() => new(HttpStatusCode.Unauthorized)
        { Content = JsonContent.Create(new ApiError("qa_unauthorized", "Sentetik oturum süresi doldu.")) };
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var parts = request.RequestUri!.AbsolutePath.Trim('/').Split('/');
            if (request.Method == HttpMethod.Get && parts[^1] == "device")
            {
                DeviceGets++;
                if (Hold is { } held) return held.Task; // deliberately ignore cancellation to exercise scope fencing
                return Task.FromResult(Devices.TryGetValue(Guid.Parse(parts[^2]), out var device)
                    ? Json(device) : new HttpResponseMessage(HttpStatusCode.NotFound));
            }
            NonDeviceRequests++;
            throw new InvalidOperationException("Recipient readiness QA forbids unrelated HTTP requests.");
        }
    }
}
