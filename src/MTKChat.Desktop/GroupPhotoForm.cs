using MTKChat.Contracts;

namespace MTKChat.Desktop;

internal sealed class GroupPhotoForm : ModernForm
{
    private readonly ChatApiClient _api;
    private readonly Guid _conversationId;
    private readonly AvatarView _avatar = new();
    private readonly Label _status = new();
    private byte[]? _staged;
    private bool _changed;
    private bool _busy;
    public GroupPhotoResult? UpdatedPhoto { get; private set; }

    internal GroupPhotoForm(ChatApiClient api, ConversationSummary conversation, AvatarCache cache, bool snapshotMode = false)
    {
        _api = api;
        _conversationId = conversation.Id;
        Text = "MTK Chat · Grup fotoğrafı";
        Icon = Theme.AppIcon();
        ClientSize = new Size(430, 445);
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Theme.Canvas;
        ForeColor = Theme.Text;
        Font = Theme.Font(10);
        Controls.Add(new Label { Text = "Grup fotoğrafı", Bounds = new Rectangle(28, 25, 370, 38),
            Font = Theme.Font(19, FontStyle.Bold), ForeColor = Theme.Text });
        _avatar.SetBounds(163, 83, 104, 104);
        _avatar.Initials = UserPresentation.Initials(conversation.Title);
        Controls.Add(_avatar);
        if (!snapshotMode) _ = cache.ApplyGroupAsync(_avatar, conversation);
        Controls.Add(new Label { Text = conversation.Title, Bounds = new Rectangle(30, 205, 370, 27),
            TextAlign = ContentAlignment.MiddleCenter, Font = Theme.Font(11, FontStyle.Bold), ForeColor = Theme.Text });
        var choose = Theme.Button("Fotoğraf seç", ButtonKind.Secondary);
        choose.SetBounds(57, 249, 150, 42);
        choose.Click += (_, _) =>
        {
            using var picker = new OpenFileDialog { Title = "Grup fotoğrafı", Filter = "Fotoğraf|*.jpg;*.jpeg;*.png", CheckFileExists = true };
            if (picker.ShowDialog(this) != DialogResult.OK) return;
            try
            {
                var (jpeg, preview) = PhotoPreparation.ReadSquare(picker.FileName);
                _avatar.Tag = null;
                using (preview) _avatar.SetPhoto(preview);
                _staged = jpeg;
                _changed = true;
                _status.Text = "Önizleme hazır. Kaydederek uygula.";
            }
            catch (Exception ex) { _status.Text = ex.Message; }
        };
        Controls.Add(choose);
        var remove = Theme.Button("Kaldır", ButtonKind.Secondary);
        remove.ForeColor = Theme.Danger;
        remove.SetBounds(222, 249, 150, 42);
        remove.Click += (_, _) => { _staged = null; _changed = true; _avatar.Tag = null; _avatar.SetPhoto(null); _status.Text = "Kaydedince grup fotoğrafı kaldırılacak."; };
        Controls.Add(remove);
        Controls.Add(new Label { Text = "Bu sohbetteki üyeler fotoğrafı değiştirebilir.", Bounds = new Rectangle(30, 311, 370, 26),
            TextAlign = ContentAlignment.MiddleCenter, Font = Theme.Font(9), ForeColor = Theme.Muted });
        _status.SetBounds(30, 340, 370, 28);
        _status.ForeColor = Theme.Warning;
        Controls.Add(_status);
        var save = Theme.Button("Değişiklikleri kaydet", ButtonKind.Primary);
        save.SetBounds(30, 383, 370, 44);
        save.Click += async (_, _) =>
        {
            if (!_changed) { Close(); return; }
            _busy = true;
            choose.Enabled = remove.Enabled = save.Enabled = false;
            _status.Text = "Fotoğraf kaydediliyor…";
            try
            {
                UpdatedPhoto = await _api.SaveGroupPhotoAsync(_conversationId, _staged);
                _busy = false;
                DialogResult = DialogResult.OK;
                Close();
            }
            catch (Exception ex) { _status.Text = ex.Message; }
            finally { _busy = false; choose.Enabled = remove.Enabled = save.Enabled = true; }
        };
        Controls.Add(save);
        FormClosing += (_, e) => { if (_busy) e.Cancel = true; };
        WrapFixedContent();
    }
}
