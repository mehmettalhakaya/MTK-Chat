using DevExpress.XtraEditors.Controls;
using MTKChat.Contracts;

namespace MTKChat.Desktop;

internal sealed partial class MainForm
{
    private const int ComposerBaseHeight = 100;
    private bool _participantsShown;
    private readonly ModernButton _participantsButton = Theme.GlyphButton("\uE716", "Katılımcıları göster");

    // These are real controls over a wallpaper, not a flattened screenshot. Each
    // action keeps its existing encrypted-message, permission and draft workflow.
    private Control BuildReferenceSidebarContent()
    {
        var surface = new ReferenceSurface { Dock = DockStyle.Fill, Tint = Color.FromArgb(244, Theme.Sidebar) };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5,
            BackColor = Color.Transparent, Padding = new Padding(16, 8, 16, 12), Margin = Padding.Empty };
        foreach (var height in new[] { 78, 54, 58 })
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        var filterHeight = new RowStyle(SizeType.Absolute, 42);
        layout.RowStyles.Add(filterHeight);
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(new StudioBrandPanel { Dock = DockStyle.Fill, Margin = Padding.Empty }, 0, 0);

        var search = new RoundedPanel { Dock = DockStyle.Fill, FillColor = Color.FromArgb(240, Theme.Surface),
            BorderColor = Theme.Divider, CornerRadius = 12, Margin = new Padding(0, 0, 0, 8),
            Padding = new Padding(13, 5, 10, 5) };
        var searchGlyph = Theme.GlyphButton("\uE721", "Sohbetlerde ara");
        searchGlyph.Dock = DockStyle.Left; searchGlyph.Width = 26; searchGlyph.ForeColor = Theme.Muted;
        searchGlyph.TabStop = false; searchGlyph.CornerRadius = 8;
        searchGlyph.Click += (_, _) => _premiumSearch.Focus();
        var shortcut = new Label { Dock = DockStyle.Right, Width = 62, Text = "Ctrl + K", Font = Theme.Font(7.5f),
            BackColor = Color.Transparent, ForeColor = Theme.Muted, TextAlign = ContentAlignment.MiddleRight };
        _premiumSearch.Dock = DockStyle.Fill;
        _premiumSearch.Properties.AutoHeight = false;
        _premiumSearch.Properties.BorderStyle = BorderStyles.NoBorder;
        _premiumSearch.Properties.NullValuePrompt = "Sohbetlerde ara...";
        StyleReferenceEditor(_premiumSearch, Theme.Surface, 10.5f);
        _premiumSearch.TextChanged += (_, _) => FilterConversations(_premiumSearch.Text);
        search.Controls.Add(_premiumSearch); search.Controls.Add(shortcut); search.Controls.Add(searchGlyph);
        layout.Controls.Add(search, 0, 1);

        var create = Theme.Button("＋  Yeni Sohbet", ButtonKind.Primary);
        create.Dock = DockStyle.Fill; create.Margin = new Padding(0, 2, 0, 10);
        create.Font = Theme.Font(11, FontStyle.Bold); create.CornerRadius = 12;
        var createMenu = Theme.ContextMenu();
        createMenu.Items.Add("Yeni sohbet", null, async (_, _) => await NewConversationAsync(false));
        createMenu.Items.Add("Yeni grup", null, async (_, _) => await NewConversationAsync(true));
        createMenu.Items.Add(new ToolStripSeparator());
        var joinInvite = createMenu.Items.Add("Davet bağlantısıyla katıl", null, async (_, _) => await OpenJoinGroupInviteAsync());
        createMenu.Opening += (_, _) => joinInvite.Enabled = !_openingConversation && !_sending && _voiceRecorder is null && !_voiceStopping;
        create.Click += (_, _) => createMenu.Show(create, new Point(0, create.Height));
        create.Disposed += (_, _) => createMenu.Dispose();
        layout.Controls.Add(create, 0, 2);

        var filters = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent, Margin = Padding.Empty,
            Name = "ConversationFilters", AccessibleName = "Sohbet filtreleri" };
        foreach (var (key, label) in new[] { ("all", "Tümü"), ("unread", "Okunmamış"),
                     ("favorites", "Favoriler"), ("direct", "Kişiler"), ("group", "Gruplar"), ("archived", "Arşiv") })
        {
            var button = Theme.Button(label, key == "all" ? ButtonKind.Secondary : ButtonKind.Ghost);
            button.Font = Theme.Font(9f); button.CornerRadius = 18;
            button.Margin = Padding.Empty; button.AccessibleName = label + " sohbet filtresi";
            button.TabIndex = _filterButtons.Count;
            button.Click += (_, _) => SetConversationFilter(key);
            _filterButtons.Add(key, button); filters.Controls.Add(button);
            button.FontChanged += (_, _) => ArrangeConversationFilters(filters, filterHeight);
            button.TextChanged += (_, _) => ArrangeConversationFilters(filters, filterHeight);
        }
        filters.Layout += (_, _) => ArrangeConversationFilters(filters, filterHeight);
        filters.DpiChangedAfterParent += (_, _) => ArrangeConversationFilters(filters, filterHeight);
        filters.HandleCreated += (_, _) => ArrangeConversationFilters(filters, filterHeight);
        filters.VisibleChanged += (_, _) => ArrangeConversationFilters(filters, filterHeight);
        layout.Controls.Add(filters, 0, 3);

        _conversationList.Dock = DockStyle.Fill;
        _conversationList.FlowDirection = FlowDirection.TopDown; _conversationList.WrapContents = false;
        _conversationList.AutoScroll = true; _conversationList.BackColor = Color.Transparent;
        _conversationList.Padding = Padding.Empty; _conversationList.Margin = Padding.Empty;
        _conversationList.Resize += (_, _) => ResizeConversationCards();
        var conversationViewport = new ModernConversationViewport(_conversationList)
        { Dock = DockStyle.Fill, Margin = Padding.Empty };
        var conversationHost = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent, Margin = Padding.Empty };
        _conversationFilterEmpty = new ConversationFilterEmptyState { Dock = DockStyle.Fill, Visible = false };
        _conversationFilterEmpty.ShowAllRequested += (_, _) =>
        {
            _premiumSearch.Text = "";
            SetConversationFilter("all");
        };
        conversationHost.Controls.Add(conversationViewport);
        conversationHost.Controls.Add(_conversationFilterEmpty);
        _conversationFilterEmpty.BringToFront();
        layout.Controls.Add(conversationHost, 0, 4);

        // Account actions belong exclusively to the navigation rail. The list
        // now fills the remaining height; no old footer or reserved blank row.
        surface.Controls.Add(layout);
        return surface;
    }

    private Control BuildReferenceChat()
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 4, ColumnCount = 1,
            BackColor = Color.Transparent, Margin = Padding.Empty, Padding = Padding.Empty };
        var headerHeight = new RowStyle(SizeType.Absolute, 92);
        layout.RowStyles.Add(headerHeight);
        var pinnedHeight = new RowStyle(SizeType.Absolute, 0);
        layout.RowStyles.Add(pinnedHeight);
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _composerHeightStyle = new RowStyle(SizeType.Absolute, ComposerBaseHeight);
        layout.RowStyles.Add(_composerHeightStyle);
        layout.Controls.Add(BuildReferenceHeader(headerHeight), 0, 0);
        layout.Controls.Add(BuildPinnedMessageBanner(pinnedHeight), 0, 1);
        layout.Controls.Add(BuildPremiumMessages(), 0, 2);
        layout.Controls.Add(BuildReferenceComposer(), 0, 3);
        var surface = new ReferenceSurface { Dock = DockStyle.Fill };
        surface.Controls.Add(layout);
        _chatSurface = surface;
        surface.Resize += (_, _) => ArrangeDetails();
        return surface;
    }

    private Control BuildReferenceHeader(RowStyle headerHeight)
    {
        var host = new ReferenceSurface { Dock = DockStyle.Fill, Tint = Color.FromArgb(120, Theme.Sidebar),
            Padding = new Padding(22, 12, 16, 12) };
        var row = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1,
            BackColor = Color.Transparent, Margin = Padding.Empty };
        row.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 62));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 224));
        _headerAvatar.Size = new Size(52, 52); _headerAvatar.Anchor = AnchorStyles.Left;
        _headerAvatar.Initials = "MTK"; _headerAvatar.AvatarColor = Theme.Accent;
        _headerAvatar.AccessibleName = "Grup fotoğrafını düzenle"; _headerAvatar.Cursor = Cursors.Hand;
        _headerAvatar.Click += async (_, _) => await OpenGroupPhotoAsync();
        var title = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent,
            Margin = Padding.Empty, Padding = new Padding(5, 7, 0, 0) };
        _conversationTitle.Text = "Sohbet seçin"; _conversationTitle.Dock = DockStyle.Top; _conversationTitle.Height = 34;
        _conversationTitle.Font = Theme.Font(17, FontStyle.Bold); _conversationTitle.ForeColor = Theme.Text;
        _conversationTitle.AutoEllipsis = true; _conversationTitle.UseMnemonic = false;
        _conversationMeta.Dock = DockStyle.Top; _conversationMeta.Height = 22; _conversationMeta.Font = Theme.Font(9.5f);
        _conversationMeta.ForeColor = Theme.Muted; _conversationMeta.AutoEllipsis = true; _conversationMeta.UseMnemonic = false;
        title.Controls.Add(_conversationMeta); title.Controls.Add(_conversationTitle);
        var actions = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1,
            BackColor = Color.Transparent, Margin = Padding.Empty };
        actions.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var width in new[] { 48, 48, 48 }) actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, width));
        _securityStatus.Dock = DockStyle.Fill; _securityStatus.Text = "● Şifreli"; _securityStatus.BackColor = Color.Transparent;
        _securityStatus.TextAlign = ContentAlignment.MiddleCenter; _securityStatus.ForeColor = Theme.Success;
        _securityStatus.Font = Theme.Font(8.5f);
        ConfigureEncryptionRecipientStatus();
        _callButton.Kind = ButtonKind.Secondary; _callButton.Dock = DockStyle.Fill;
        _callButton.Margin = new Padding(2, 8, 4, 8); _callButton.CornerRadius = 12;
        _callButton.Click += async (_, _) => await StartCallAsync();
        _participantsButton.Kind = ButtonKind.Secondary; _participantsButton.Dock = DockStyle.Fill;
        _participantsButton.Margin = new Padding(2, 8, 4, 8); _participantsButton.CornerRadius = 12;
        _participantsButton.Click += (_, _) => ToggleParticipants();
        var more = Theme.GlyphButton("\uE712", "Sohbet seçenekleri", ButtonKind.Secondary);
        more.Dock = DockStyle.Fill; more.Margin = new Padding(2, 8, 0, 8); more.CornerRadius = 12;
        more.Click += (_, _) => _selectedConversationCard?.ContextMenuStrip?.Show(more, new Point(0, more.Height));
        actions.Controls.Add(_securityStatus, 0, 0); actions.Controls.Add(_callButton, 1, 0);
        actions.Controls.Add(_participantsButton, 2, 0); actions.Controls.Add(more, 3, 0);
        row.Controls.Add(_headerAvatar, 0, 0); row.Controls.Add(title, 1, 0); row.Controls.Add(actions, 2, 0);
        host.Controls.Add(row);
        void FitHeaderText()
        {
            if (host.IsDisposed || title.IsDisposed) return;
            _conversationTitle.Height = Math.Max((int)Math.Round(34 * host.DeviceDpi / 96d), _conversationTitle.Font.Height + 4);
            _conversationMeta.Height = Math.Max((int)Math.Round(22 * host.DeviceDpi / 96d), _conversationMeta.Font.Height + 4);
            var required = Math.Max((int)Math.Round(92 * host.DeviceDpi / 96d),
                _conversationTitle.Height + _conversationMeta.Height + title.Padding.Vertical + host.Padding.Vertical);
            if (Math.Abs(headerHeight.Height - required) > .5f)
            { headerHeight.Height = required; QueueDetailsLayout(); }
        }
        _conversationTitle.FontChanged += (_, _) => FitHeaderText();
        _conversationMeta.FontChanged += (_, _) => FitHeaderText();
        host.HandleCreated += (_, _) => FitHeaderText();
        host.DpiChangedAfterParent += (_, _) => FitHeaderText();
        host.Paint += (_, args) => { using var pen = new Pen(Color.FromArgb(150, Theme.Divider));
            args.Graphics.DrawLine(pen, 0, host.Height - 1, host.Width, host.Height - 1); };
        return host;
    }

    private Control BuildReferenceComposer()
    {
        var host = new ReferenceSurface { Dock = DockStyle.Fill };
        var bar = new RoundedPanel { FillColor = Color.FromArgb(230, Theme.Surface), BorderColor = Color.FromArgb(86, 80, 158),
            CornerRadius = 18, Padding = new Padding(10, 8, 10, 8) };
        _composerBar = bar;
        var content = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2,
            BackColor = Color.Transparent, Margin = Padding.Empty, Padding = Padding.Empty };
        _imageDraftRowStyle = new RowStyle(SizeType.Absolute, 0); content.RowStyles.Add(_imageDraftRowStyle);
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var tools = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 6, RowCount = 1,
            BackColor = Color.Transparent, Margin = Padding.Empty, Padding = Padding.Empty };
        tools.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        tools.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 48));
        tools.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 48));
        tools.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var voiceColumn = new ColumnStyle(SizeType.Absolute, 64);
        var expiryColumn = new ColumnStyle(SizeType.Absolute, 82);
        tools.ColumnStyles.Add(voiceColumn);
        tools.ColumnStyles.Add(expiryColumn);
        tools.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 62));
        var attach = Theme.GlyphButton("\uE723", "Görsel veya dosya ekle", ButtonKind.Secondary);
        attach.Dock = DockStyle.Fill; attach.Margin = new Padding(1, 6, 7, 6); attach.CornerRadius = 13;
        var attachMenu = Theme.ContextMenu();
        attachMenu.Items.Add("Görsel", null, async (_, _) => await PickImageAsync());
        attachMenu.Items.Add("Dosya", null, async (_, _) => await PickFileAsync());
        attach.Click += (_, _) => attachMenu.Show(attach, new Point(0, -attachMenu.PreferredSize.Height));
        attach.Disposed += (_, _) => attachMenu.Dispose();
        var emoji = Theme.GlyphButton("\uE76E", "Emoji ekle", ButtonKind.Secondary);
        emoji.Dock = DockStyle.Fill; emoji.Margin = new Padding(1, 6, 7, 6); emoji.CornerRadius = 13;
        emoji.Click += (_, _) => ShowComposerEmojis(emoji);
        var inputFrame = new RoundedPanel { Dock = DockStyle.Fill, CornerRadius = 13,
            FillColor = Theme.Surface, BorderColor = Theme.Divider, Margin = new Padding(4, 1, 8, 1),
            Padding = new Padding(12, 10, 10, 8) };
        _premiumComposer.Dock = DockStyle.Fill; _premiumComposer.Margin = Padding.Empty;
        _premiumComposer.Properties.BorderStyle = BorderStyles.NoBorder;
        _premiumComposer.Properties.ScrollBars = ScrollBars.None;
        ConfigureComposerPrompt();
        StyleReferenceEditor(_premiumComposer, Theme.Surface, 11);
        _composerEmojiRendering = ComposerEmojiRendering.Attach(_premiumComposer);
        _premiumComposer.KeyDown += async (_, args) =>
        {
            if (args.Control && args.KeyCode == Keys.V && TryStageClipboardImage()) { args.SuppressKeyPress = true; return; }
            if (args.KeyCode != Keys.Enter || args.Shift) return;
            args.SuppressKeyPress = true; await SendTextAsync();
        };
        inputFrame.Controls.Add(_premiumComposer);
        AddComposerHint(inputFrame);
        _voiceRecordButton.Dock = DockStyle.Fill; _voiceRecordButton.Margin = new Padding(0, 6, 2, 6);
        _voiceRecordButton.Font = Theme.Font(9); _voiceRecordButton.Click += async (_, _) => await ToggleVoiceRecordingAsync();
        _premiumExpiryButton.Dock = DockStyle.Fill; _premiumExpiryButton.Margin = new Padding(0, 6, 2, 6);
        _premiumExpiryButton.Font = Theme.Font(9); _premiumExpiryButton.Click += (_, _) => ShowExpiryMenu();
        var send = Theme.GlyphButton("\uE724", "Mesajı gönder", ButtonKind.Primary);
        send.VectorIcon = ModernButtonIcon.Send;
        send.CircularSurface = true;
        send.AccessibleName = "Mesajı gönder";
        send.Margin = Padding.Empty; send.CornerRadius = 30;
        var sendSlot = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent,
            Margin = Padding.Empty, Padding = new Padding(4, 1, 0, 1) };
        sendSlot.Controls.Add(send);
        void FitSendCircle()
        {
            if (sendSlot.IsDisposed || send.IsDisposed) return;
            var available = sendSlot.DisplayRectangle;
            var desiredEdge = Math.Max(1, (int)Math.Round(56 * sendSlot.DeviceDpi / 96d));
            var edge = Math.Max(1, Math.Min(desiredEdge, Math.Min(available.Width, available.Height)));
            // Do not stretch the primary action with Dock=Fill: its silhouette
            // and center stay aligned with the neighboring tools when a draft
            // increases the composer height or Windows rounds a scaled row.
            send.SetBounds(available.Left + (available.Width - edge) / 2,
                available.Top + (available.Height - edge) / 2, edge, edge);
        }
        sendSlot.Resize += (_, _) => FitSendCircle();
        sendSlot.DpiChangedAfterParent += (_, _) => FitSendCircle();
        sendSlot.HandleCreated += (_, _) => FitSendCircle();
        send.Click += async (_, _) => await SendTextAsync();
        tools.Controls.Add(attach, 0, 0); tools.Controls.Add(emoji, 1, 0); tools.Controls.Add(inputFrame, 2, 0);
        tools.Controls.Add(_voiceRecordButton, 3, 0); tools.Controls.Add(_premiumExpiryButton, 4, 0); tools.Controls.Add(sendSlot, 5, 0);
        void FitTextTools()
        {
            if (tools.IsDisposed || _voiceRecordButton.IsDisposed || _premiumExpiryButton.IsDisposed) return;
            using var graphics = tools.CreateGraphics();
            var voiceWidth = ToolbarTextWidth(graphics, _voiceRecordButton.Text, _voiceRecordButton.Font,
                64, tools.DeviceDpi, _voiceRecordButton.Margin.Horizontal + _voiceRecordButton.Padding.Horizontal);
            var expiryWidth = ToolbarTextWidth(graphics, _premiumExpiryButton.Text, _premiumExpiryButton.Font,
                82, tools.DeviceDpi, _premiumExpiryButton.Margin.Horizontal + _premiumExpiryButton.Padding.Horizontal);
            // A recording timer changes its label frequently. Only change the
            // layout when the measured width differs, not on every 200ms tick.
            if (voiceColumn.Width == voiceWidth && expiryColumn.Width == expiryWidth) return;
            tools.SuspendLayout();
            voiceColumn.Width = voiceWidth; expiryColumn.Width = expiryWidth;
            tools.ResumeLayout();
        }
        _voiceRecordButton.TextChanged += (_, _) => FitTextTools();
        _premiumExpiryButton.TextChanged += (_, _) => FitTextTools();
        _voiceRecordButton.FontChanged += (_, _) => FitTextTools();
        _premiumExpiryButton.FontChanged += (_, _) => FitTextTools();
        tools.HandleCreated += (_, _) => FitTextTools();
        tools.DpiChangedAfterParent += (_, _) => FitTextTools();
        var drafts = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent };
        _imageDraftCard = BuildImageDraftPreview(); drafts.Controls.Add(_imageDraftCard);
        drafts.Controls.Add(BuildVoiceDraftPreview()); drafts.Controls.Add(BuildFileDraftPreview());
        content.Controls.Add(drafts, 0, 0); content.Controls.Add(tools, 0, 1); bar.Controls.Add(content); host.Controls.Add(bar);
        host.Resize += (_, _) => bar.SetBounds(14, 8, Math.Max(1, host.ClientSize.Width - 28), Math.Max(62, host.ClientSize.Height - 20));
        return host;
    }

    private static void StyleReferenceEditor(DevExpress.XtraEditors.TextEdit edit, Color background, float size)
    {
        edit.Properties.Appearance.BackColor = background; edit.Properties.Appearance.ForeColor = Theme.Text;
        edit.Properties.Appearance.Font = Theme.Font(size); edit.Properties.Appearance.Options.UseBackColor = true;
        edit.Properties.Appearance.Options.UseForeColor = true; edit.Properties.Appearance.Options.UseFont = true;
    }

    internal static int ToolbarTextWidth(IDeviceContext context, string text, Font font, int minimumWidth, int dpi, int spacing)
    {
        // Match the actual owner-draw button's padded GDI text measurement,
        // adding a small breathing inset. Fixed pixel columns clipped the
        // recording timer and expiry labels on larger Windows text/DPI settings.
        var measured = TextRenderer.MeasureText(context, text, font, new Size(int.MaxValue, int.MaxValue),
            TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix).Width;
        var breathing = Math.Max(8, (int)Math.Round(8 * dpi / 96d));
        return Math.Max((int)Math.Round(minimumWidth * dpi / 96d), measured + spacing + breathing);
    }

    private void ToggleParticipants()
    {
        var infoWasOpen = _messageInfoSelection is not null;
        CloseMessageInfo();
        // Narrow windows show the existing member viewer rather than crushing the editor.
        if (ClientSize.Width < 1280 && _selectedConversation is { } room)
        {
            using var members = new ConversationMembersForm(_api, room); members.ShowDialog(this); return;
        }
        _participantsShown = infoWasOpen || !_participantsShown;
        _participantsButton.AccessibleName = _participantsShown ? "Katılımcıları gizle" : "Katılımcıları göster";
        UpdatePresenceVisibility();
    }

    internal void ShowParticipantsSnapshot()
    {
        _participantsShown = true; UpdatePresenceVisibility();
    }

    internal void VerifyReferenceLayout()
    {
        if (_selectedConversation is null) return;
        if (_conversationTitle.Height < _conversationTitle.Font.Height ||
            _conversationMeta.Height < _conversationMeta.Font.Height ||
            _conversationMeta.Bottom > _conversationMeta.Parent!.ClientSize.Height)
            throw new InvalidOperationException($"Sohbet başlığı veya durum satırı kırpıldı: title={_conversationTitle.Height}/{_conversationTitle.Font.Height}, meta={_conversationMeta.Bounds}/{_conversationMeta.Font.Height}, slot={_conversationMeta.Parent!.ClientSize}, dpi={DeviceDpi}, font={_conversationTitle.Font.Size}, client={ClientSize}, root={_premiumRoot.ClientSize}, cols={string.Join(',', _premiumRoot.GetColumnWidths())}, styles={string.Join(',', _premiumRoot.ColumnStyles.Cast<ColumnStyle>().Select(c => c.Width))}, chat={_chatSurface?.ClientSize}.");
        if (_premiumComposer.Width < 120 || _premiumComposer.Height < _premiumComposer.Properties.Appearance.Font.Height ||
            !_premiumComposer.Parent!.ClientRectangle.Contains(_premiumComposer.Bounds))
            throw new InvalidOperationException("Mesaj editörü referans yerleşiminde sıkıştı.");
        if (_premiumAdminButton.Visible && Math.Abs(_premiumAdminButton.Left + _premiumAdminButton.Width / 2 -
            _premiumAdminButton.Parent!.ClientSize.Width / 2) > 2)
            throw new InvalidOperationException("Yönetim düğmesi ortalı değil.");
        if (_participantsShown == false && _messageInfoSelection is null && _premiumRoot.ColumnStyles[2].Width != 0)
            throw new InvalidOperationException("Kapalı katılımcı paneli sohbet alanını daraltıyor.");
    }

    internal IReadOnlyList<string> VerifyToolbarIcons()
    {
        var expected = new Dictionary<string, ModernButtonIcon>
        {
            ["Emoji ekle"] = ModernButtonIcon.Smile,
            ["Görsel veya dosya ekle"] = ModernButtonIcon.Attachment,
            ["Sesli arama başlat"] = ModernButtonIcon.Phone,
            ["Sohbet seçenekleri"] = ModernButtonIcon.More,
            ["Mesajı gönder"] = ModernButtonIcon.Send,
            ["Sohbetlerde ara"] = ModernButtonIcon.Search
        };
        var buttons = Descendants(this).OfType<ModernButton>().ToArray();
        foreach (var (label, icon) in expected)
        {
            var button = buttons.SingleOrDefault(candidate => candidate.AccessibleName == label);
            if (button is null || button.VectorIcon != icon || button.Text.Length != 0)
                throw new InvalidOperationException("Toolbar icon is not font-independent: " + label);
        }
        if (_participantsButton.VectorIcon != ModernButtonIcon.Participants || _participantsButton.Text.Length != 0)
            throw new InvalidOperationException("Participant icon is not font-independent.");
        return ["Seven actual chat/search toolbar controls use vector paths with no private-use glyph text."];

        static IEnumerable<Control> Descendants(Control parent)
        {
            foreach (Control child in parent.Controls)
            {
                yield return child;
                foreach (var descendant in Descendants(child)) yield return descendant;
            }
        }
    }

    internal void VerifyConversationScrolling()
    {
        if (_conversationList.Parent?.Parent is ModernConversationViewport viewport)
            foreach (var result in viewport.VerifyScrolling()) Console.WriteLine("Sidebar QA: " + result);
        else throw new InvalidOperationException("Modern sohbet listesi viewport'u bulunamadı.");
    }

    private sealed class ReferenceSurface : Panel
    {
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        internal Color Tint { get; init; } = Color.Empty;
        internal ReferenceSurface()
        {
            // TableLayoutPanel otherwise reserves its default 3px margin on every
            // side, silently reducing the measured header/text slot by 6px.
            Margin = Padding.Empty;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            ChatWallpaper.Draw(e.Graphics, this);
            if (!Tint.IsEmpty) { using var tint = new SolidBrush(Tint); e.Graphics.FillRectangle(tint, ClientRectangle); }
        }
    }
}
