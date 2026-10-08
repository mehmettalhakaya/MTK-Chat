using System.Security.Cryptography;
using System.Text.Json;
using MTKChat.Contracts;
using MTKChat.Cryptography;

namespace MTKChat.Desktop;

internal sealed partial class MainForm
{
    private sealed record PendingFile(byte[] Bytes, string Name);
    private PendingFile? _pendingFile;
    private Control? _fileDraftCard;
    private readonly Label _fileDraftLabel = new();

    private Control BuildFileDraftPreview()
    {
        var card = new RoundedPanel { Dock = DockStyle.Fill, Visible = false, Margin = new Padding(2, 0, 2, 8),
            Padding = new Padding(12), FillColor = Theme.SurfaceRaised, CornerRadius = 12 };
        var remove = Theme.Button("×", ButtonKind.Ghost); remove.Dock = DockStyle.Right; remove.Width = 40;
        remove.AccessibleName = "Dosya taslağını kaldır"; remove.Click += (_, _) => ClearPendingFile();
        _fileDraftLabel.Dock = DockStyle.Fill; _fileDraftLabel.ForeColor = Theme.Text;
        _fileDraftLabel.Font = Theme.Font(10); _fileDraftLabel.AutoEllipsis = true;
        _fileDraftLabel.TextAlign = ContentAlignment.MiddleLeft;
        card.Controls.Add(_fileDraftLabel); card.Controls.Add(remove); _fileDraftCard = card;
        return card;
    }

    private async Task PickFileAsync()
    {
        if (_sending || _voiceRecorder is not null || _voiceStopping) return;
        using var picker = new OpenFileDialog { Title = "Dosya ekle", Filter = "Tüm dosyalar|*.*", CheckFileExists = true };
        if (picker.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            var info = new FileInfo(picker.FileName);
            if (info.Length is < 1 or > FileCryptography.MaxFileBytes) throw new InvalidDataException("Dosya 1 bayt–5 MB olmalı.");
            var room = _selectedConversation?.Id;
            var bytes = await File.ReadAllBytesAsync(picker.FileName);
            if (IsDisposed || _selectedConversation?.Id != room) { CryptographicOperations.ZeroMemory(bytes); return; }
            StageFile(bytes, FileCryptography.SafeName(info.Name));
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    private void StageFile(byte[] bytes, string name)
    {
        ClearPendingImage(); ClearPendingVoice(); ClearPendingFile();
        _pendingFile = new(bytes, name);
        _fileDraftLabel.Text = $"▤  {name}   ·   {bytes.Length / 1024d:0.#} KB";
        if (_fileDraftCard is not null) { _fileDraftCard.Visible = true; _fileDraftCard.BringToFront(); }
        if (_imageDraftRowStyle is not null) _imageDraftRowStyle.Height = 72;
        if (_composerHeightStyle is not null) _composerHeightStyle.Height = ComposerBaseHeight + 72;
    }

    private void ClearPendingFile()
    {
        if (_pendingFile is not null) CryptographicOperations.ZeroMemory(_pendingFile.Bytes);
        _pendingFile = null;
        if (_fileDraftCard is not null) _fileDraftCard.Visible = false;
        _fileDraftLabel.Text = "";
        if (_imageDraftRowStyle is not null) _imageDraftRowStyle.Height = 0;
        if (_composerHeightStyle is not null) _composerHeightStyle.Height = ComposerBaseHeight;
    }

    private async Task<bool> SendFileAsync(PendingFile file)
    {
        byte[]? descriptorBytes = null;
        byte[]? cipher = null;
        // Conversation changes dispose/zero the draft while recipient lookup is awaiting HTTP.
        // Keep an independent send buffer so a cleared draft cannot become a zero-filled file.
        var plaintext = file.Bytes.ToArray();
        try
        {
            if (_selectedConversation is null || _session is null) return false;
            var room = _selectedConversation.Id;
            var sender = _session.User.Id;
            var duration = _selectedExpiry.Duration;
            var devices = new List<DeviceKeyBundle>();
            foreach (var member in (await _api.GetMembersAsync(room)).Where(m => !m.IsAgent))
            {
                var device = await GetDeviceAsync(member.Id);
                if (device is not null) devices.Add(device);
                else if (room != Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"))
                    throw new InvalidOperationException($"{member.DisplayName} şifreli dosya alabilmek için önce uygulamaya giriş yapmalı.");
            }
            if (devices.Count == 0) throw new InvalidOperationException("Alıcı cihazı bulunamadı.");
            var client = Guid.NewGuid();
            var at = DateTimeOffset.UtcNow;
            var encrypted = FileCryptography.Encrypt(plaintext, file.Name, client, room, sender);
            cipher = encrypted.Ciphertext;
            var upload = await _api.UploadFileAsync(room, client, cipher);
            var descriptor = encrypted.Descriptor with { StorageToken = upload.StorageToken };
            descriptorBytes = JsonSerializer.SerializeToUtf8Bytes(descriptor);
            var payloads = devices.Select(d => MessageCryptography.Encrypt(descriptorBytes, client, room, sender,
                d.UserId, at, d.EncryptionPublicKey, _identity.SigningKey)).ToArray();
            await _api.SendMessageAsync(new(client, room, "file", at, duration is { } expiry ? at.Add(expiry) : null,
                payloads, new EncryptedAttachment("", "application/octet-stream", cipher.Length, upload.StorageToken, "", "")));
            if (_selectedConversation?.Id == room) await RefreshMessagesAsync(silent: true);
            return true;
        }
        catch (Exception ex) { ShowError(ex.Message); return false; }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            if (descriptorBytes is not null) CryptographicOperations.ZeroMemory(descriptorBytes);
            if (cipher is not null) CryptographicOperations.ZeroMemory(cipher);
        }
    }

    private async Task SaveReceivedFileAsync(StoredMessage message, EncryptedFileDescriptor file)
    {
        using var picker = new SaveFileDialog { Title = "Dosyayı kaydet", FileName = FileCryptography.SafeName(file.FileName),
            Filter = "Tüm dosyalar|*.*", OverwritePrompt = true, AddExtension = false };
        if (picker.ShowDialog(this) != DialogResult.OK) return;
        byte[]? cipher = null; byte[]? plaintext = null;
        try
        {
            cipher = await _api.DownloadFileAsync(file.StorageToken);
            plaintext = FileCryptography.Decrypt(cipher, file, message.ClientMessageId, message.ConversationId, message.SenderId);
            // Save only to the explicitly chosen location; never execute received files automatically.
            await File.WriteAllBytesAsync(picker.FileName, plaintext);
        }
        catch (Exception ex) { ShowError($"Dosya kaydedilemedi: {ex.Message}"); }
        finally
        {
            if (cipher is not null) CryptographicOperations.ZeroMemory(cipher);
            if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    internal void PopulateFileDraftSnapshot() => StageFile("MTK örnek dosya"u8.ToArray(), "Toplantı notları.pdf");
}
