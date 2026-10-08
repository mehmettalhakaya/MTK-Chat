using System.Security.Cryptography;
using System.Globalization;
using System.Text;
using DevExpress.XtraEditors;
using DevExpress.XtraEditors.Controls;
using MTKChat.Contracts;

namespace MTKChat.Desktop;

// The drawer and the standalone preview use the same editor, so photo validation
// and privacy writes cannot diverge between two superficially identical screens.
internal sealed class ProfileEditor : UserControl
{
    // Keep cards darker than a disabled command's Surface fill. Reusing that
    // exact fill for both erased the disabled button's footprint inside cards.
    internal static readonly Color ProfileCardFill = Color.FromArgb(22, 27, 52);
    private readonly ChatApiClient _api;
    private readonly AvatarCache _cache;
    private readonly Label _heading;
    private readonly Label _email;
    private ChatUser _presentedUser;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly AvatarView _avatar = new();
    private readonly Label _status = new() { AutoSize = true, ForeColor = Theme.Muted };
    private readonly ModernButton _save = Theme.Button("Kaydet", ButtonKind.Primary);
    private readonly ModernButton _select = Theme.Button("Fotoğraf seç", ButtonKind.Secondary);
    private readonly ModernButton _remove = Theme.Button("Kaldır", ButtonKind.Secondary);
    private readonly Label _fullName = TextLine("", 12, Theme.Text, 38);
    private readonly Label _personalHeading = TextLine("Kişisel bilgiler", 10, Theme.Text, 48);
    private readonly Label _nameCaption = TextLine("Ad ve Soyad:", 9, Theme.Muted, 28);
    private readonly Label _nameStatus = TextLine("", 9, Theme.Muted, 34);
    private readonly ModernButton _editName = Theme.Button("Düzenle", ButtonKind.Secondary);
    private readonly ModernButton _saveName = Theme.Button("Kaydet", ButtonKind.Primary);
    private readonly ModernButton _cancelName = Theme.Button("Vazgeç", ButtonKind.Secondary);
    private readonly TextEdit _firstName = new(), _lastName = new();
    private readonly TableLayoutPanel _nameRead = new(), _nameEdit = new();
    private readonly ToggleSwitch _lastSeen = new();
    private readonly ToggleSwitch _readReceipts = new();
    private readonly ModernButton _retryPrivacy = Theme.Button("Yeniden dene", ButtonKind.Secondary);
    private readonly TableLayoutPanel _photoSection = new();
    private readonly TableLayoutPanel _privacySection = new();
    private readonly RoundedPanel _identityCard = new(), _nameCard = new();
    private readonly TableLayoutPanel _nameHeader = new();
    private bool _reflowingProfile;
    private byte[]? _stagedPhoto;
    private bool _photoChanged;
    private bool _privacyLoaded;
    private bool _loading;
    private bool _lifetimeDisposed;
    private bool _privacyMode;
    private bool _editingName;
    private bool _savingName;
    private PrivacySettings? _loadedPrivacy;
    private string _photoStatus = "";
    private string _privacyStatus = "";
    internal bool IsBusy { get; private set; }
    internal ChatUser? UpdatedUser { get; private set; }
    internal event Action<ChatUser>? PhotoSaved;
    internal event Action<bool>? BusyChanged;

    internal ProfileEditor(ChatApiClient api, ChatUser user, AvatarCache cache, bool snapshotMode = false)
    {
        // Build every child before the DPI layout runs. Eager layout during
        // construction otherwise consumes the design baseline before cards exist.
        SuspendLayout();
        _api = api; _cache = cache; _presentedUser = user;
        BackColor = Theme.Sidebar; ForeColor = Theme.Text; Font = Theme.Font(10);
        AutoScaleMode = AutoScaleMode.Dpi; AutoScaleDimensions = new SizeF(96, 96);
        AutoScroll = true; Padding = new Padding(18, 12, 18, 20);
        var content = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1,
            RowCount = 4, Margin = Padding.Empty, BackColor = Color.Transparent };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _photoSection.Dock = DockStyle.Top; _photoSection.AutoSize = true;
        _photoSection.ColumnCount = 1; _photoSection.BackColor = Color.Transparent;
        _photoSection.Margin = Padding.Empty;
        _photoSection.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _identityCard.Dock = DockStyle.Top; _identityCard.Height = 244;
        _identityCard.Padding = new Padding(14, 12, 14, 12);
        _identityCard.Margin = new Padding(0, 0, 0, 14);
        _identityCard.FillColor = ProfileCardFill;
        _identityCard.GradientEndColor = Color.FromArgb(18, 23, 44);
        _identityCard.BorderColor = Theme.Divider; _identityCard.CornerRadius = 18;
        _identityCard.AccessibleName = "Profil kimliği ve fotoğrafı";
        var identity = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4,
            Margin = Padding.Empty, BackColor = Color.Transparent };
        identity.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        identity.RowStyles.Add(new RowStyle(SizeType.Absolute, 112));
        identity.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        identity.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        identity.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _avatar.Size = new Size(100, 100); _avatar.Anchor = AnchorStyles.None;
        _avatar.Margin = Padding.Empty;
        _avatar.Initials = UserPresentation.Initials(user.DisplayName);
        identity.Controls.Add(_avatar, 0, 0);
        _heading = TextLine(UserPresentation.Heading(user), 12, UserPresentation.RoleColor(user), 32);
        _email = TextLine(user.Email, 9, Theme.Muted, 38);
        _heading.Dock = _email.Dock = DockStyle.Fill;
        _heading.TextAlign = _email.TextAlign = ContentAlignment.MiddleCenter;
        _heading.Font = Theme.Font(12, FontStyle.Bold);
        identity.Controls.Add(_heading, 0, 1); identity.Controls.Add(_email, 0, 2);
        var actions = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1,
            Margin = Padding.Empty, BackColor = Color.Transparent };
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));
        actions.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _select.Dock = _remove.Dock = DockStyle.Fill;
        _select.Margin = new Padding(0, 4, 6, 4); _remove.Margin = new Padding(0, 4, 0, 4);
        _select.Click += (_, _) => SelectPhoto();
        _remove.ForeColor = Theme.Danger;
        _remove.Click += (_, _) => StageRemoval();
        actions.Controls.Add(_select, 0, 0); actions.Controls.Add(_remove, 1, 0);
        identity.Controls.Add(actions, 0, 3); _identityCard.Controls.Add(identity);
        _photoSection.Controls.Add(_identityCard);
        BuildNameSection();

        _privacySection.Dock = DockStyle.Top; _privacySection.AutoSize = true;
        _privacySection.ColumnCount = 1; _privacySection.BackColor = Color.Transparent;
        _privacySection.Margin = Padding.Empty;
        _privacySection.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _privacySection.Controls.Add(TextLine("Görünürlüğünü sen seç", 12, Theme.Text, 52));
        foreach (var (option, title) in new[] { (_lastSeen, "Son görülmemi göster"), (_readReceipts, "Okundu bilgisi gönder") })
        {
            var row = new RoundedPanel { Dock = DockStyle.Top, Height = 72, FillColor = Theme.Surface,
                BorderColor = Theme.Divider, CornerRadius = 12, Padding = new Padding(12),
                Margin = new Padding(0, 0, 0, 10) };
            var label = new Label { Dock = DockStyle.Fill, Text = title, AutoEllipsis = true,
                TextAlign = ContentAlignment.MiddleLeft, ForeColor = Theme.Text,
                BackColor = Color.Transparent, Font = Theme.Font(10), Margin = Padding.Empty };
            option.Dock = DockStyle.Right; option.Width = 64; option.Margin = Padding.Empty;
            option.Properties.ShowText = false;
            option.Properties.AllowThumbAnimation = true;
            option.Properties.Appearance.BackColor = Theme.Surface;
            option.Properties.Appearance.Options.UseBackColor = true;
            option.AccessibleName = title;
            option.Toggled += (_, _) => UpdateSaveState();
            row.Controls.Add(label); row.Controls.Add(option);
            _privacySection.Controls.Add(row);
        }
        _privacySection.Controls.Add(TextLine("Çevrimiçi durumun görünmeye devam eder.", 9, Theme.Muted, 54));
        _retryPrivacy.Dock = DockStyle.Top; _retryPrivacy.Height = 38;
        _retryPrivacy.Visible = false; _retryPrivacy.Margin = new Padding(0, 0, 0, 10);
        _retryPrivacy.Click += async (_, _) => await LoadPrivacyAsync(refresh: true);
        _privacySection.Controls.Add(_retryPrivacy);
        _status.Dock = DockStyle.Top; _status.Margin = new Padding(0, 14, 0, 12);
        _save.Dock = DockStyle.Top; _save.Height = 44; _save.Margin = Padding.Empty;
        _save.Click += async (_, _) => await SaveAsync();
        content.Controls.Add(_photoSection, 0, 0); content.Controls.Add(_privacySection, 0, 1);
        content.Controls.Add(_status, 0, 2); content.Controls.Add(_save, 0, 3);
        Controls.Add(content);
        if (snapshotMode) ApplyPrivacy(new PrivacySettings(true, true));
        else _ = cache.ApplyAsync(_avatar, user);
        SetMode(privacy: false);
        ResumeLayout(performLayout: true);
        ReflowProfileCards();
    }

    internal void SetMode(bool privacy)
    {
        _privacyMode = privacy;
        _photoSection.Visible = !privacy; _privacySection.Visible = privacy;
        UpdateVisibleStatus();
        UpdateSaveState();
    }

    internal void RefreshUser(ChatUser user)
    {
        if (IsDisposed || Disposing || user.Id != _presentedUser.Id) return;
        var photoChanged = user.PhotoVersion != _presentedUser.PhotoVersion;
        _presentedUser = user;
        _heading.Text = UserPresentation.Heading(user); _heading.ForeColor = UserPresentation.RoleColor(user);
        _email.Text = user.Email; _avatar.Initials = UserPresentation.Initials(user.DisplayName);
        _fullName.Text = FullName(user);
        // Presence/profile refreshes may bring newer account/photo data, but must
        // not replace the first/last-name draft while its editor is open.
        if (!_editingName) FillNameEditors();
        // A server refresh must never replace the user's unsubmitted preview.
        if (photoChanged && !_photoChanged) _ = _cache.ApplyAsync(_avatar, user);
        // Another session may change the confirmed name while this draft stays
        // open. Recompare against that new baseline without rewriting the draft:
        // a former no-op can become a change, and vice versa.
        UpdateSaveState();
    }

    internal async Task LoadPrivacyAsync(bool refresh = false)
    {
        if (IsBusy || _loading || IsDisposed || Disposing || (!refresh && _privacyLoaded)) return;
        if (_privacyLoaded && _loadedPrivacy is { } prior &&
            (_lastSeen.IsOn != prior.ShowLastSeen || _readReceipts.IsOn != prior.SendReadReceipts)) return;
        // Reopening reads other sessions' changes, unless this editor holds an
        // explicit unsaved choice. Failed refreshes never save stale defaults.
        _privacyLoaded = false; UpdateSaveState();
        _loading = true;
        _retryPrivacy.Visible = false; _retryPrivacy.Enabled = false;
        SetStatus("Gizlilik tercihleri yükleniyor…", privacy: true);
        try
        {
            var privacy = await _api.GetPrivacyAsync(_lifetime.Token);
            if (IsDisposed || Disposing) return;
            ApplyPrivacy(privacy); SetStatus("", privacy: true);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception)
        {
            if (!IsDisposed && !Disposing)
            {
                SetStatus("Gizlilik tercihleri alınamadı. Yeniden deneyebilirsin.", privacy: true);
                _retryPrivacy.Visible = true;
                // Keep the network failure inside this editor; a background
                // load must not become an unhandled async-void exception.
            }
        }
        finally
        {
            _loading = false;
            if (!IsDisposed && !Disposing) _retryPrivacy.Enabled = !IsBusy;
        }
    }

    private void ApplyPrivacy(PrivacySettings privacy)
    {
        _loadedPrivacy = privacy; _privacyLoaded = true;
        _lastSeen.IsOn = privacy.ShowLastSeen; _readReceipts.IsOn = privacy.SendReadReceipts;
        UpdateSaveState();
    }

    internal async Task SaveAsync()
    {
        if (IsBusy || IsDisposed || !_save.Enabled) return;
        var savingPrivacy = _privacyMode;
        IsBusy = true; UpdateSaveState(); SetStatus("Kaydediliyor…", savingPrivacy);
        BusyChanged?.Invoke(true);
        try
        {
            if (savingPrivacy)
            {
                var desired = new PrivacySettings(_lastSeen.IsOn, _readReceipts.IsOn);
                await _api.SavePrivacyAsync(desired, _lifetime.Token);
                if (IsDisposed || Disposing) return;
                _loadedPrivacy = desired;
            }
            else
            {
                var saved = await _api.SavePhotoAsync(_stagedPhoto, _lifetime.Token);
                if (IsDisposed || Disposing) return;
                UpdatedUser = saved; _photoChanged = false; RefreshUser(saved);
                ClearStagedPhoto();
                // Report a confirmed server response immediately; closing a drawer
                // does not have to return DialogResult.OK to propagate a new avatar.
                PhotoSaved?.Invoke(saved);
            }
            SetStatus("Kaydedildi.", savingPrivacy);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) { if (!IsDisposed && !Disposing) SetStatus("Kaydedilemedi: " + ex.Message, savingPrivacy); }
        finally
        {
            IsBusy = false;
            if (!IsDisposed && !Disposing) { UpdateSaveState(); BusyChanged?.Invoke(false); }
        }
    }

    private void UpdateSaveState()
    {
        _select.Enabled = _remove.Enabled = !IsBusy;
        _editName.Enabled = _cancelName.Enabled = _firstName.Enabled = _lastName.Enabled = !IsBusy;
        _saveName.Enabled = !IsBusy && _editingName &&
            TryNormalizeName(_firstName.Text, out var first) && TryNormalizeName(_lastName.Text, out var last) &&
            (first != (_presentedUser.FirstName ?? "") || last != (_presentedUser.LastName ?? ""));
        _lastSeen.Enabled = _readReceipts.Enabled = !IsBusy && _privacyLoaded;
        _retryPrivacy.Enabled = !IsBusy && !_loading;
        _save.Enabled = !IsBusy && (_privacyMode ? _privacyLoaded && _loadedPrivacy is not null &&
            (_lastSeen.IsOn != _loadedPrivacy.ShowLastSeen || _readReceipts.IsOn != _loadedPrivacy.SendReadReceipts) : _photoChanged);
        // There is no photo operation to submit in a clean profile. Keep the
        // large primary action for an actual draft, not an idle disabled footer.
        _save.Visible = _privacyMode || _photoChanged;
    }

    private void StageRemoval()
    {
        ClearStagedPhoto(); _photoChanged = true; _avatar.Tag = null; _avatar.SetPhoto(null);
        SetStatus("Fotoğrafın kaydettiğinde kaldırılacak.", privacy: false);
        UpdateSaveState();
    }

    private void SelectPhoto()
    {
        using var picker = new OpenFileDialog { Title = "Profil fotoğrafı", Filter = "Fotoğraf|*.jpg;*.jpeg;*.png;*.gif", CheckFileExists = true };
        if (picker.ShowDialog(FindForm()) != DialogResult.OK) return;
        try
        {
            byte[] bytes;
            if (Path.GetExtension(picker.FileName).Equals(".gif", StringComparison.OrdinalIgnoreCase))
            {
                if (new FileInfo(picker.FileName).Length > 2 * 1024 * 1024) throw new InvalidDataException("GIF en fazla 2 MB olabilir.");
                bytes = File.ReadAllBytes(picker.FileName);
                try
                {
                    using var stream = new MemoryStream(bytes);
                    using var image = Image.FromStream(stream, false, true);
                    if (image.Width > 256 || image.Height > 256 || image.GetFrameCount(System.Drawing.Imaging.FrameDimension.Time) > 120)
                        throw new InvalidDataException("GIF en fazla 256×256 piksel ve 120 kare olabilir.");
                    _avatar.SetEncodedPhoto(bytes);
                }
                catch { CryptographicOperations.ZeroMemory(bytes); throw; }
            }
            else
            {
                var photo = PhotoPreparation.ReadSquare(picker.FileName);
                bytes = photo.Jpeg;
                using (photo.Preview) _avatar.SetPhoto(photo.Preview);
            }
            ClearStagedPhoto(); _stagedPhoto = bytes;
            _photoChanged = true; _avatar.Tag = null;
            SetStatus("Önizleme hazır. Kaydederek uygula.", privacy: false);
            UpdateSaveState();
        }
        catch (Exception ex) { SetStatus("Fotoğraf açılamadı: " + ex.Message, privacy: false); }
    }

    private void SetStatus(string text, bool privacy)
    {
        // A delayed privacy read is allowed to finish in the cached editor, but
        // must not erase an unsaved photo preview/status after navigating away.
        if (privacy) _privacyStatus = text;
        else _photoStatus = text;
        UpdateVisibleStatus();
    }

    private void UpdateVisibleStatus()
    {
        _status.Text = _privacyMode
            ? _privacyStatus.Length > 0 ? _privacyStatus : !_privacyLoaded ? "Gizlilik tercihleri yükleniyor…" : ""
            : _photoStatus;
        _status.Visible = _status.Text.Length > 0;
    }

    private void ClearStagedPhoto()
    {
        if (_stagedPhoto is not null) CryptographicOperations.ZeroMemory(_stagedPhoto);
        _stagedPhoto = null;
    }

    internal void StageRemovalForQa() => StageRemoval();
    internal void ChangePrivacyForQa(bool seen, bool receipts) { _lastSeen.IsOn = seen; _readReceipts.IsOn = receipts; }
    internal bool SaveEnabledForQa => _save.Enabled;
    internal string HeadingForQa => _heading.Text;
    internal string StatusForQa => _status.Text;
    internal bool PrivacyLoadingForQa => _loading;
    internal ModernButton PrivacyRetryForQa => _retryPrivacy;
    internal PrivacySettings PrivacyChoiceForQa => new(_lastSeen.IsOn, _readReceipts.IsOn);

    private void BuildNameSection()
    {
        _nameCard.Dock = DockStyle.Top; _nameCard.Padding = new Padding(14);
        _nameCard.Margin = Padding.Empty; _nameCard.FillColor = ProfileCardFill;
        _nameCard.BorderColor = Theme.Divider; _nameCard.CornerRadius = 18;
        _nameCard.AccessibleName = "Kişisel bilgiler kartı";
        _nameCard.SizeChanged += (_, _) => ReflowProfileCards();
        _nameRead.Dock = DockStyle.Top; _nameRead.ColumnCount = 1; _nameRead.RowCount = 3;
        _nameRead.Margin = Padding.Empty;
        _nameRead.BackColor = Color.Transparent;
        _nameRead.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _nameRead.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        _nameRead.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        _nameRead.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _nameHeader.Dock = DockStyle.Fill; _nameHeader.ColumnCount = 2; _nameHeader.RowCount = 1;
        _nameHeader.Margin = Padding.Empty; _nameHeader.BackColor = Color.Transparent;
        _nameHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _nameHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 88));
        _nameHeader.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _personalHeading.Dock = DockStyle.Fill; _personalHeading.AutoEllipsis = false;
        _personalHeading.Font = Theme.Font(10, FontStyle.Bold);
        _nameCaption.Dock = DockStyle.Fill; _nameCaption.AutoEllipsis = false;
        _fullName.Dock = DockStyle.Fill; _fullName.Text = FullName(_presentedUser);
        _fullName.AutoEllipsis = false; _fullName.TextAlign = ContentAlignment.TopLeft;
        _fullName.Padding = new Padding(0, 8, 0, 0);
        _fullName.TextChanged += (_, _) => ReflowProfileCards();
        _fullName.AccessibleName = "Ad ve Soyad";
        _editName.Dock = DockStyle.Fill; _editName.Margin = new Padding(6, 3, 0, 3);
        _editName.AccessibleName = "İsim soyisim düzenle";
        _editName.Click += (_, _) => BeginNameEdit();
        _nameHeader.Controls.Add(_personalHeading, 0, 0); _nameHeader.Controls.Add(_editName, 1, 0);
        _nameRead.Controls.Add(_nameHeader, 0, 0); _nameRead.Controls.Add(_nameCaption, 0, 1);
        _nameRead.Controls.Add(_fullName, 0, 2);
        _nameCard.Controls.Add(_nameRead);

        _nameEdit.Dock = DockStyle.Top; _nameEdit.AutoSize = true; _nameEdit.ColumnCount = 1;
        _nameEdit.Margin = new Padding(0, 0, 0, 8); _nameEdit.BackColor = Color.Transparent;
        _nameEdit.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _nameEdit.Controls.Add(TextLine("Kişisel bilgiler", 10, Theme.Text, 34));
        foreach (var (edit, label) in new[] { (_firstName, "Ad"), (_lastName, "Soyad") })
        {
            _nameEdit.Controls.Add(TextLine(label, 9, Theme.Muted, 28));
            var frame = new RoundedPanel { Dock = DockStyle.Top, Height = 44, CornerRadius = 10,
                FillColor = Theme.Surface, BorderColor = Theme.Divider, Padding = new Padding(10, 7, 10, 7),
                Margin = new Padding(0, 0, 0, 6) };
            edit.Dock = DockStyle.Fill; edit.Margin = Padding.Empty; edit.AccessibleName = label;
            // A Unicode scalar may occupy two UTF-16 code units. The server's
            // 80-character rule counts scalars; do not truncate a surrogate pair.
            edit.Properties.AutoHeight = false; edit.Properties.MaxLength = 160;
            edit.Properties.BorderStyle = BorderStyles.NoBorder;
            edit.Properties.Appearance.BackColor = Theme.Surface; edit.Properties.Appearance.ForeColor = Theme.Text;
            edit.Properties.Appearance.Font = Theme.Font(10);
            edit.Properties.Appearance.Options.UseBackColor = edit.Properties.Appearance.Options.UseForeColor = edit.Properties.Appearance.Options.UseFont = true;
            edit.Properties.AppearanceFocused.Assign(edit.Properties.Appearance);
            edit.EditValueChanged += (_, _) => { if (!_savingName) { _nameStatus.Text = ""; UpdateSaveState(); } };
            frame.Controls.Add(edit); _nameEdit.Controls.Add(frame);
        }
        var buttons = new TableLayoutPanel { Dock = DockStyle.Top, Height = 48, ColumnCount = 2, RowCount = 1,
            Margin = Padding.Empty, BackColor = Color.Transparent };
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        buttons.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _cancelName.Dock = _saveName.Dock = DockStyle.Fill;
        _cancelName.Margin = new Padding(0, 3, 6, 3); _saveName.Margin = new Padding(0, 3, 0, 3);
        _cancelName.AccessibleName = "İsim düzenlemeyi iptal et"; _saveName.AccessibleName = "İsim soyisim kaydet";
        _cancelName.Click += (_, _) => CancelNameEdit();
        _saveName.Click += async (_, _) => await SaveNameAsync();
        buttons.Controls.Add(_cancelName, 0, 0); buttons.Controls.Add(_saveName, 1, 0);
        _nameEdit.Controls.Add(buttons); _nameEdit.Controls.Add(_nameStatus);
        _nameStatus.Visible = false;
        _nameStatus.TextChanged += (_, _) => { _nameStatus.Visible = _nameStatus.Text.Length > 0; ReflowProfileCards(); };
        _nameCard.Controls.Add(_nameEdit); _photoSection.Controls.Add(_nameCard);
        _nameEdit.Visible = false; FillNameEditors();
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        ReflowProfileCards();
    }

    private void ReflowProfileCards()
    {
        if (_reflowingProfile || _nameCard.Parent is null || IsDisposed || Disposing) return;
        _reflowingProfile = true;
        try
        {
            // Full names get an independent full-width, word-wrapped line. The
            // edit button no longer steals their text width in compact drawers.
            var width = Math.Max(1, _nameCard.ClientSize.Width - _nameCard.Padding.Horizontal);
            // The section title is longer than the old field name. Measure its
            // real remaining width beside Düzenle so a compact/DPI-scaled drawer
            // can wrap the title rather than clip it or crowd the action.
            var headingWidth = Math.Max(1, width - ScaleProfile(88) - _personalHeading.Padding.Horizontal);
            var headingTextHeight = TextRenderer.MeasureText(_personalHeading.Text, _personalHeading.Font,
                new Size(headingWidth, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height;
            var headingHeight = Math.Max(ScaleProfile(48), headingTextHeight + _personalHeading.Padding.Vertical + ScaleProfile(8));
            var captionHeight = Math.Max(ScaleProfile(28), _nameCaption.Font.Height + ScaleProfile(6));
            _nameRead.RowStyles[0].Height = headingHeight;
            _nameRead.RowStyles[1].Height = captionHeight;
            var lineHeight = TextRenderer.MeasureText(_fullName.Text.Length == 0 ? " " : _fullName.Text,
                _fullName.Font, new Size(width, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height;
            var nameHeight = Math.Max(_fullName.Font.Height, lineHeight) + _fullName.Padding.Vertical + ScaleProfile(8);
            _nameRead.Height = headingHeight + captionHeight + nameHeight;
            _nameCard.Height = _nameCard.Padding.Vertical + (_editingName
                ? _nameEdit.GetPreferredSize(new Size(width, 0)).Height
                : _nameRead.Height);
        }
        finally { _reflowingProfile = false; }
    }

    private int ScaleProfile(int logical) => Math.Max(1, (int)Math.Round(logical * DeviceDpi / 96f));

    private static string FullName(ChatUser user) => string.Join(" ",
        new[] { user.FirstName?.Trim(), user.LastName?.Trim() }.Where(value => !string.IsNullOrEmpty(value)));

    private void FillNameEditors()
    {
        _firstName.Text = _presentedUser.FirstName ?? "";
        _lastName.Text = _presentedUser.LastName ?? "";
    }

    private static bool TryNormalizeName(string value, out string normalized)
    {
        normalized = "";
        if (string.IsNullOrWhiteSpace(value) || value.Length > 512) return false;
        string candidate;
        try { candidate = value.Trim().Normalize(NormalizationForm.FormC); }
        catch (ArgumentException) { return false; }
        var result = new StringBuilder(candidate.Length);
        var count = 0; var hasLetter = false; var wasSpace = false; var wasSeparator = false;
        // Mirror the server's canonical own-profile-name rule. This is UI
        // validation only; authorization and validation remain server-side.
        foreach (var rune in candidate.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter or
                UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter)
            { hasLetter = true; wasSpace = wasSeparator = false; }
            else if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark)
            { if (count == 0 || wasSeparator) return false; wasSpace = false; }
            else if (category == UnicodeCategory.SpaceSeparator)
            {
                if (wasSpace) continue;
                wasSpace = wasSeparator = true;
                if (++count > 80) return false;
                result.Append(' '); continue;
            }
            else if (rune.Value is '-' or '\'' or '\u2019')
            { if (count == 0) return false; wasSpace = false; wasSeparator = true; }
            else return false;
            if (++count > 80) return false;
            result.Append(rune.ToString());
        }
        if (!hasLetter || wasSeparator) return false;
        normalized = result.ToString(); return true;
    }

    internal void BeginNameEdit()
    {
        if (IsBusy || IsDisposed || Disposing || _editingName) return;
        FillNameEditors(); _editingName = true; _nameStatus.Text = "";
        _nameRead.Visible = false; _nameEdit.Visible = true; UpdateSaveState();
        ReflowProfileCards();
        _firstName.Focus(); _firstName.SelectAll();
    }

    internal void CancelNameEdit()
    {
        if (IsBusy || IsDisposed || Disposing) return;
        _editingName = false; _nameRead.Visible = true; _nameEdit.Visible = false;
        _fullName.Text = FullName(_presentedUser); FillNameEditors(); UpdateSaveState();
        ReflowProfileCards();
    }

    internal async Task SaveNameAsync()
    {
        if (IsDisposed || Disposing) return;
        UpdateSaveState();
        if (IsBusy || !_saveName.Enabled) return;
        if (!TryNormalizeName(_firstName.Text, out var firstName) || !TryNormalizeName(_lastName.Text, out var lastName)) return;
        _savingName = IsBusy = true; UpdateSaveState(); _nameStatus.ForeColor = Theme.Muted;
        _nameStatus.Text = "Kaydediliyor…"; BusyChanged?.Invoke(true);
        try
        {
            var saved = await _api.SaveProfileNameAsync(firstName, lastName, _lifetime.Token);
            if (IsDisposed || Disposing) return;
            if (saved.Id != _presentedUser.Id || saved.IsAgent ||
                saved.FirstName != firstName || saved.LastName != lastName)
                throw new InvalidDataException("Sunucudan geçersiz profil bilgisi geldi.");
            UpdatedUser = saved; RefreshUser(saved);
            _editingName = false; _nameEdit.Visible = false; _nameRead.Visible = true;
            ReflowProfileCards();
            FillNameEditors();
            // Existing subscriber updates the logged-in session and rail avatar.
            // Keep its compatibility name: both photo and name writes return ChatUser.
            PhotoSaved?.Invoke(saved);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!IsDisposed && !Disposing)
            {
                _nameStatus.ForeColor = Theme.Danger;
                // Keep backend/network implementation details out of the drawer.
                _nameStatus.Text = ex switch
                {
                    InvalidDataException => "Kaydedilemedi: Sunucudan geçersiz profil bilgisi geldi.",
                    ChatApiException { StatusCode: System.Net.HttpStatusCode.BadRequest } => "Kaydedilemedi: İsim ve soyisimi kontrol et.",
                    ChatApiException { StatusCode: System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.Unauthorized } => "Kaydedilemedi: Tekrar giriş yapıp dene.",
                    ChatApiException { StatusCode: System.Net.HttpStatusCode.TooManyRequests } => "Kaydedilemedi: Biraz bekleyip tekrar dene.",
                    _ => "Kaydedilemedi: Bağlantını kontrol edip tekrar dene."
                };
            }
        }
        finally
        {
            _savingName = IsBusy = false;
            if (!IsDisposed && !Disposing) { UpdateSaveState(); BusyChanged?.Invoke(false); }
        }
    }

    internal string FullNameForQa => _fullName.Text;
    internal bool NameEditingForQa => _editingName;
    internal bool NameSaveEnabledForQa => _saveName.Enabled;
    internal string NameStatusForQa => _nameStatus.Text;
    internal (string FirstName, string LastName) NameDraftForQa => (_firstName.Text, _lastName.Text);
    internal void SetNameDraftForQa(string first, string last) { _firstName.Text = first; _lastName.Text = last; }
    internal IReadOnlyList<ModernButton> NameButtonsForQa => [_editName, _saveName, _cancelName];
    internal IReadOnlyList<ModernButton> PhotoButtonsForQa => [_select, _remove, _save];
    internal IReadOnlyList<RoundedPanel> ProfileCardsForQa => [_identityCard, _nameCard];
    internal Label FullNameLabelForQa => _fullName;
    internal IReadOnlyList<Label> PersonalLabelsForQa => [_personalHeading, _nameCaption];

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_lifetimeDisposed)
        {
            _lifetimeDisposed = true;
            _lifetime.Cancel(); ClearStagedPhoto(); _lifetime.Dispose();
        }
        base.Dispose(disposing);
    }

    private static Label TextLine(string text, float size, Color color, int height) => new()
    {
        Text = text, Font = Theme.Font(size), ForeColor = color, BackColor = Color.Transparent,
        Dock = DockStyle.Top, Height = height, Margin = Padding.Empty, AutoEllipsis = true, UseMnemonic = false,
        TextAlign = ContentAlignment.MiddleLeft
    };
}
