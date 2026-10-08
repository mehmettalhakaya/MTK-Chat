using System.Security.Cryptography;
using MTKChat.Contracts;
using MTKChat.Cryptography;

namespace MTKChat.Desktop;

internal sealed partial class MainForm
{
    private StatusesPanel? _sidebarStatuses;
    private BlockedUsersPanel? _sidebarBlocked;
    private ModernButton? _railStatus;

    private bool SidebarWriteBusy => _sidebarProfileEditor?.IsBusy == true || _sidebarStatuses?.IsBusy == true;

    private void EnsureSocialPanels()
    {
        if (_session is null || _sidebarDrawerBody is null) return;
        _sidebarStatuses ??= new StatusesPanel(_api, _session.User, _avatars, CreateStatusAsync, ViewStatusAsync) { Dock = DockStyle.Fill };
        _sidebarStatuses.RefreshOwner(_session.User);
        _sidebarBlocked ??= new BlockedUsersPanel(_api, _session.User.Id, _avatars) { Dock = DockStyle.Fill };
        if (_sidebarStatuses.Parent is null)
        {
            _sidebarStatuses.BusyChanged += _ => StyleSidebarNavigation();
            _sidebarDrawerBody.Controls.Add(_sidebarStatuses);
        }
        if (_sidebarBlocked.Parent is null)
        {
            _sidebarBlocked.UserUnblocked += _ => InvalidateConversationVisibility();
            _sidebarDrawerBody.Controls.Add(_sidebarBlocked);
        }
    }

    private async Task CreateStatusAsync()
    {
        if (_session is null || IsDisposed || Disposing) return;
        var owner = _session.User.Id;
        var usersTask = _api.GetUsersAsync(); var blockedTask = _api.GetBlockedUsersAsync();
        await Task.WhenAll(usersTask, blockedTask);
        if (IsDisposed || Disposing || _session?.User.Id != owner) return;
        var blocked = (await blockedTask).ToHashSet();
        var users = (await usersTask).Where(u => !u.IsAgent && u.Id != owner && !blocked.Contains(u.Id));
        using var composer = new StatusComposerForm(_api, users, _avatars, PrepareStatusAsync);
        composer.ShowDialog(this);
    }

    private async Task<SendStatusRequest> PrepareStatusAsync(StatusDraft draft, CancellationToken token)
    {
        var owner = _session?.User.Id ?? throw new InvalidOperationException("Önce giriş yap.");
        if (draft.Audience.Length is < 1 or > 50 || draft.Audience.Distinct().Count() != draft.Audience.Length || draft.Audience.Contains(owner))
            throw new InvalidDataException("Durumunu görebilecek 1–50 kişi seç.");
        var recipients = new List<DeviceKeyBundle>();
        foreach (var id in draft.Audience.Prepend(owner))
        {
            var device = await _api.TryGetDeviceAsync(id, token);
            if (device is null) throw new InvalidOperationException("Seçtiğin kişilerden biri henüz uygulamaya giriş yapmamış; şifreleme anahtarı yok.");
            if (device.UserId != id || id == owner && device.EncryptionPublicKey != _identity.ExportEncryptionPublicKey())
                throw new InvalidDataException("Cihaz anahtarı doğrulanamadı. Tekrar giriş yap.");
            recipients.Add(device);
        }
        if (IsDisposed || Disposing || _session?.User.Id != owner) throw new OperationCanceledException(token);
        var privateKey = _identity.SigningKey.ExportPkcs8PrivateKey();
        var idForStatus = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
        try
        {
            return await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested(); using var signing = ECDsa.Create();
                signing.ImportPkcs8PrivateKey(privateKey, out _);
                return StatusCryptography.Encrypt(draft.Content, draft.Kind, idForStatus, owner, now, recipients, signing);
            }, token);
        }
        finally { CryptographicOperations.ZeroMemory(privateKey); }
    }

    private async Task ViewStatusAsync(StatusSummary summary)
    {
        var owner = _session?.User.Id ?? throw new InvalidOperationException("Önce giriş yap.");
        var status = await _api.GetStatusAsync(summary.Id);
        if (status.Id != summary.Id || status.ClientStatusId != summary.ClientStatusId || status.SenderId != summary.SenderId ||
            status.Kind != summary.Kind || status.CreatedAt != summary.CreatedAt || status.ExpiresAt != summary.ExpiresAt ||
            status.ExpiresAt <= DateTimeOffset.UtcNow) throw new InvalidDataException("Durum silinmiş veya süresi dolmuş.");
        var device = await _api.TryGetDeviceAsync(status.SenderId) ?? throw new InvalidDataException("Gönderen anahtarı bulunamadı.");
        var sender = status.SenderId == owner ? _session!.User : (await _api.GetUsersAsync()).FirstOrDefault(u => u.Id == status.SenderId)
            ?? throw new InvalidDataException("Gönderen hesabı bulunamadı.");
        if (IsDisposed || Disposing || _session?.User.Id != owner) return;
        var privateKey = _identity.EncryptionKey.ExportPkcs8PrivateKey(); byte[]? content = null;
        try
        {
            content = await Task.Run(() =>
            {
                using var encryption = ECDiffieHellman.Create(); encryption.ImportPkcs8PrivateKey(privateKey, out _);
                return StatusCryptography.Decrypt(status, owner, encryption, device.SigningPublicKey);
            });
            if (IsDisposed || Disposing || _session?.User.Id != owner || status.ExpiresAt <= DateTimeOffset.UtcNow) return;
            using var viewer = new StatusViewerForm(sender, status, content); viewer.ShowDialog(this);
        }
        finally { CryptographicOperations.ZeroMemory(privateKey); if (content is not null) CryptographicOperations.ZeroMemory(content); }
    }
}
