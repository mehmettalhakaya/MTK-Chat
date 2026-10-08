namespace MTKChat.Desktop;

internal sealed record StarredMessagePreview(Guid MessageId, string Sender, Color SenderColor,
    DateTimeOffset SentAt, string Preview);

// A read-only view of the current authorized history, not a second message cache.
internal sealed class StarredMessagesForm : ModernForm
{
    private readonly FlowLayoutPanel _list = new() { FlowDirection = FlowDirection.TopDown, WrapContents = false,
        AutoScroll = true, BackColor = Theme.Canvas, Margin = Padding.Empty };
    private readonly ModernConversationViewport _viewport;
    private StarredMessagePreview[]? _messages;
    internal Guid? SelectedMessageId { get; private set; }
    internal int MessageCountForQa => _messages?.Length ?? 0;
    internal StarredMessagesForm(string conversationTitle, IReadOnlyList<StarredMessagePreview> messages)
    {
        Text = "MTK Chat · Yıldızlı mesajlar";
        Icon = Theme.AppIcon();
        BackColor = Theme.Canvas; ForeColor = Theme.Text; Font = Theme.Font(10);
        Size = new Size(640, 680); MinimumSize = new Size(480, 420);
        StartPosition = FormStartPosition.CenterParent;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24),
            ColumnCount = 1, RowCount = 2, BackColor = Theme.Canvas };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 84));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var header = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
        header.Controls.Add(new Label { Text = "Yıldızlı mesajlar", Bounds = new Rectangle(0, 0, 560, 40),
            Font = Theme.Font(21, FontStyle.Bold), ForeColor = Theme.Text, AutoEllipsis = true, Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right });
        header.Controls.Add(new Label { Text = conversationTitle, Bounds = new Rectangle(0, 44, 560, 25),
            ForeColor = Theme.Muted, AutoEllipsis = true, UseMnemonic = false, Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right });
        layout.Controls.Add(header, 0, 0);
        _viewport = new ModernConversationViewport(_list) { Dock = DockStyle.Fill, Tint = Theme.Canvas };
        layout.Controls.Add(_viewport, 0, 1);
        _list.Resize += (_, _) => ResizeCards();
        Controls.Add(layout);
        UpdateMessages(messages);
    }

    internal bool UpdateMessages(IReadOnlyList<StarredMessagePreview> messages)
    {
        if (IsDisposed || Disposing || _messages?.SequenceEqual(messages) == true) return false;
        // The view follows the authorized history rather than retaining a second
        // plaintext cache after a message is deleted, hidden, expired or unstarred.
        _messages = messages.ToArray();
        _list.SuspendLayout();
        try
        {
            foreach (var old in _list.Controls.Cast<Control>().ToArray()) old.Dispose();
            PopulateCards(_messages);
            ResizeCards();
        }
        finally { _list.ResumeLayout(performLayout: true); }
        _viewport.SynchronizeNow();
        return true;
    }

    private void ResizeCards()
    {
        foreach (Control card in _list.Controls) card.Width = Math.Max(1, _list.ClientSize.Width - 2);
    }

    private void PopulateCards(IReadOnlyList<StarredMessagePreview> messages)
    {
        if (messages.Count == 0)
            _list.Controls.Add(new Label { Text = "Henüz yıldızlı mesaj yok", ForeColor = Theme.Muted,
                TextAlign = ContentAlignment.MiddleCenter, Height = 120, AutoEllipsis = true });
        foreach (var message in messages)
        {
            var card = new RoundedPanel { Height = 150, FillColor = Theme.Surface,
                BorderColor = Theme.Divider, CornerRadius = 14, Margin = new Padding(0, 0, 0, 12), Cursor = Cursors.Hand };
            var sender = new Label { Text = message.Sender, ForeColor = message.SenderColor, UseMnemonic = false,
                Font = Theme.Font(9.5f, FontStyle.Bold), AutoEllipsis = true };
            var time = new Label { Text = message.SentAt.ToLocalTime().ToString("dd MMM yyyy · HH:mm"),
                ForeColor = Theme.Muted, Font = Theme.Font(9), AutoEllipsis = true };
            var body = new Label { Text = message.Preview, ForeColor = Theme.Text, AutoEllipsis = true, UseMnemonic = false };
            var action = Theme.Button("Sohbette göster  →", ButtonKind.Secondary);
            action.ForeColor = Theme.Violet;
            void Select() { SelectedMessageId = message.MessageId; DialogResult = DialogResult.OK; Close(); }
            foreach (var control in new Control[] { card, sender, body, time, action }) control.Click += (_, _) => Select();
            card.Controls.AddRange([sender, time, body, action]);
            card.Resize += (_, _) =>
            {
                var pad = Math.Max(12, (int)Math.Round(14 * card.DeviceDpi / 96f));
                var width = Math.Max(1, card.Width - 2 * pad);
                sender.SetBounds(pad, pad, width, sender.Font.Height + 4);
                time.SetBounds(pad, sender.Bottom + 4, width, time.Font.Height + 3);
                body.SetBounds(pad, time.Bottom + 8, width, body.Font.Height * 3);
                action.SetBounds(pad - 8, body.Bottom + 5, Math.Min(width, 190), Math.Max(32, action.Font.Height + 12));
                card.Height = action.Bottom + pad;
            };
            _list.Controls.Add(card);
        }
    }
}
