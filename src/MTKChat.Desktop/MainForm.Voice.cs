using System.Security.Cryptography;

namespace MTKChat.Desktop;

internal sealed partial class MainForm
{
    private sealed record PendingVoice(byte[] Bytes, TimeSpan Duration);
    private PendingVoice? _pendingVoice;
    private VoiceRecorder? _voiceRecorder;
    private bool _voiceStopping;
    private int _voiceDraftVersion;
    private VoicePlayback? _draftPlayback;
    private readonly System.Windows.Forms.Timer _voiceTimer = new() { Interval = 200 };
    private readonly ModernButton _voiceRecordButton = Theme.Button("●  Ses", ButtonKind.Ghost);
    private readonly Label _voiceDraftLabel = new();
    private Control? _voiceDraftCard;
    private Control? _imageDraftCard;

    private Control BuildVoiceDraftPreview()
    {
        var card = new RoundedPanel
        {
            Dock = DockStyle.Fill, Margin = new Padding(2, 0, 2, 8), Padding = new Padding(8),
            FillColor = Theme.SurfaceRaised, BorderColor = Theme.Divider, CornerRadius = 12,
            Visible = false
        };
        var row = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, BackColor = Color.Transparent };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 40));
        var play = Theme.Button("▶ Dinle", ButtonKind.Ghost);
        play.Dock = DockStyle.Fill;
        play.Click += (_, _) =>
        {
            try { _draftPlayback?.Toggle(); }
            catch { _voiceDraftLabel.Text = "Ses oynatılamadı"; }
        };
        _voiceDraftLabel.Dock = DockStyle.Fill;
        _voiceDraftLabel.TextAlign = ContentAlignment.MiddleLeft;
        _voiceDraftLabel.ForeColor = Theme.Text;
        _voiceDraftLabel.Font = Theme.Font(9.5f);
        _voiceDraftLabel.Padding = new Padding(10, 0, 0, 0);
        var remove = Theme.Button("×", ButtonKind.Ghost);
        remove.Dock = DockStyle.Fill;
        remove.AccessibleName = "Ses taslağını kaldır";
        remove.Click += (_, _) => ClearPendingVoice();
        row.Controls.Add(play, 0, 0);
        row.Controls.Add(_voiceDraftLabel, 1, 0);
        row.Controls.Add(remove, 2, 0);
        card.Controls.Add(row);
        _voiceDraftCard = card;
        _voiceTimer.Tick += async (_, _) =>
        {
            if (_voiceRecorder is null) return;
            var elapsed = DateTimeOffset.UtcNow - _voiceRecorder.StartedAt;
            _voiceRecordButton.Text = $"■  {elapsed:mm\\:ss}";
            if (elapsed >= VoiceRecorder.MaxDuration) await StopVoiceRecordingAsync();
        };
        return card;
    }

    private async Task ToggleVoiceRecordingAsync()
    {
        if (_voiceRecorder is not null) { await StopVoiceRecordingAsync(); return; }
        if (_sending || _voiceStopping || _selectedConversation is null) return;
        if (_callForm is { IsDisposed: false }) { ShowError("Sesli mesaj kaydetmek için önce aramayı kapatın."); return; }
        try
        {
            ClearPendingImage();
            ClearPendingFile();
            ClearPendingVoice();
            _voiceRecorder = new VoiceRecorder();
            _voiceRecordButton.Text = "■  0:00";
            _voiceRecordButton.Kind = ButtonKind.Danger;
            _voiceTimer.Start();
        }
        catch (Exception ex)
        {
            _voiceRecorder?.Dispose();
            _voiceRecorder = null;
            ShowError($"Mikrofon açılamadı: {ex.Message}");
        }
    }

    private async Task StopVoiceRecordingAsync()
    {
        var recorder = _voiceRecorder;
        if (recorder is null) return;
        var conversationId = _selectedConversation?.Id;
        var draftVersion = _voiceDraftVersion;
        _voiceStopping = true;
        _voiceRecorder = null;
        _voiceTimer.Stop();
        _voiceRecordButton.Text = "●  Ses";
        _voiceRecordButton.Kind = ButtonKind.Ghost;
        try
        {
            var bytes = await recorder.StopAsync();
            if (IsDisposed || _selectedConversation?.Id != conversationId || draftVersion != _voiceDraftVersion)
            {
                CryptographicOperations.ZeroMemory(bytes);
                return;
            }
            if (bytes.Length < 100 || bytes.Length > 2 * 1024 * 1024)
            {
                CryptographicOperations.ZeroMemory(bytes);
                ShowError("Ses kaydı boş veya çok uzun.");
                return;
            }
            var elapsed = recorder.Duration;
            StageVoice(bytes, elapsed);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ShowError($"Ses kaydı tamamlanamadı: {ex.Message}"); }
        finally { recorder.Dispose(); _voiceStopping = false; }
    }

    private void StageVoice(byte[] bytes, TimeSpan elapsed)
    {
        ClearPendingImage();
        ClearPendingVoice();
        _pendingVoice = new PendingVoice(bytes, elapsed);
        _voiceDraftLabel.Text = $"Sesli mesaj  ·  {elapsed:mm\\:ss}";
        _voiceDraftCard!.Visible = true;
        if (_imageDraftCard is not null) _imageDraftCard.Visible = false;
        if (_imageDraftRowStyle is not null) _imageDraftRowStyle.Height = 76;
        if (_composerHeightStyle is not null) _composerHeightStyle.Height = ComposerBaseHeight + 76;
        _draftPlayback = new VoicePlayback(_voiceDraftCard, bytes.ToArray(),
            playing => _voiceDraftLabel.Text = playing ? "Sesli mesaj çalıyor…" : $"Sesli mesaj  ·  {elapsed:mm\\:ss}");
    }

    internal void PopulateVoiceDraftSnapshot()
    {
        StageVoice(CreateSilentVoiceSample(), TimeSpan.FromSeconds(3));
    }

    internal void PopulateVoiceMessageSnapshot()
    {
        foreach (Control row in _messageList.Controls.Cast<Control>().ToArray()) row.Dispose();
        _messageList.Controls.Clear();
        var conversation = _selectedConversation!;
        var sender = conversation.Participants.Last();
        var message = new MTKChat.Contracts.StoredMessage(Guid.NewGuid(), Guid.NewGuid(), conversation.Id,
            sender.Id, "audio/wav", DateTimeOffset.Now, null, false,
            Array.Empty<MTKChat.Contracts.EncryptedPayload>(), null);
        _messageList.Controls.Add(BuildMessageBubble(message, "", null, CreateSilentVoiceSample()));
        ResizeBubbles();
    }

    private static byte[] CreateSilentVoiceSample()
    {
        using var buffer = new MemoryStream();
        using (var writer = new NAudio.Wave.WaveFileWriter(buffer, new NAudio.Wave.WaveFormat(16000, 16, 1)))
        {
            var silence = new byte[16000 * 2 * 3];
            writer.Write(silence, 0, silence.Length);
        }
        return buffer.ToArray();
    }

    private void ClearPendingVoice()
    {
        _voiceDraftVersion++;
        _voiceTimer.Stop();
        _voiceRecorder?.Dispose();
        _voiceRecorder = null;
        _voiceRecordButton.Text = "●  Ses";
        _voiceRecordButton.Kind = ButtonKind.Ghost;
        _draftPlayback?.Dispose();
        _draftPlayback = null;
        if (_pendingVoice is not null) CryptographicOperations.ZeroMemory(_pendingVoice.Bytes);
        _pendingVoice = null;
        if (_voiceDraftCard is not null) _voiceDraftCard.Visible = false;
        if (_imageDraftCard is not null) _imageDraftCard.Visible = _pendingImage is not null;
        if (_pendingImage is null)
        {
            if (_imageDraftRowStyle is not null) _imageDraftRowStyle.Height = 0;
            if (_composerHeightStyle is not null) _composerHeightStyle.Height = ComposerBaseHeight;
        }
    }
}
