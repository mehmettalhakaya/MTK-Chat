using System.Text;
using System.Runtime.InteropServices;
using MTKChat.Contracts;
using MTKChat.Cryptography;

namespace MTKChat.Desktop;

internal sealed partial class MainForm
{
    internal static IReadOnlyList<string> VerifyMessageInfoIntegration(string directory)
    {
        Directory.CreateDirectory(directory);
        using var context = new HistoryQaUiContext();
        var checks = new List<string>();
        void Require(bool value, string text)
        {
            HistoryQaAssertUiThread();
            if (!value) throw new InvalidOperationException("Message info integration QA: " + text);
            checks.Add(text);
        }
        using var fixture = new HistoryQaFixture();
        var form = fixture.Form;
        var room = fixture.InitialRoom;
        var me = form._session!.User;
        form._deviceCache[me.Id] = new DeviceKeyBundle(me.Id, Guid.NewGuid(),
            form._identity.ExportEncryptionPublicKey(), form._identity.ExportSigningPublicKey());
        var client = Guid.NewGuid();
        var at = DateTimeOffset.UtcNow;
        var payload = MessageCryptography.Encrypt(Encoding.UTF8.GetBytes("Teslim bilgisini bu panelden izleyebilirsin."),
            client, room.Id, me.Id, me.Id, at, form._identity.ExportEncryptionPublicKey(), form._identity.SigningKey);
        var own = new StoredMessage(Guid.NewGuid(), client, room.Id, me.Id, "text", at, null, false, [payload], null,
            new MessageDelivery("sent", [new RecipientReceipt(fixture.FirstSender.Id, null, null),
                new RecipientReceipt(fixture.SecondSender.Id, null, null)]));
        fixture.Handler.Messages[room.Id] = [own];
        HistoryQaPump(form.RefreshMessagesAsync(true));
        var row = form._messageList.Controls.OfType<MessageRow>().Single();
        var infoItem = HistoryQaControls(row).Select(control => control.ContextMenuStrip).Where(menu => menu is not null)
            .SelectMany(menu => menu!.Items.OfType<ToolStripMenuItem>()).First(item => item.Text == "Mesaj bilgisi");
        var draft = "Kaybolmayacak taslak 😊";
        form._premiumComposer.Text = draft;
        var callsBeforeOpen = fixture.Handler.HistoryCalls[room.Id];
        infoItem.PerformClick();
        Application.DoEvents();
        Require(form._messageInfoSelection?.MessageId == own.Id && form._messageInfoPanel!.Visible,
            "The real outgoing message menu opens one non-modal receipt panel");
        Require(fixture.Dialogs == 0 && fixture.Handler.HistoryCalls[room.Id] == callsBeforeOpen,
            "Opening receipt information issues no extra history request or error dialog");
        Require(form._premiumComposer.Text == draft && form._messageInfoPanel!.Parent == form._detailsHost &&
            form._premiumRoot.ColumnStyles[2].Width == 340, "Wide receipt pane preserves draft and uses the right column");
        foreach (var check in form._messageInfoPanel!.VerifyLayout()) checks.Add(check);

        var updated = own with { Delivery = new MessageDelivery("delivered",
            [new RecipientReceipt(fixture.FirstSender.Id, at.AddSeconds(1), at.AddSeconds(2)),
             new RecipientReceipt(fixture.SecondSender.Id, at.AddSeconds(1), null)]) };
        fixture.Handler.Messages[room.Id] = [updated];
        HistoryQaPump(form.RefreshMessagesAsync(true));
        Require(ReferenceEquals(row, form._messageList.Controls.OfType<MessageRow>().Single()) &&
            form._messageInfoPanel.Counts == (1, 1, 0), "Status-only HTTP history refresh updates the open pane without replacing the message row");
        var incoming = fixture.Message(room, fixture.FirstSender, "Bu mesajın göndereni başkası.");
        fixture.Handler.Messages[room.Id] = [updated, incoming];
        HistoryQaPump(form.RefreshMessagesAsync(true));
        Require(!row.IsDisposed && ReferenceEquals(row, form._messageList.Controls.OfType<MessageRow>().Single(item => item.MessageId == own.Id)) &&
            form._messageInfoPanel.RenderedMessageId == own.Id && form._messageInfoSelection?.MessageId == own.Id,
            "Appending encrypted history reuses the unchanged outgoing row and preserves the selected receipt pane");
        var incomingRow = form._messageList.Controls.OfType<MessageRow>().Single(item => item.MessageId == incoming.Id);
        Require(!HistoryQaControls(incomingRow).Select(control => control.ContextMenuStrip).Where(menu => menu is not null)
            .SelectMany(menu => menu!.Items.Cast<ToolStripItem>()).Any(item => item.Text == "Mesaj bilgisi"),
            "Incoming messages do not expose sender-only receipt information");
        form.ShowMessageInfo(incoming, incomingRow);
        Require(form._messageInfoSelection?.MessageId == own.Id, "Programmatic incoming-message selection cannot bypass the ownership guard");
        var replacedPayload = MessageCryptography.Encrypt(Encoding.UTF8.GetBytes("Değişen şifreli içerik yeniden doğrulanır."),
            client, room.Id, me.Id, me.Id, at, form._identity.ExportEncryptionPublicKey(), form._identity.SigningKey);
        updated = updated with { Payloads = [replacedPayload] };
        fixture.Handler.Messages[room.Id] = [updated, incoming];
        HistoryQaPump(form.RefreshMessagesAsync(true));
        Require(row.IsDisposed && form._messageInfoPanel.RenderedMessageId == own.Id && form._messageInfoSelection?.MessageId == own.Id,
            "A genuinely changed encrypted row is disposed without losing the selected receipt pane");
        SaveInfoSnapshot(form, Path.Combine(directory, "chat-message-info-group.png"));

        form.Size = new Size(1120, 720);
        Application.DoEvents(); form.PerformLayout();
        form.VerifyReferenceLayout();
        Require(form._premiumRoot.GetColumnWidths()[2] == 0 && form._chatSurface!.Width > 600,
            "A collapsed detail style commits actual cached column widths after native resize, even at scaled DPI");
        Require(form._messageInfoPanel.Visible && form._chatSurface!.Controls.GetChildIndex(form._messageInfoPanel) == 0 &&
            form._messageInfoOverlayMode && form._premiumRoot.ColumnStyles[2].Width == 0 &&
            form._messageInfoPanel.Parent == form._chatSurface &&
            form._messageInfoPanel.Right == form._chatSurface!.ClientSize.Width &&
            form._messageInfoPanel.Bottom == form._chatSurface.ClientSize.Height,
            "Compact receipt panel overlays the right edge without crushing the composer");
        Require(form._premiumComposer.Text == draft, "Resizing an open pane keeps the draft intact");
        foreach (var check in form._messageInfoPanel.VerifyLayout()) checks.Add("Compact: " + check);
        SaveInfoSnapshot(form, Path.Combine(directory, "chat-message-info-compact.png"));
        var key = Message.Create(form.Handle, 0x0100, IntPtr.Zero, IntPtr.Zero);
        Require(form.ProcessCmdKey(ref key, Keys.Escape) && form._messageInfoSelection is null &&
            !form._messageInfoPanel.Visible && !form._messageInfoOverlayMode,
            "Escape closes the compact pane and releases its conservative read-ACK gate");

        form.Size = new Size(1536, 940); Application.DoEvents();
        form.ShowParticipantsSnapshot();
        var currentRow = form._messageList.Controls.OfType<MessageRow>().Single(item => item.MessageId == own.Id);
        form.ShowMessageInfo(updated, currentRow); Application.DoEvents();
        var close = (ModernButton)HistoryQaControls(form._messageInfoPanel).Single(control => control.Name == "MessageInfoClose");
        close.PerformClick(); Application.DoEvents();
        Require(form._messageInfoSelection is null && form._participantsShown && form._presencePanel!.Visible &&
            form._premiumRoot.ColumnStyles[2].Width == 280, "Close button restores a previously open participants pane");
        form.ShowMessageInfo(updated, currentRow);
        form.RefreshOpenMessageInfo([updated with { DeletedForEveryone = true }]);
        Require(form._messageInfoSelection is null, "Deleting the selected message closes stale information");
        form.ShowMessageInfo(updated, currentRow); form.RefreshOpenMessageInfo([]);
        Require(form._messageInfoSelection is null, "Removing or expiring the selected message closes stale information");
        form._selectedConversation = room with { Kind = "direct", Title = fixture.FirstSender.DisplayName,
            Participants = [me, fixture.FirstSender], GroupRoles = null };
        currentRow.UpdateDelivery(new MessageDelivery("read", [new RecipientReceipt(fixture.FirstSender.Id, at, at.AddSeconds(2))]));
        form.ShowMessageInfo(updated, currentRow); Application.DoEvents();
        Require(form._messageInfoPanel.Counts == (1, 0, 0) && form._messageInfoPanel.RecipientIds.Count == 1,
            "A direct conversation uses the same receipt pane with its single actual recipient");
        form.CloseMessageInfo();
        form._selectedConversation = room;
        form.ShowMessageInfo(updated, currentRow);
        var next = fixture.AddRoom("Sonraki özel grup");
        HistoryQaPump(form.SelectConversationAsync(next, fixture.Card(next), silent: true));
        Require(form._messageInfoSelection is null && !form._messageInfoPanel.Visible,
            "Changing conversations closes prior-room receipt information");
        form.ShowMessageInfo(updated, currentRow);
        Require(form._messageInfoSelection is null, "A stale disposed menu cannot reopen a different room's receipt panel");
        form.ResetConversationSelection();
        Require(form._messageInfoSelection is null && form._selectedConversation is null,
            "Clearing conversation selection leaves no receipt overlay");
        form.Hide();
        return checks;
    }

    internal void PopulateMessageInfoSnapshot(bool direct = false)
    {
        PopulateReferenceSnapshot(direct);
        var room = _selectedConversation!;
        var at = DateTimeOffset.Now;
        var recipients = room.Participants.Where(user => user.Id != _session!.User.Id)
            .Select((user, index) => new RecipientReceipt(user.Id, index < 2 ? at.AddSeconds(1) : null,
                index == 0 ? at.AddSeconds(2) : null)).ToArray();
        var message = new StoredMessage(Guid.NewGuid(), Guid.NewGuid(), room.Id, _session!.User.Id,
            "text", at, null, false, [], null, new MessageDelivery("delivered", recipients));
        var row = (MessageRow)BuildMessageBubble(message, "Mesajın bilgisi sağdaki panelde.", null);
        _messageList.Controls.Add(row); ResizeBubbles();
        _messageList.ScrollToOffset(_messageList.MaximumOffset);
        ShowMessageInfo(message, row);
    }

    private static void SaveInfoSnapshot(MainForm form, string path)
    {
        form.PerformLayout(); Application.DoEvents();
        using var bitmap = CaptureInfoClient(form);
        if (form._messageInfoOverlayMode && form._messageInfoPanel is { Visible: true } panel)
        {
            var point = form.PointToClient(panel.PointToScreen(new Point(panel.Width / 2, 5)));
            if (bitmap.GetPixel(point.X, point.Y).ToArgb() != Theme.Sidebar.ToArgb())
                throw new InvalidOperationException("Native compact receipt pane is occluded by the chat layout.");
        }
        bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }

    // Capture only this QA-owned form's client DC after normal WM_PAINT. DrawToBitmap
    // can flatten overlapping sibling controls in the wrong order; the compact
    // overlay must be verified on the native surface, not just by Visible/Bounds.
    internal static Bitmap CaptureInfoClient(MainForm form)
    {
        var bitmap = new Bitmap(form.ClientSize.Width, form.ClientSize.Height);
        using var graphics = Graphics.FromImage(bitmap);
        var destination = graphics.GetHdc();
        var source = InfoGetDC(form.Handle);
        try
        {
            if (source == IntPtr.Zero || !InfoBitBlt(destination, 0, 0, bitmap.Width, bitmap.Height,
                    source, 0, 0, 0x00cc0020))
                throw new InvalidOperationException("Cannot capture the QA-owned receipt client surface.");
        }
        finally
        {
            if (source != IntPtr.Zero) InfoReleaseDC(form.Handle, source);
            graphics.ReleaseHdc(destination);
        }
        return bitmap;
    }

    [DllImport("user32.dll", EntryPoint = "GetDC")] private static extern IntPtr InfoGetDC(IntPtr window);
    [DllImport("user32.dll", EntryPoint = "ReleaseDC")] private static extern int InfoReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("gdi32.dll", EntryPoint = "BitBlt")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InfoBitBlt(IntPtr destination, int x, int y, int width, int height,
        IntPtr source, int sourceX, int sourceY, int operation);
}
