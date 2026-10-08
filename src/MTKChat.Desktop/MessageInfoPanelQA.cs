using System.Drawing.Imaging;
using System.Globalization;
using MTKChat.Contracts;

namespace MTKChat.Desktop;

// An app-owned, deterministic UI probe. The fake handler rejects every request;
// avatar fixtures have no PhotoVersion, so this does not contact the live server.
internal static class MessageInfoPanelQA
{
    internal static IReadOnlyList<string> Verify(string directory)
    {
        Directory.CreateDirectory(directory);
        using var handler = new NoNetworkHandler();
        using var api = new ChatApiClient("https://receipt-qa.invalid/", handler);
        using var host = new Form
        {
            ClientSize = new Size(340, 700), ShowInTaskbar = false,
            BackColor = Theme.Sidebar, AutoScaleMode = AutoScaleMode.None,
            Text = "MTK Chat · Message info QA"
        };
        var panel = new MessageInfoPanel(new AvatarCache(api)) { Dock = DockStyle.Fill };
        host.Controls.Add(panel);
        host.Show();
        Pump(host);
        var checks = new List<string>();
        void Require(bool valid, string name)
        {
            if (!valid) throw new InvalidOperationException("Message info QA: " + name);
            checks.Add(name);
        }
        static Guid Id(int value) => Guid.Parse($"10000000-0000-0000-0000-{value:000000000000}");
        static ChatUser User(int id, string name, string role = "user", bool bot = false) =>
            new(Id(id), name, "", bot, null, role, PhotoVersion: null);
        var siteAdmin = User(1, "MTK", "admin");
        var groupAdmin = User(2, "Zeynep Kaya");
        var mod = User(3, "Kerem Arslan");
        var normal = User(4, "Elif Demir");
        var readOnly = User(6, "Ahmet Yılmaz");
        var nonRecipient = User(7, "Sonradan katılan kişi");
        var bot = User(8, "Gemini Agent", bot: true);
        var sent = new DateTimeOffset(2026, 10, 1, 18, 22, 0, TimeSpan.Zero);
        var delivered = sent.AddSeconds(4);
        var read = sent.AddMinutes(1);
        IReadOnlyDictionary<Guid, string> roles = new Dictionary<Guid, string>
        {
            [siteAdmin.Id] = "user", [groupAdmin.Id] = "admin", [mod.Id] = "mod", [normal.Id] = "user"
        };
        var recipients = new RecipientReceipt[]
        {
            new(siteAdmin.Id, delivered, read), new(groupAdmin.Id, delivered, null),
            new(mod.Id, delivered, null), new(normal.Id, null, null),
            new(Id(5), delivered, null), new(readOnly.Id, null, read),
            new(normal.Id, null, null), new(mod.Id, delivered.AddSeconds(2), null),
            new(bot.Id, delivered, read)
        };
        var view = new MessageInfoView(Id(100), Id(101), sent, "image", new MessageDelivery("read", recipients),
            [siteAdmin, groupAdmin, mod, normal, readOnly, nonRecipient, bot], roles);
        panel.Render(view);
        Pump(host);
        Require(panel.RenderedMessageId == view.MessageId && panel.Counts == (3, 3, 1),
            "Original recipients are classified into read/delivered/pending sections");
        Require(panel.RecipientIds.Count == 7 && panel.RecipientIds.Distinct().Count() == 7,
            "Duplicate receipt IDs appear only once");
        Require(!panel.RecipientIds.Contains(nonRecipient.Id),
            "A current member absent from the original receipt list is not invented as a recipient");
        var departed = Recipient(panel, Id(5));
        Require(departed.AccessibleName == "Kullanıcı · User" && Labels(departed).First().ForeColor == Theme.Text,
            "Unknown or departed recipients retain their real ID with a white fallback heading");
        var readOnlyCard = Recipient(panel, readOnly.Id);
        Require(readOnlyCard.AccessibleDescription == "Okundu · " + Time(read),
            "ReadAt-only receipt displays its actual read time without inventing a delivery time");
        Require(Recipient(panel, mod.Id).AccessibleDescription == "Teslim · " + Time(delivered.AddSeconds(2)),
            "Duplicate receipt metadata selects the latest observed delivery time");
        Require(Labels(Recipient(panel, siteAdmin.Id)).First().ForeColor == Theme.Warning &&
            Labels(Recipient(panel, groupAdmin.Id)).First().ForeColor == Theme.Warning &&
            Labels(Recipient(panel, mod.Id)).First().ForeColor == UserPresentation.RoleColor(mod, "mod") &&
            Labels(Recipient(panel, normal.Id)).First().ForeColor == Theme.Text &&
            Labels(Recipient(panel, bot.Id)).First().ForeColor == Theme.Bot,
            "Site admin/group admin/mod/user/bot headings keep their distinct role colors");
        Require(Recipient(panel, normal.Id).AccessibleDescription == "Teslim bilgisi henüz yok" &&
            Descendants(panel).OfType<Control>().All(control =>
                !(control.AccessibleDescription ?? "").Contains("okunmadı", StringComparison.OrdinalIgnoreCase)),
            "Missing read metadata is not displayed as proof of an unread message");
        Require(Find(panel, "MessageInfoKind").Text == "Görsel mesajı" &&
            Find(panel, "MessageInfoSent").Text == "Gönderildi · " + Time(sent),
            "Message kind and local send timestamp are rendered without message content");
        checks.AddRange(panel.VerifyLayout().Select(check => "340 px: " + check));
        Capture(panel, Path.Combine(directory, "message-info-340x700.png"));
        Require(File.Exists(Path.Combine(directory, "message-info-340x700.png")),
            "340x700 receipt panel screenshot is generated");

        var extras = Enumerable.Range(10, 36).Select(index => User(index,
            index == 10 ? "Çok uzun kullanıcı adı: üst üste binmeden sınırda kesilmeli" : "Katılımcı " + index)).ToArray();
        var expanded = view with
        {
            Participants = view.Participants.Concat(extras).ToArray(),
            Delivery = new MessageDelivery("delivered", recipients.Concat(extras.Select((user, index) =>
                new RecipientReceipt(user.Id, delivered.AddMinutes(index), index % 3 == 0 ? read.AddMinutes(index) : null))).ToArray())
        };
        panel.Render(expanded);
        Pump(host);
        Require(panel.MaximumScrollOffset > 0, "Many receipt rows create a scrollable panel");
        checks.AddRange(panel.VerifyLayout().Select(check => "Overflow: " + check));
        checks.AddRange(panel.VerifyScrolling().Select(check => "Receipt scroll: " + check));
        var list = (FlowLayoutPanel)Find(panel, "MessageInfoRecipients");
        var viewport = (ModernConversationViewport)Find(panel, "MessageInfoViewport");
        list.AutoScrollPosition = new Point(0, panel.MaximumScrollOffset / 2);
        viewport.SynchronizeNow();
        Pump(host);
        var offset = panel.ScrollOffset;
        var originalControls = list.Controls.Cast<Control>().ToArray();
        panel.Render(expanded with { Participants = expanded.Participants.ToArray(),
            Delivery = new MessageDelivery(expanded.Delivery!.Status, expanded.Delivery.Recipients.ToArray()) });
        Pump(host);
        Require(originalControls.SequenceEqual(list.Controls.Cast<Control>()) && panel.ScrollOffset == offset,
            "Unchanged polling preserves the actual receipt controls and scroll offset");

        // A genuine metadata change rebuilds rows while retaining a clamped offset.
        list.AutoScrollPosition = new Point(0, panel.MaximumScrollOffset);
        viewport.SynchronizeNow();
        Pump(host);
        var oldMaximum = panel.MaximumScrollOffset;
        var reduced = expanded with
        {
            Delivery = new MessageDelivery("read", expanded.Delivery!.Recipients.Take(18).ToArray())
        };
        panel.Render(reduced);
        Pump(host);
        Require(panel.MaximumScrollOffset < oldMaximum && panel.ScrollOffset >= 0 &&
            panel.ScrollOffset <= panel.MaximumScrollOffset,
            "Updated shorter receipt data clamps the previous offset to its new range");
        var updated = view with { Delivery = new MessageDelivery("read", recipients.Select(receipt =>
            receipt.UserId == normal.Id ? receipt with { DeliveredAt = delivered, ReadAt = read } : receipt).ToArray()) };
        panel.Render(updated);
        Pump(host);
        Require(panel.Counts == (4, 3, 0) && panel.RecipientIds.Count == 7,
            "A recipient moves from pending to read without being duplicated across sections");

        host.ClientSize = new Size(320, 600);
        panel.Render(view);
        Pump(host);
        checks.AddRange(panel.VerifyLayout().Select(check => "320 px: " + check));
        // The offset-continuity check above intentionally scrolled to the bottom.
        // Capture the narrow panel's normal opening state, not that testing offset.
        list.AutoScrollPosition = Point.Empty;
        viewport.SynchronizeNow();
        Pump(host);
        Capture(panel, Path.Combine(directory, "message-info-320x600.png"));
        Require(File.Exists(Path.Combine(directory, "message-info-320x600.png")),
            "320x600 narrow receipt panel screenshot is generated");
        var close = Find(panel, "MessageInfoClose") as ModernButton;
        var closeEvents = 0;
        panel.CloseRequested += (_, _) => closeEvents++;
        close!.PerformClick();
        Require(closeEvents == 1, "Panel close button raises one non-modal close event");

        panel.Render(view with { MessageId = Id(200), Delivery = null });
        Pump(host);
        Require(panel.RecipientIds.Count == 0 && panel.Counts == (0, 0, 0) &&
            Find(panel, "MessageInfoUnavailable") is not null &&
            !Descendants(panel).Any(control => control.Name.StartsWith("ReceiptRecipient_", StringComparison.Ordinal)),
            "Missing delivery metadata shows an unavailable state with no fake recipients or timestamps");
        panel.Render(view with { MessageId = Id(201), Delivery = new MessageDelivery("sent", Array.Empty<RecipientReceipt>()) });
        Pump(host);
        Require(panel.RecipientIds.Count == 0 && panel.Counts == (0, 0, 0) && panel.ScrollOffset == 0,
            "An empty recipient set stays empty and a different message resets the scroll");
        Require(handler.Requests == 0, "All receipt and avatar QA uses zero HTTP requests");
        host.Hide();
        return checks;
    }

    private static void Pump(Form form)
    {
        for (var iteration = 0; iteration < 3; iteration++)
        { form.PerformLayout(); Application.DoEvents(); form.Update(); }
    }
    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls)
        { yield return child; foreach (var nested in Descendants(child)) yield return nested; }
    }
    private static Control Find(Control parent, string name) => Descendants(parent).Single(control => control.Name == name);
    private static Control Recipient(Control panel, Guid id) => Find(panel, "ReceiptRecipient_" + id.ToString("N"));
    private static IEnumerable<Label> Labels(Control parent) => parent.Controls.OfType<Label>();
    private static string Time(DateTimeOffset time) => time.ToLocalTime().ToString("dd MMM yyyy · HH:mm", CultureInfo.GetCultureInfo("tr-TR"));
    private static void Capture(Control control, string path)
    {
        using var bitmap = new Bitmap(control.Width, control.Height);
        control.DrawToBitmap(bitmap, control.ClientRectangle);
        bitmap.Save(path, ImageFormat.Png);
    }
    private sealed class NoNetworkHandler : HttpMessageHandler
    {
        internal int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            throw new InvalidOperationException("Message info QA unexpectedly attempted network access.");
        }
    }
}
