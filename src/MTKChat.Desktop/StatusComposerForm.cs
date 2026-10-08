using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Security.Cryptography;
using System.Text;
using DevExpress.XtraEditors;
using DevExpress.XtraEditors.Controls;
using MTKChat.Contracts;
using MTKChat.Cryptography;

namespace MTKChat.Desktop;

internal sealed record StatusDraft(byte[] Content, string Kind, Guid[] Audience);

internal sealed class StatusComposerForm : ModernForm
{
    private readonly ChatUser[] _users;
    private readonly HashSet<Guid> _audience = [];
    private readonly MemoEdit _text = new();
    private readonly TextEdit _search = new();
    private readonly PictureBox _preview = new() { SizeMode = PictureBoxSizeMode.Zoom, BackColor = Theme.Surface };
    private readonly Label _selection = new(), _error = new();
    private readonly ModernButton _publish, _photo, _remove, _cancel;
    private readonly PickerPeopleViewport _people;
    private readonly ChatApiClient _api;
    private readonly Func<StatusDraft, CancellationToken, Task<SendStatusRequest>> _prepare;
    private readonly CancellationTokenSource _lifetime = new();
    private byte[]? _image;
    private SendStatusRequest? _prepared;
    private bool _busy;
    internal bool Published { get; private set; }

    internal StatusComposerForm(ChatApiClient api, IEnumerable<ChatUser> users, AvatarCache? avatars,
        Func<StatusDraft, CancellationToken, Task<SendStatusRequest>> prepare)
    {
        _api = api; _prepare = prepare;
        _users = users.Where(u => !u.IsAgent).DistinctBy(u => u.Id).OrderBy(u => u.DisplayName).ToArray();
        SuspendLayout(); AutoScaleMode = AutoScaleMode.Dpi; AutoScaleDimensions = new SizeF(96, 96);
        Text = "MTK Chat · Yeni durum"; Icon = Theme.AppIcon(); BackColor = Theme.Canvas;
        Size = new Size(620, 800); MinimumSize = new Size(480, 720);
        MaximizeBox = false; MinimizeBox = false; StartPosition = FormStartPosition.CenterParent;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 9,
            Padding = new Padding(24, 18, 24, 18), BackColor = Theme.Canvas, Margin = Padding.Empty };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var height in new[] { 62, 150, 50, 50, 32 }) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        foreach (var height in new[] { 24, 38, 56 }) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        layout.Controls.Add(new Label { Text = "Yeni durum", Dock = DockStyle.Fill, ForeColor = Theme.Text,
            Font = Theme.Font(21, FontStyle.Bold), Margin = Padding.Empty }, 0, 0);
        var content = new RoundedPanel { Dock = DockStyle.Fill, FillColor = Theme.Surface,
            BorderColor = Theme.Divider, CornerRadius = 16, Padding = new Padding(14), Margin = new Padding(0, 0, 0, 8) };
        _text.Dock = DockStyle.Fill; _text.Properties.BorderStyle = BorderStyles.NoBorder;
        _text.Properties.NullValuePrompt = "Paylaşmak istediğin bir şey yaz..."; _text.Properties.MaxLength = 2000;
        // Native MemoEdit scrollbars otherwise remain white over the dark card;
        // keyboard/caret and mouse-wheel scrolling still belong to the editor.
        _text.Properties.ScrollBars = ScrollBars.None;
        _text.Properties.Appearance.BackColor = Theme.Surface; _text.Properties.Appearance.ForeColor = Theme.Text;
        _text.Properties.Appearance.Font = Theme.Font(12); _text.Properties.Appearance.Options.UseFont = true;
        _text.Properties.Appearance.Options.UseForeColor = _text.Properties.Appearance.Options.UseBackColor = true;
        _preview.Dock = DockStyle.Fill; _preview.Visible = false;
        content.Controls.Add(_text); content.Controls.Add(_preview); layout.Controls.Add(content, 0, 1);
        var photoActions = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 1, ColumnCount = 2, Margin = Padding.Empty };
        photoActions.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        photoActions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60)); photoActions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));
        _photo = Theme.Button("Görsel seç"); _remove = Theme.Button("Görseli kaldır", ButtonKind.Danger);
        _photo.Dock = _remove.Dock = DockStyle.Fill; _photo.Margin = new Padding(0, 0, 8, 8); _remove.Margin = new Padding(0, 0, 0, 8);
        _remove.Visible = false; _photo.Click += async (_, _) => await PickPhotoAsync();
        _remove.Click += (_, _) => { ClearImage(); Changed(); };
        photoActions.Controls.Add(_photo, 0, 0); photoActions.Controls.Add(_remove, 1, 0); layout.Controls.Add(photoActions, 0, 2);
        _search.Dock = DockStyle.Fill; _search.Properties.AutoHeight = false; _search.Margin = new Padding(0, 0, 0, 8);
        _search.Properties.NullValuePrompt = "Durumu görebilecek kişilerde ara...";
        _search.Properties.Appearance.BackColor = Theme.Surface; _search.Properties.Appearance.ForeColor = Theme.Text;
        _search.Properties.Appearance.Font = Theme.Font(10); _search.Properties.Appearance.Options.UseFont = true;
        _search.Properties.Appearance.Options.UseForeColor = _search.Properties.Appearance.Options.UseBackColor = true;
        layout.Controls.Add(_search, 0, 3);
        _selection.Dock = DockStyle.Fill; _selection.ForeColor = Theme.Muted; _selection.Font = Theme.Font(9);
        _selection.Margin = Padding.Empty; layout.Controls.Add(_selection, 0, 4);
        _people = new PickerPeopleViewport(true, avatars, id => _audience.Contains(id), SelectPerson)
        { Dock = DockStyle.Fill, AccessibleName = "Durumu görebilecek kişiler", Margin = Padding.Empty };
        layout.Controls.Add(_people, 0, 5);
        layout.Controls.Add(new Label { Text = "Yalnızca seçtiğin kişiler · 24 saat · Şifreli", Dock = DockStyle.Fill,
            ForeColor = Theme.Muted, Font = Theme.Font(9), Margin = Padding.Empty }, 0, 6);
        _error.Dock = DockStyle.Fill; _error.ForeColor = Theme.Warning; _error.Font = Theme.Font(9);
        _error.AutoEllipsis = true; _error.Margin = Padding.Empty; layout.Controls.Add(_error, 0, 7);
        var actions = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 1, ColumnCount = 2, Margin = Padding.Empty };
        actions.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110)); actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _cancel = Theme.Button("Vazgeç"); _publish = Theme.Button("Durumu paylaş", ButtonKind.Primary);
        _cancel.Dock = _publish.Dock = DockStyle.Fill; _cancel.Margin = new Padding(0, 6, 10, 0); _publish.Margin = new Padding(0, 6, 0, 0);
        _cancel.Click += (_, _) => Close(); _publish.Click += async (_, _) => await PublishAsync();
        actions.Controls.Add(_cancel, 0, 0); actions.Controls.Add(_publish, 1, 0); layout.Controls.Add(actions, 0, 8);
        _text.TextChanged += (_, _) => Changed(); _search.TextChanged += (_, _) => FilterPeople();
        FormClosing += (_, e) => { if (_busy) e.Cancel = true; };
        Controls.Add(layout); FilterPeople(); UpdateActions(); ResumeLayout(true);
    }

    private void SelectPerson(Guid id, bool selected)
    {
        if (_busy || !_users.Any(u => u.Id == id)) return;
        if (selected && !_audience.Contains(id) && _audience.Count >= 50) { _error.Text = "En fazla 50 kişi seçebilirsin."; _people.RefreshSelection(); return; }
        if (selected) _audience.Add(id); else _audience.Remove(id);
        Changed(); _people.RefreshSelection();
    }
    private void Changed() { _prepared = null; _error.Text = ""; UpdateActions(); }
    private void FilterPeople() => _people.SetUsers(_users.Where(u => u.DisplayName.Contains(_search.Text.Trim(), StringComparison.CurrentCultureIgnoreCase)).ToArray());
    private void UpdateActions()
    {
        _selection.Text = $"{_audience.Count} kişi seçildi";
        _publish.Enabled = !_busy && _audience.Count is >= 1 and <= 50 && (_image is not null || _text.Text.Trim().Length is >= 1 and <= 2000);
        _publish.Text = _busy ? "Paylaşılıyor..." : "Durumu paylaş";
        _text.Enabled = _search.Enabled = _people.Enabled = _photo.Enabled = _remove.Enabled = _cancel.Enabled = !_busy;
    }
    private async Task PickPhotoAsync()
    {
        if (_busy) return;
        using var picker = new OpenFileDialog { Title = "Durum görseli", Filter = "Görseller|*.jpg;*.jpeg;*.png", CheckFileExists = true };
        if (picker.ShowDialog(this) != DialogResult.OK) return;
        _busy = true; UpdateActions();
        try
        {
            var image = await Task.Run(() => PreparePhoto(picker.FileName), _lifetime.Token);
            if (IsDisposed || Disposing || _lifetime.IsCancellationRequested) { CryptographicOperations.ZeroMemory(image); return; }
            ClearImage(); _image = image;
            using var stream = new MemoryStream(image); using var decoded = Image.FromStream(stream);
            _preview.Image = new Bitmap(decoded); _preview.Visible = true; _text.Visible = false; _remove.Visible = true;
            Changed();
        }
        catch (Exception ex) { if (!IsDisposed) _error.Text = "Görsel açılamadı: " + ex.Message; }
        finally { _busy = false; if (!IsDisposed && !Disposing) UpdateActions(); }
    }
    internal static byte[] PreparePhoto(string path)
    {
        if (new FileInfo(path).Length is < 1 or > 8 * 1024 * 1024) throw new InvalidDataException("Görsel en fazla 8 MB olabilir.");
        using var stream = File.OpenRead(path); using var source = Image.FromStream(stream, false, true);
        if ((long)source.Width * source.Height > 24_000_000) throw new InvalidDataException("Görsel en fazla 24 megapiksel olabilir.");
        for (var edge = 1280; edge >= 320; edge /= 2)
        {
            var scale = Math.Min(1d, (double)edge / Math.Max(source.Width, source.Height));
            using var resized = new Bitmap(Math.Max(1, (int)(source.Width * scale)), Math.Max(1, (int)(source.Height * scale)));
            using (var g = Graphics.FromImage(resized)) { g.Clear(Color.White); g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.DrawImage(source, new Rectangle(Point.Empty, resized.Size)); }
            using var encoded = new MemoryStream(); using var quality = new EncoderParameters(1);
            quality.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 80L);
            resized.Save(encoded, ImageCodecInfo.GetImageEncoders().Single(c => c.FormatID == ImageFormat.Jpeg.Guid), quality);
            if (encoded.Length <= StatusCryptography.MaximumContentBytes) return encoded.ToArray();
        }
        throw new InvalidDataException("Görsel sıkıştırılamadı; daha küçük bir görsel seç.");
    }
    private async Task PublishAsync()
    {
        if (!_publish.Enabled || _busy) return;
        _busy = true; UpdateActions(); byte[]? content = null;
        try
        {
            content = _image?.ToArray() ?? Encoding.UTF8.GetBytes(_text.Text.Trim());
            var prepared = _prepared ??= await _prepare(new StatusDraft(content, _image is null ? "text" : "image/jpeg", _audience.ToArray()), _lifetime.Token);
            var result = await _api.PublishStatusAsync(prepared, _lifetime.Token);
            if (IsDisposed || Disposing || _lifetime.IsCancellationRequested) return;
            var authors = prepared.Payloads.Select(p => p.RecipientId).Except(_audience).ToArray();
            if (authors.Length != 1 || result.SenderId != authors[0] || result.Id == Guid.Empty || result.Payload is null ||
                result.ExpiresAt <= DateTimeOffset.UtcNow || result.ExpiresAt - result.CreatedAt < TimeSpan.FromDays(1) - TimeSpan.FromMinutes(5) ||
                result.ExpiresAt - result.CreatedAt > TimeSpan.FromDays(1) + TimeSpan.FromMinutes(5) ||
                result.ClientStatusId != prepared.ClientStatusId || result.ScopeId != StatusProtocol.ScopeId ||
                result.Kind != prepared.Kind || result.CreatedAt != prepared.CreatedAt || result.Ciphertext != prepared.Ciphertext ||
                result.Payload.RecipientId != result.SenderId || !prepared.Payloads.Contains(result.Payload))
                throw new InvalidDataException("Durum yanıtı doğrulanamadı.");
            Published = true; _busy = false; DialogResult = DialogResult.OK; Close();
        }
        catch (Exception ex) { if (!IsDisposed) _error.Text = ex is ChatTransportException ? "Paylaşım doğrulanamadı. Tekrar dene; aynı durum iki kez oluşturulmaz." : ex.Message; }
        finally { if (content is not null) CryptographicOperations.ZeroMemory(content); _busy = false; if (!IsDisposed) UpdateActions(); }
    }
    private void ClearImage()
    {
        if (_image is not null) CryptographicOperations.ZeroMemory(_image); _image = null;
        var old = _preview.Image; _preview.Image = null; old?.Dispose();
        _preview.Visible = false; _text.Visible = true; _remove.Visible = false;
    }
    protected override void Dispose(bool disposing)
    {
        // A modeless Close can dispose the form before its owner's using block.
        // Release the token/images only once so the second Dispose stays harmless.
        if (disposing && !_resourcesDisposed)
        {
            _resourcesDisposed = true;
            _lifetime.Cancel(); ClearImage(); _lifetime.Dispose();
        }
        base.Dispose(disposing);
    }
    private bool _resourcesDisposed;
    internal void SelectForQa(Guid id, bool value) => SelectPerson(id, value);
    internal void TextForQa(string value) => _text.Text = value;
    internal bool PublishEnabledForQa => _publish.Enabled;
    internal Guid[] AudienceForQa => _audience.ToArray();
    internal PickerPeopleViewport PeopleForQa => _people;
    internal Task PublishForQa() => PublishAsync();
    internal bool BusyForQa => _busy;
    internal string FeedbackForQa => _error.Text;
    internal SendStatusRequest? PreparedForQa => _prepared;
}
