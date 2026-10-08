using MTKChat.Contracts;

namespace MTKChat.Desktop;

// A row owns measurement and its decoded image; resizing reflows text rather than clipping a fixed 600px bubble.
internal sealed class MessageRow : Panel
{
    private readonly bool _mine;
    private readonly RoundedPanel _bubble;
    private readonly Label _heading;
    private readonly MessageText _body;
    private readonly MetadataText _time;
    private readonly ReceiptIndicator? _receipt;
    internal Guid MessageId { get; }
    internal Guid ConversationId { get; }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal bool CanMarkRead { get; set; }
    internal MessageDelivery? Delivery { get; private set; }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal bool CanStar { get; set; }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal bool CanPin { get; set; }
    internal DateTimeOffset? ExpiresAt { get; }
    internal bool IsStarred { get; private set; }
    internal DateTimeOffset CreatedAt { get; }
    internal string Kind { get; }
    internal string SenderHeading => _heading.Text;
    internal Color SenderColor => _heading.ForeColor;
    internal string StarredPreview => Kind.StartsWith("image/", StringComparison.OrdinalIgnoreCase) || Kind == "image"
        ? "Fotoğraf" : Kind is "audio/wav" or "voice" ? "Sesli mesaj" : _body.Text;
    private readonly StarIndicator _star = new();
    private readonly PictureBox? _picture;
    private readonly ModernButton? _voiceButton;
    private readonly VoicePlayback? _voicePlayer;
    private readonly ModernButton? _speedButton;
    private readonly AnimatedEmojiView? _emoji;
    internal AvatarView Avatar { get; } = new();
    private bool _layingOut;
    private readonly ChatUser _sender;
    private bool _inlineMetadata;

    internal MessageRow(ChatUser sender, bool mine, StoredMessage message, string text, byte[]? imageBytes, byte[]? voiceBytes = null)
    {
        _mine = mine;
        _sender = sender;
        MessageId = message.Id;
        ConversationId = message.ConversationId;
        CreatedAt = message.CreatedAt;
        ExpiresAt = message.ExpiresAt;
        Kind = message.Kind;
        CanStar = !message.DeletedForEveryone;
        CanPin = !message.DeletedForEveryone;
        CanMarkRead = !mine && !message.DeletedForEveryone;
        SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Color.Transparent;
        Margin = new Padding(0, 0, 0, 10);
        Width = 680;
        Avatar.Initials = UserPresentation.Initials(sender.DisplayName);
        Avatar.AvatarColor = Theme.SurfaceRaised;
        Avatar.Size = new Size(40, 40);
        Avatar.Visible = !mine;
        _bubble = new RoundedPanel
        {
            FillColor = mine ? Color.FromArgb(100, 64, 255) : Color.FromArgb(31, 37, 67),
            GradientEndColor = mine ? Color.FromArgb(46, 56, 239) : Color.FromArgb(27, 33, 57),
            BorderColor = mine ? Color.FromArgb(124, 119, 255) : Color.FromArgb(65, 73, 109),
            CornerRadius = 13
        };
        _heading = new Label
        {
            Text = UserPresentation.Heading(sender), ForeColor = UserPresentation.RoleColor(sender),
            Font = Theme.Font(8.5f, FontStyle.Bold), AutoEllipsis = true, UseMnemonic = false,
            BackColor = Color.Transparent
        };
        _body = new MessageText
        {
            Text = text, ForeColor = message.DeletedForEveryone ? Theme.Muted : Theme.Text,
            Font = Theme.Font(12, message.DeletedForEveryone ? FontStyle.Italic : FontStyle.Regular),
            // A file name/deletion placeholder is not a rich chat message.
            RichEmojiEnabled = !message.DeletedForEveryone && message.Kind == "text" && message.Attachment is null
        };
        _time = new MetadataText
        {
            Text = message.CreatedAt.ToLocalTime().ToString("HH:mm") + (message.ExpiresAt is null ? "" : " · süreli"),
            ForeColor = mine ? Color.FromArgb(219, 216, 255) : Color.FromArgb(167, 176, 201),
            Font = Theme.Font(9), BackColor = Color.Transparent
        };
        if (mine && !message.DeletedForEveryone)
        {
            _receipt = new ReceiptIndicator();
            _bubble.Controls.Add(_receipt);
        }
        UpdateDelivery(message.Delivery);
        if (imageBytes is not null)
        {
            try
            {
                // The chat only needs a screen preview. Retaining a full-resolution
                // bitmap in every row made photo histories needlessly consume RAM.
                _picture = new PictureBox { Image = MessageImagePreview.Decode(imageBytes), SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Transparent };
                _bubble.Controls.Add(_picture);
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidDataException or
                System.Runtime.InteropServices.ExternalException)
            { _body.Text = "Görsel açılamadı veya çözünürlüğü desteklenmiyor"; CanMarkRead = false; }
            finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(imageBytes); }
        }
        if (voiceBytes is not null)
        {
            _voiceButton = Theme.Button("▶  Sesli mesaj", ButtonKind.Ghost);
            _voiceButton.Font = Theme.Font(9, FontStyle.Bold);
            _voicePlayer = new VoicePlayback(this, voiceBytes,
                playing => { if (!_voiceButton.IsDisposed) _voiceButton.Text = playing ? "Ⅱ  Duraklat" : "▶  Sesli mesaj"; });
            _voiceButton.Click += (_, _) =>
            {
                try { _voicePlayer.Toggle(); }
                catch { _voiceButton.Text = "Ses oynatılamadı"; _voiceButton.Enabled = false; }
            };
            _bubble.Controls.Add(_voiceButton);
            _speedButton = Theme.Button("1×", ButtonKind.Secondary);
            _speedButton.Font = Theme.Font(9, FontStyle.Bold);
            _speedButton.AccessibleName = "Ses hızı: 1, 1.5 veya 2 kat";
            _speedButton.Click += (_, _) =>
            {
                _voicePlayer.CycleSpeed();
                _speedButton.Text = _voicePlayer.Speed.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "×";
            };
            _bubble.Controls.Add(_speedButton);
        }
        // Unicode stays in _body for copy, pins and personal stars. One-to-three
        // supported standalone emoji get a separate animated surface; ordinary
        // text messages use static colored vectors inline. Unsupported graphemes,
        // file labels and deleted content are never rewritten into another emoji.
        if (!message.DeletedForEveryone && message.Kind == "text" && message.Attachment is null &&
            imageBytes is null && voiceBytes is null && EmojiCatalog.ParseEmojiOnly(text) is { } emoji)
        {
            _emoji = new AnimatedEmojiView(emoji);
            _bubble.Controls.Add(_emoji);
        }
        _bubble.Controls.Add(_body);
        _bubble.Controls.Add(_time);
        _star.Visible = false;
        _bubble.Controls.Add(_star);
        Controls.Add(_bubble);
        Controls.Add(_heading);
        Controls.Add(Avatar);
        Resize += (_, _) => Reflow();
        Reflow();
    }

    internal void SetMessageMenu(ContextMenuStrip menu)
    {
        _bubble.ContextMenuStrip = menu;
        foreach (Control child in _bubble.Controls) child.ContextMenuStrip = menu;
        Disposed += (_, _) => menu.Dispose();
    }

    internal void UpdateDelivery(MessageDelivery? delivery)
    {
        Delivery = delivery;
        _receipt?.UpdateDelivery(delivery);
    }
    internal void SetStarred(bool starred)
    {
        if (IsStarred == starred) return;
        IsStarred = starred;
        _star.Visible = starred;
        Reflow();
    }
    internal void SetGroupRole(string? role)
    {
        _heading.Text = UserPresentation.Heading(_sender, role);
        _heading.ForeColor = UserPresentation.RoleColor(_sender, role);
    }
    internal void SetFileAction(Func<Task> action)
    {
        _body.Cursor = Cursors.Hand;
        _body.AccessibleName = "Dosyayı kaydet";
        var busy = false;
        _body.Click += async (_, _) =>
        {
            if (busy) return;
            busy = true;
            try { await action(); }
            finally { busy = false; }
        };
        _body.Text += "\n↓  Kaydet";
        Reflow();
    }

    internal bool IsContentVisible(Rectangle viewport)
    {
        // Inline timestamps make the old bubbleHeight−36 shortcut too short. Project the real
        // content control into list coordinates while retaining the same read-visibility threshold.
        Control actualContent = _picture is not null ? _picture : _voiceButton is not null ? _voiceButton :
            _emoji is not null ? _emoji : _body;
        var content = new Rectangle(Left + _bubble.Left + actualContent.Left,
            Top + _bubble.Top + actualContent.Top, actualContent.Width, actualContent.Height);
        var intersection = Rectangle.Intersect(viewport, content);
        return Visible && CanMarkRead && intersection.Width > 0 && intersection.Height >= Math.Min(12, content.Height);
    }

    private void Reflow()
    {
        if (_layingOut) return;
        _layingOut = true;
        try
        {
            // Fonts are point-sized, but controls use physical pixels. Scale all row geometry exactly
            // once against DeviceDpi; fixed 23px headings clipped larger Windows text at 125/150%.
            var avatarSize = Scale(40);
            var avatarGutter = _mine ? 0 : Scale(52);
            var horizontalPadding = Scale(12);
            var maxWidth = Math.Max(1, Math.Min(Scale(650), Width - avatarGutter - Scale(4)));
            var measured = _body.Measure(Math.Max(1, maxWidth - horizontalPadding * 2));
            var minWidth = Math.Min(maxWidth, Scale(_emoji is null ? 152 : 112));
            var receiptWidth = _receipt is null ? 0 : Scale(28);
            var starWidth = IsStarred ? Scale(20) : 0;
            using var graphics = CreateGraphics();
            var timeWidth = TextRenderer.MeasureText(graphics, _time.Text, _time.Font,
                new Size(int.MaxValue, int.MaxValue), TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding).Width;
            var metadataWidth = timeWidth + receiptWidth + starWidth;
            var inlineWidth = measured.Width + horizontalPadding * 2 + metadataWidth + Scale(10);
            // Short messages keep metadata on the same baseline. A wrapped message always gets
            // a separate footer: reserving guessed trailing spaces could otherwise cover its last line.
            _inlineMetadata = _voiceButton is null && _picture is null && _emoji is null &&
                !_body.Text.Contains('\n') && measured.Height <= _body.Font.Height + Scale(2) && inlineWidth <= maxWidth;
            var emojiWidth = _emoji is null ? 0 : Math.Max(_emoji.PreferredSize.Width, metadataWidth) + horizontalPadding * 2;
            var width = _voiceButton is not null ? Math.Min(Scale(338), maxWidth) :
                _picture is not null ? Math.Min(Scale(358), maxWidth) :
                _emoji is not null ? Math.Min(maxWidth, Math.Max(minWidth, emojiWidth)) :
                Math.Min(maxWidth, Math.Max(minWidth, _inlineMetadata ? inlineWidth : measured.Width + horizontalPadding * 2));
            var bodyWidth = Math.Max(1, width - horizontalPadding * 2 - (_inlineMetadata ? metadataWidth + Scale(10) : 0));
            var bodyHeight = _voiceButton is not null ? Scale(40) : _emoji is not null ? _emoji.PreferredSize.Height : _picture is null ?
                Math.Max(_body.Font.Height, _body.Measure(bodyWidth).Height) :
                Math.Clamp((int)Math.Round((width - Scale(12)) * (_picture.Image!.Height / (double)_picture.Image.Width)), Scale(120), Scale(320));
            var headingHeight = Math.Max(Scale(15), _heading.Font.Height + Scale(2));
            var bubbleTop = headingHeight + Scale(3);
            var bodyTop = _picture is not null ? Scale(6) : Scale(10);
            var footerHeight = Math.Max(Scale(14), _time.Font.Height + Scale(1));
            var footerTop = _inlineMetadata ? bodyTop + Math.Max(0, (bodyHeight - footerHeight) / 2) : bodyTop + bodyHeight + Scale(3);
            var bubbleHeight = _inlineMetadata ? bodyTop + Math.Max(bodyHeight, footerHeight) + Scale(10) : footerTop + footerHeight + Scale(7);
            var left = _mine ? Width - width - Scale(3) : avatarGutter;
            _heading.SetBounds(left + Scale(1), 0, Math.Max(1, width - Scale(2)), headingHeight);
            _heading.TextAlign = _mine ? ContentAlignment.MiddleRight : ContentAlignment.MiddleLeft;
            Avatar.Size = new Size(avatarSize, avatarSize);
            Avatar.Location = new Point(_mine ? Width - avatarSize : 0, bubbleTop + Scale(1));
            // RoundedPanel scales its logical radius internally, unlike SetBounds pixel coordinates.
            _bubble.CornerRadius = 13;
            _bubble.SetBounds(left, bubbleTop, width, bubbleHeight);
            _body.SetBounds(horizontalPadding, bodyTop, bodyWidth, bodyHeight);
            _body.Visible = _picture is null && _voiceButton is null && _emoji is null;
            _emoji?.SetBounds(horizontalPadding, bodyTop, bodyWidth, bodyHeight);
            _picture?.SetBounds(Scale(6), bodyTop, Math.Max(1, width - Scale(12)), bodyHeight);
            _voiceButton?.SetBounds(Scale(10), bodyTop, Math.Max(1, width - Scale(88)), bodyHeight);
            _speedButton?.SetBounds(width - Scale(68), bodyTop + Scale(2), Scale(56), Scale(36));
            _time.SetBounds(_inlineMetadata ? width - horizontalPadding - metadataWidth : horizontalPadding,
                footerTop, _inlineMetadata ? timeWidth : Math.Max(1, width - horizontalPadding * 2 - receiptWidth - starWidth), footerHeight);
            _star.SetBounds(width - horizontalPadding - receiptWidth - starWidth, footerTop, Math.Max(1, starWidth), footerHeight);
            _receipt?.SetBounds(width - horizontalPadding - Scale(26), footerTop, Scale(26), footerHeight);
            Margin = new Padding(0, 0, 0, Scale(10));
            Height = Math.Max(_bubble.Bottom, _mine ? 0 : Avatar.Bottom);
        }
        finally { _layingOut = false; }
    }

    protected override void OnPaintBackground(PaintEventArgs e) => ChatWallpaper.Draw(e.Graphics, this);

    protected override void OnLocationChanged(EventArgs args)
    {
        base.OnLocationChanged(args);
        // Reflow/reordering can move an existing child HWND without repainting its
        // cached background. Its wallpaper sample must follow the new window origin.
        Invalidate(invalidateChildren: true);
    }

    private int Scale(int value) => Math.Max(1, (int)Math.Round(value * DeviceDpi / 96f));

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        Reflow();
    }

    // Called by app-owned snapshot QA. Assertions inspect the same controls and renderer that users see.
    internal void VerifyLayout()
    {
        Reflow();
        if (_bubble.Left < 0 || _bubble.Right > ClientSize.Width || _bubble.Bottom > ClientSize.Height)
            throw new InvalidOperationException("Mesaj balonu satır sınırlarını aşıyor.");
        if (_heading.Bottom > _bubble.Top || (!_mine && (Avatar.Left < 0 || Avatar.Right > ClientSize.Width)))
            throw new InvalidOperationException("Mesaj başlığı veya avatarı üst üste geliyor.");
        if (_body.Visible && (_body.Height < _body.Measure(_body.Width).Height ||
            (_inlineMetadata ? _body.Right + Scale(4) > _time.Left : _body.Bottom > _time.Top)))
            throw new InvalidOperationException("Mesaj metni ölçülen yüksekliğe sığmıyor.");
        if (_emoji is not null && (_emoji.Left < 0 || _emoji.Top < 0 ||
            _emoji.Right > _bubble.ClientSize.Width || _emoji.Bottom > _time.Top || _inlineMetadata))
            throw new InvalidOperationException("Emoji yüzeyi balon sınırlarını veya mesaj alt bilgisini aşıyor.");
        if (_time.Bottom > _bubble.ClientSize.Height || (_receipt is not null && _time.Right > _receipt.Left))
            throw new InvalidOperationException("Mesaj zamanı ve teslim göstergesi çakışıyor.");
        if (IsStarred && (_star.Left < _time.Right || _star.Right > (_receipt?.Left ?? _bubble.Width) ||
            _star.Bottom > _bubble.Height)) throw new InvalidOperationException("Mesaj yıldızı zaman veya teslim göstergesiyle çakışıyor.");
    }

    private sealed class StarIndicator : Control
    {
        internal StarIndicator()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            AccessibleName = "Yıldızlı mesaj";
            AccessibleRole = AccessibleRole.StaticText;
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var radius = Math.Min(Width, Height) * .4f;
            var center = new PointF(Width / 2f, Height / 2f);
            var points = Enumerable.Range(0, 10).Select(index =>
            {
                var angle = -Math.PI / 2 + index * Math.PI / 5;
                var length = radius * (index % 2 == 0 ? 1 : .45f);
                return new PointF(center.X + (float)Math.Cos(angle) * length, center.Y + (float)Math.Sin(angle) * length);
            }).ToArray();
            using var brush = new SolidBrush(Theme.Warning);
            e.Graphics.FillPolygon(brush, points);
        }
    }

    private sealed class MessageText : Control
    {
        // Label used a different padding/wrapping path than MeasureText. Keeping both paths identical
        // prevents the final Turkish/multiline line from disappearing at narrower widths and higher DPI.
        private const TextFormatFlags Flags = TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl |
            TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
        private readonly InlineEmojiTextCache _emojiLayout = new();
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        internal bool RichEmojiEnabled { get; init; }

        internal MessageText()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            AccessibleRole = AccessibleRole.StaticText;
        }

        internal Size Measure(int width)
        {
            using var graphics = CreateGraphics();
            if (RichEmojiEnabled && _emojiLayout.Get(graphics, Text, Font, width) is { } layout)
                return layout.Size;
            return TextRenderer.MeasureText(graphics, Text.Length == 0 ? " " : Text, Font,
                new Size(Math.Max(1, width), int.MaxValue), Flags);
        }

        protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); _emojiLayout.Clear(); Invalidate(); }
        protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); _emojiLayout.Clear(); Invalidate(); }
        protected override void OnDpiChangedAfterParent(EventArgs e)
        { base.OnDpiChangedAfterParent(e); _emojiLayout.Clear(); Invalidate(); }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (RichEmojiEnabled && _emojiLayout.Get(e.Graphics, Text, Font, Width) is { } layout)
            { layout.Draw(e.Graphics, Font, ForeColor, Point.Empty); return; }
            TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, ForeColor, Flags);
        }
    }

    private sealed class MetadataText : Control
    {
        internal MetadataText()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            AccessibleRole = AccessibleRole.StaticText;
        }

        protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, ForeColor,
                TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding |
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _picture?.Image?.Dispose(); _voicePlayer?.Dispose(); }
        base.Dispose(disposing);
    }
}
