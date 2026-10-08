using System.Reflection;
using MTKChat.Contracts;

namespace MTKChat.Desktop;

internal sealed partial class MainForm
{
    // Native probes exercise the production bubble/menu controls with synthetic
    // identities only. Commands that delete, copy, play audio or contact a real
    // server are never clicked; this verifies the affordance, not server writes.
    internal static IReadOnlyList<string> VerifyMessageActions(string directory)
    {
        Directory.CreateDirectory(directory);
        using var context = new HistoryQaUiContext();
        using var fixture = new HistoryQaFixture();
        var form = fixture.Form;
        var room = fixture.InitialRoom;
        var me = form._session!.User;
        var checks = new List<string>();
        void Require(bool condition, string check)
        {
            HistoryQaAssertUiThread();
            if (!condition) throw new InvalidOperationException("Message actions QA: " + check);
            checks.Add(check);
        }

        foreach (var old in form._messageList.Controls.Cast<Control>().ToArray()) old.Dispose();
        var selected = form._selectedConversation;
        const string draft = "Menüden etkilenmeyecek taslak 😊";
        form._premiumComposer.Text = draft;
        var now = DateTimeOffset.UtcNow;
        StoredMessage Sample(string kind, bool mine, bool deleted = false, DateTimeOffset? deadline = null) =>
            new(Guid.NewGuid(), Guid.NewGuid(), room.Id, mine ? me.Id : fixture.FirstSender.Id, kind,
                now, null, deleted, [], null, mine ? new MessageDelivery("read",
                    [new RecipientReceipt(fixture.FirstSender.Id, now, now)]) : null,
                deadline ?? now.AddMinutes(15));

        // Cover real content control branches, not just label-only mock rows.
        // Image bytes are decoded/zeroed by MessageRow; each row gets fresh data.
        foreach (var mine in new[] { false, true })
        foreach (var kind in new[] { "text", "emoji", "image/png", "audio/wav", "file", "deleted" })
        {
            var message = Sample(kind is "emoji" or "deleted" ? "text" : kind, mine, kind == "deleted");
            var text = kind switch
            {
                "text" => "Mesaj seçenekleri, metni ve saati örtmeden açılır. 😊",
                "emoji" => "😊 👋",
                "file" => "▤  sentetik-deneme.pdf\n12 KB",
                "deleted" => "Mesaj silindi",
                _ => string.Empty
            };
            using var row = (MessageRow)form.BuildMessageBubble(message, text,
                kind == "image/png" ? MessageActionsQaImage() : null,
                kind == "audio/wav" ? MessageActionsQaWave() : null);
            if (kind == "file") row.SetFileAction(() => throw new InvalidOperationException("QA does not save files."));
            form._messageList.Controls.Add(row);
            Application.DoEvents();
            var bubble = row.Controls.OfType<RoundedPanel>().Single();
            var button = row.ActionButtonForQa;
            var menu = (ModernContextMenu)bubble.ContextMenuStrip!;
            var label = (mine ? "outgoing/" : "incoming/") + kind;
            Require(button.Visible && button.TabStop && button.AccessibleName == "Mesaj seçenekleri" &&
                button.AccessibilityObject.Role == AccessibleRole.PushButton,
                label + ": the hidden-ink arrow remains a named accessible keyboard button");
            Require(ReferenceEquals(button.ContextMenuStrip, menu) &&
                HistoryQaControls(bubble).All(child => ReferenceEquals(child.ContextMenuStrip, menu)),
                label + ": button, bubble and all media children share the original owned message popup");

            foreach (var width in new[] { 340, 650, 1100 })
            {
                row.Width = width; row.VerifyLayout();
                var bounds = HistoryQaControls(row).ToDictionary(control => control, control => control.Bounds);
                var rowBounds = row.Bounds;
                var layouts = 0;
                void LayoutChanged(object? _, LayoutEventArgs __) => layouts++;
                row.Layout += LayoutChanged;
                row.TrackActionPointer(new Point(-100, -100));
                Require(!button.Revealed, label + $"/{width}px: empty row space does not show an unfocused arrow");
                row.TrackActionPointer(new Point(bubble.Left + bubble.Width / 2, bubble.Top + bubble.Height / 2));
                Require(button.Revealed, label + $"/{width}px: hovering the real content rectangle reveals its arrow");
                var bridge = new Point(mine ? (button.Right + bubble.Left) / 2 : (bubble.Right + button.Left) / 2,
                    button.Top + button.Height / 2);
                row.TrackActionPointer(bridge);
                Require(button.Revealed, label + $"/{width}px: the bridge between bubble and arrow remains hot");
                row.TrackActionPointer(new Point(button.Left + button.Width / 2, button.Top + button.Height / 2));
                Require(button.Revealed && !button.Bounds.IntersectsWith(bubble.Bounds) &&
                    (mine ? button.Right < bubble.Left : button.Left > bubble.Right),
                    label + $"/{width}px: the arrow uses an outside gutter, never the content/time/receipt area");
                row.TrackActionPointer(new Point(-100, -100));
                row.Layout -= LayoutChanged;
                Require(!button.Revealed && layouts == 0 && row.Bounds == rowBounds &&
                    bounds.All(pair => pair.Key.Bounds == pair.Value),
                    label + $"/{width}px: entering/exiting hover performs no relayout and preserves every child rectangle");
            }

            row.Width = 650; row.VerifyLayout();
            row.TrackActionPointer(new Point(bubble.Left + 12, bubble.Top + 12));
            using (var image = new Bitmap(row.Width, row.Height))
            {
                row.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size));
                image.Save(Path.Combine(directory, "message-hover-" + label.Replace('/', '-') + ".png"));
            }
            button.PerformClick(); Application.DoEvents();
            row.TrackActionPointer(new Point(-100, -100));
            Require(menu.Visible && button.Revealed,
                label + ": the actual arrow opens the same popup and stays revealed while it is open");
            VerifyConversationMenuSurface(menu, Require, label + " popup", maxHeight: 300);
            var deletion = ConversationMenuItem(menu, "Benden sil");
            Require(deletion.Enabled && deletion.ForeColor == Theme.Danger,
                label + ": personal deletion is a visible, enabled, modern danger action");
            var everyone = menu.Items.OfType<ToolStripMenuItem>().SingleOrDefault(item => item.Text == "Herkesten sil");
            Require((everyone?.Available == true) == (mine && kind != "deleted"),
                label + ": only a fresh own undeleted message exposes Delete for everyone");
            if (everyone?.Available == true)
                Require(everyone.ForeColor == Theme.Danger, label + ": Delete for everyone keeps its danger color");
            if (kind == "deleted")
                Require(ConversationMenuCommands(menu).Select(item => item.Text).SequenceEqual(new[] { "Benden sil" }),
                    label + ": a deletion placeholder offers no copy, star, pin or receipt actions");
            if (kind is "text" or "deleted")
                CaptureConversationMenu(menu, null, Path.Combine(directory, "message-menu-" + label.Replace('/', '-') + ".png"));
            menu.Close(); Application.DoEvents();
            for (var repeat = 0; repeat < 5; repeat++)
            {
                button.PerformClick(); Application.DoEvents();
                Require(menu.Visible, label + $": popup reopens on the original arrow ({repeat + 1})");
                button.PerformClick(); Application.DoEvents();
                Require(!menu.Visible && !menu.IsDisposed && !row.IsDisposed &&
                    ReferenceEquals(bubble.ContextMenuStrip, menu) && form._premiumComposer.Text == draft,
                    label + $": closing the reusable popup preserves its owner, context menu and draft ({repeat + 1})");
            }
            row.Dispose();
            Require(menu.IsDisposed, label + ": disposing the message releases its owned native popup");
        }

        using (var keyboardRow = (MessageRow)form.BuildMessageBubble(Sample("text", true), "Klavye menüsü", null))
        {
            form._messageList.Controls.Add(keyboardRow); Application.DoEvents();
            var button = keyboardRow.ActionButtonForQa;
            var menu = (ModernContextMenu)keyboardRow.Controls.OfType<RoundedPanel>().Single().ContextMenuStrip!;
            keyboardRow.TrackActionPointer(new Point(-100, -100));
            button.Select(); button.Focus(); Application.DoEvents();
            Require(button.Focused && button.Revealed,
                "Keyboard focus reveals the arrow even when the pointer is outside the message");
            var down = new KeyEventArgs(Keys.Down);
            typeof(MessageActionButton).GetMethod("OnKeyDown", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(button, [down]);
            Application.DoEvents();
            Require(down.Handled && down.SuppressKeyPress && menu.Visible,
                "The actual focused arrow's Down key handler opens the message menu without editing the composer");
            var escape = Message.Create(menu.Handle, 0x0100, new IntPtr((int)Keys.Escape), IntPtr.Zero);
            ProcessConversationMenuKey(menu, ref escape); Application.DoEvents();
            Require(!menu.Visible && !menu.IsDisposed && form._premiumComposer.Text == draft,
                "Native menu Escape dismisses the message popup without disposing it or changing the draft");
        }

        using (var expired = (MessageRow)form.BuildMessageBubble(Sample("text", true,
            deadline: now.AddSeconds(-1)), "Süresi dolmuş mesaj", null))
        {
            form._messageList.Controls.Add(expired); Application.DoEvents();
            var menu = (ModernContextMenu)expired.Controls.OfType<RoundedPanel>().Single().ContextMenuStrip!;
            expired.ActionButtonForQa.PerformClick(); Application.DoEvents();
            Require(menu.Visible && !ConversationMenuItem(menu, "Herkesten sil").Available &&
                ConversationMenuItem(menu, "Benden sil").Available,
                "An expired own message keeps personal deletion but hides Delete for everyone at popup opening");
            menu.Close();
        }
        var boundary = now.AddMinutes(15);
        var boundaryMessage = Sample("text", true, deadline: boundary);
        Require(CanDeleteForEveryone(boundaryMessage, me.Id, boundary.AddTicks(-1)) &&
            !CanDeleteForEveryone(boundaryMessage, me.Id, boundary) &&
            !CanDeleteForEveryone(boundaryMessage, me.Id, boundary.AddTicks(1)) &&
            !CanDeleteForEveryone(boundaryMessage, fixture.FirstSender.Id, now) &&
            !CanDeleteForEveryone(boundaryMessage with { DeleteForEveryoneUntil = null }, me.Id, now) &&
            !CanDeleteForEveryone(boundaryMessage with { DeletedForEveryone = true }, me.Id, now),
            "The shared server-deadline gate accepts only the sender strictly before expiry, never at/after expiry or without a deadline");

        // Exercise the actual decrypt failure path, rather than merely assigning
        // a disabled flag on a handcrafted menu. The in-process handler rejects
        // all message mutations and returns only fixture device keys.
        var invalid = fixture.Message(room, fixture.FirstSender, "Bu içerik gösterilemez.");
        invalid = invalid with { Payloads = [invalid.Payloads[0] with { Signature = Convert.ToBase64String(new byte[64]) }] };
        var blockedTask = form.CreateMessageBubbleAsync(invalid);
        HistoryQaPump(blockedTask);
        using (var blocked = (MessageRow)blockedTask.GetAwaiter().GetResult())
        {
            form._messageList.Controls.Add(blocked); Application.DoEvents();
            var menu = (ModernContextMenu)blocked.Controls.OfType<RoundedPanel>().Single().ContextMenuStrip!;
            blocked.ActionButtonForQa.PerformClick(); Application.DoEvents();
            Require(!blocked.CanStar && !blocked.CanPin && !blocked.CanMarkRead &&
                HistoryQaHasText(blocked, "Mesaj açılamadı") && !HistoryQaHasText(blocked, "Bu içerik gösterilemez."),
                "An invalid signed envelope remains a non-readable placeholder with no leaked plaintext");
            Require(!ConversationMenuItem(menu, "Metni kopyala").Enabled &&
                !ConversationMenuItem(menu, "Yıldızla").Enabled &&
                !ConversationMenuItem(menu, "Mesajı sabitle").Available &&
                ConversationMenuItem(menu, "Benden sil").Enabled,
                "A decrypt-blocked message disables copy/star and hides pin while retaining personal deletion");
            CaptureConversationMenu(menu, null, Path.Combine(directory, "message-menu-decrypt-blocked.png"));
            menu.Close();
        }

        Require(ReferenceEquals(form._selectedConversation, selected) && form._premiumComposer.Text == draft &&
            fixture.Dialogs == 0 && form._messageList.Controls.Count == 0,
            "All pointer/menu/keyboard probes leave selection and draft intact without modal errors or surviving fixture rows");
        checks.Add("Native popup/button geometry was checked at the available " + form.DeviceDpi +
            " DPI; physical cross-monitor changes, real clipboard/deletion, remote accounts and audio devices were not exercised.");
        return checks;
    }

    private static byte[] MessageActionsQaImage()
    {
        using var bitmap = new Bitmap(160, 96);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Theme.SurfaceRaised);
            using var brush = new SolidBrush(Theme.Violet);
            graphics.FillEllipse(brush, 44, 12, 72, 72);
        }
        using var stream = new MemoryStream();
        bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
        return stream.ToArray();
    }

    private static byte[] MessageActionsQaWave()
    {
        using var stream = new MemoryStream();
        using (var writer = new NAudio.Wave.WaveFileWriter(stream, new NAudio.Wave.WaveFormat(16000, 16, 1)))
            writer.Write(new byte[3200], 0, 3200);
        return stream.ToArray();
    }
}
