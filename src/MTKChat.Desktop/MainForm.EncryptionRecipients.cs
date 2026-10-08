using MTKChat.Contracts;

namespace MTKChat.Desktop;

internal sealed partial class MainForm
{
    // This is delivery readiness, not a switch that can turn encryption off. A
    // missing recipient key never authorizes a plaintext payload or a new key
    // invented on the server. Reports belong to the signed-in account and room.
    private sealed record RecipientKeyReport(Guid AccountId, Guid ConversationId,
        IReadOnlyList<ChatUser> Missing, IReadOnlyList<ChatUser> OmittedFromAcceptedSend,
        bool SendAccepted, DateTimeOffset NextCheck, int Cursor = 0);

    private readonly Dictionary<Guid, RecipientKeyReport> _recipientKeyReports = new();
    private readonly CancellationTokenSource _recipientKeyLifetime = new();
    private ToolTip? _recipientKeyTip;
    private sealed record RecipientKeyHelp(Guid? AccountId, Guid? ConversationId, string Details);
    private RecipientKeyHelp? _recipientKeyHelp;
    private Guid? _recipientKeyAccount;
    private bool _recipientKeyRefreshRunning;
    private Task? _recipientKeyRefreshTask;

    private void ConfigureEncryptionRecipientStatus()
    {
        _securityStatus.AutoEllipsis = true;
        _securityStatus.UseMnemonic = false;
        _securityStatus.AccessibleName = "Mesaj şifreleme ve alıcı durumu";
        _recipientKeyTip = new ToolTip
        {
            AutoPopDelay = 20000, InitialDelay = 250, ReshowDelay = 100,
            ShowAlways = true, UseAnimation = false, UseFading = false, OwnerDraw = true
        };
        _recipientKeyTip.Popup += (_, args) =>
        {
            using var font = Theme.Font(9f);
            var scale = _securityStatus.DeviceDpi / 96f;
            var width = Math.Max(260, (int)Math.Round(420 * scale));
            var text = _securityStatus.AccessibleDescription ?? "";
            var measured = TextRenderer.MeasureText(text, font, new Size(width - 24, int.MaxValue),
                TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            args.ToolTipSize = new Size(Math.Min(width, measured.Width + 24), measured.Height + 20);
        };
        _recipientKeyTip.Draw += (_, args) =>
        {
            using var fill = new SolidBrush(Theme.Surface);
            using var border = new Pen(Theme.Divider);
            args.Graphics.FillRectangle(fill, args.Bounds);
            args.Graphics.DrawRectangle(border, 0, 0, Math.Max(0, args.Bounds.Width - 1), Math.Max(0, args.Bounds.Height - 1));
            using var font = Theme.Font(9f);
            TextRenderer.DrawText(args.Graphics, args.ToolTipText, font,
                Rectangle.Inflate(args.Bounds, -12, -10), Theme.Text,
                TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        };
        _securityStatus.Click += (_, _) =>
        {
            if (_securityStatus.AccessibleDescription is { Length: > 0 } details)
                _recipientKeyTip?.Show(details, _securityStatus, new Point(0, _securityStatus.Height), 20000);
        };
        _securityStatus.Disposed += (_, _) =>
        {
            _recipientKeyLifetime.Cancel();
            _recipientKeyLifetime.Dispose();
            _recipientKeyTip?.Dispose(); _recipientKeyTip = null;
            _recipientKeyHelp = null;
            _recipientKeyReports.Clear();
        };
        RefreshEncryptionRecipientStatus();
    }

    private void RecordRecipientKeyStatus(Guid accountId, Guid conversationId, int selectionVersion,
        IReadOnlyList<ChatUser> missing, bool sendAccepted)
    {
        if (IsDisposed || _session?.User.Id != accountId || selectionVersion != _conversationVersion) return;
        EnsureRecipientKeyAccount(accountId);
        // Snapshot caller-owned lists: later list mutation cannot rewrite the
        // security explanation of an already accepted encrypted send.
        var recipients = missing.DistinctBy(user => user.Id).ToArray();
        _recipientKeyReports.TryGetValue(conversationId, out var previous);
        _recipientKeyReports[conversationId] = new(accountId, conversationId, recipients,
            sendAccepted ? recipients : previous?.OmittedFromAcceptedSend ?? [],
            sendAccepted, DateTimeOffset.UtcNow.AddSeconds(30));
        if (_selectedConversation?.Id == conversationId && selectionVersion == _conversationVersion)
            RefreshEncryptionRecipientStatus();
    }

    private void EnsureRecipientKeyAccount(Guid accountId)
    {
        if (_recipientKeyAccount == accountId) return;
        _recipientKeyAccount = accountId;
        _recipientKeyReports.Clear();
    }

    private void RefreshEncryptionRecipientStatus()
    {
        if (IsDisposed) return;
        if (_session is null || _selectedConversation is null)
        {
            _securityStatus.Text = "";
            UpdateRecipientKeyHelp("");
            return;
        }
        EnsureRecipientKeyAccount(_session.User.Id);
        _recipientKeyReports.TryGetValue(_selectedConversation.Id, out var report);
        var memberIds = _selectedConversation.Participants.Select(user => user.Id).ToHashSet();
        var missing = report?.Missing.Where(user => memberIds.Contains(user.Id)).ToArray() ?? [];
        var omitted = report?.OmittedFromAcceptedSend.Where(user => memberIds.Contains(user.Id)).ToArray() ?? [];
        _securityStatus.Text = missing.Length > 0 ? "Alıcı eksik" : "● Şifreli";
        _securityStatus.ForeColor = missing.Length > 0 ? Theme.Warning : Theme.Success;
        _securityStatus.Cursor = Cursors.Help;
        var details = missing.Length > 0
            ? $"{missing.Length} katılımcının chat cihazı şifreleme anahtarı henüz yok: {RecipientNames(missing)}.\n" +
              "Bu kişiler chat uygulamasına bir kez giriş yapmalı. Bu, Gemini veya Groq API anahtarı değildir.\n" +
              (report?.SendAccepted == true
                  ? "Son mesaj yalnız anahtarı olan alıcılara uçtan uca şifreli gönderildi; anahtarı olmayanlara gönderilmedi."
                  : "Bu alıcılarla gönderim tamamlanmadı; mesaj şifresiz gönderilmez.")
            : "Mesaj gövdesi uçtan uca şifrelenir. Alıcı cihaz anahtarları gönderimden önce kontrol edilir.";
        if (missing.Length == 0 && omitted.Length > 0)
            details += $"\nYeni mesajlar için alıcı anahtarları artık hazır. Önceki gönderimde {RecipientNames(omitted)} alıcılarına zarf oluşturulamadı; eski mesaj onlara otomatik yeniden gönderilmez.";
        else if (omitted.Length > 0 && report?.SendAccepted != true)
            details += $"\nÖnceki kabul edilmiş gönderimde {RecipientNames(omitted)} alıcılarına mesaj gönderilemedi. Yeni gönderim hazırlığı bu eski teslim durumunu değiştirmez.";
        UpdateRecipientKeyHelp(details);
    }

    private void UpdateRecipientKeyHelp(string details)
    {
        var accountId = _session?.User.Id;
        var conversationId = _selectedConversation?.Id;
        if (_recipientKeyHelp is { } current && current.AccountId == accountId &&
            current.ConversationId == conversationId && string.Equals(current.Details, details, StringComparison.Ordinal)) return;
        // Ordinary three-second list polls must not interrupt a twenty-second
        // explanation. Hide/re-arm only on a genuine scope or content change,
        // including switching between rooms whose explanation text is identical.
        _recipientKeyTip?.Hide(_securityStatus);
        _recipientKeyHelp = new(accountId, conversationId, details);
        _securityStatus.AccessibleDescription = details;
        _recipientKeyTip?.SetToolTip(_securityStatus, details.Length == 0 ? null : details);
    }

    private static string RecipientNames(IReadOnlyList<ChatUser> users)
    {
        // Keep a tooltip bounded even for a large imported group; names are UI
        // data already visible to this room, never key material or identifiers.
        static string Safe(string name) => new(name.Where(character => !char.IsControl(character)).Take(60).ToArray());
        var names = string.Join(", ", users.Take(6).Select(user => Safe(user.DisplayName)));
        return users.Count > 6 ? $"{names} ve {users.Count - 6} kişi daha" : names;
    }

    private void QueueMissingRecipientKeyRefresh()
    {
        if (IsDisposed || _recipientKeyRefreshRunning || _session is null || _selectedConversation is null) return;
        EnsureRecipientKeyAccount(_session.User.Id);
        if (!_recipientKeyReports.TryGetValue(_selectedConversation.Id, out var report) || report.Missing.Count == 0 ||
            DateTimeOffset.UtcNow < report.NextCheck) return;
        var currentMembers = _selectedConversation.Participants.Select(user => user.Id).ToHashSet();
        var pending = report.Missing.Where(user => currentMembers.Contains(user.Id)).ToArray();
        if (pending.Length == 0) { RefreshEncryptionRecipientStatus(); return; }
        // At most four already-known missing recipients per 30 seconds. This is
        // event-driven by the existing poll, not a second timer or an all-member
        // scan on every presence tick; it cannot delay history/call polling.
        report = report with { Missing = pending, NextCheck = DateTimeOffset.UtcNow.AddSeconds(30) };
        _recipientKeyReports[report.ConversationId] = report;
        _recipientKeyRefreshRunning = true;
        _recipientKeyRefreshTask = RefreshMissingRecipientKeysAsync(report, _conversationVersion);
    }

    private async Task RefreshMissingRecipientKeysAsync(RecipientKeyReport report, int selectionVersion)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_recipientKeyLifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        var ready = new HashSet<Guid>();
        var count = Math.Min(4, report.Missing.Count);
        try
        {
            for (var offset = 0; offset < count; offset++)
            {
                if (!RecipientKeyRefreshIsCurrent(report, selectionVersion)) return;
                var recipient = report.Missing[(report.Cursor + offset) % report.Missing.Count];
                // Do not use a cached null/old observation to decide a newly
                // registered device is still absent. Existing identity keys are
                // neither generated, replaced nor deleted by this read-only check.
                if (await GetDeviceAsync(recipient.Id, forceRefresh: true, timeout.Token) is not null)
                    ready.Add(recipient.Id);
            }
            if (!RecipientKeyRefreshIsCurrent(report, selectionVersion)) return;
            var remaining = report.Missing.Where(user => !ready.Contains(user.Id)).ToArray();
            _recipientKeyReports[report.ConversationId] = report with
            {
                Missing = remaining,
                Cursor = remaining.Length == 0 ? 0 : (report.Cursor + count) % remaining.Length
            };
            RefreshEncryptionRecipientStatus();
        }
        catch (OperationCanceledException) { /* Keep the last truthful readiness report. */ }
        catch (Exception exception)
        {
            // An auxiliary readiness outage does not prove that a key vanished.
            // Authentication failure still follows the normal re-login path.
            if (exception is ChatApiException { StatusCode: System.Net.HttpStatusCode.Unauthorized } &&
                RecipientKeyRefreshIsCurrent(report, selectionVersion))
                RecordNetworkFailure(exception);
        }
        finally { _recipientKeyRefreshRunning = false; }
    }

    private bool RecipientKeyRefreshIsCurrent(RecipientKeyReport report, int selectionVersion) =>
        !IsDisposed && _session?.User.Id == report.AccountId && _selectedConversation?.Id == report.ConversationId &&
        _conversationVersion == selectionVersion && _recipientKeyReports.TryGetValue(report.ConversationId, out var current) &&
        ReferenceEquals(current, report);
}
