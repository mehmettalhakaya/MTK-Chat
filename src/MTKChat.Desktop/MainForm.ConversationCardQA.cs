using System.Globalization;

namespace MTKChat.Desktop;

internal sealed partial class MainForm
{
    internal static IReadOnlyList<string> VerifyConversationCards(string directory)
    {
        using var context = new HistoryQaUiContext();
        using var fixture = new HistoryQaFixture();
        var form = fixture.Form;
        var checks = new List<string>();
        void Require(bool condition, string check)
        {
            HistoryQaAssertUiThread();
            if (!condition) throw new InvalidOperationException("Conversation card QA: " + check);
            checks.Add(check);
        }
        var older = new DateTimeOffset(2026, 10, 1, 23, 17, 0, TimeSpan.FromHours(3));
        var expected = older.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
        var yesterday = DateTimeOffset.Now.AddDays(-1);
        Require(ConversationTime(older) == expected && ConversationTime(older.AddYears(-1)) == expected &&
            ConversationTime(yesterday) == yesterday.ToString("HH:mm", CultureInfo.InvariantCulture),
            "Old, yesterday and previous-year messages use local HH:mm, never a date or relative day");
        Require(ConversationTime(null) == "", "An empty conversation has no timestamp");
        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");
            Require(ConversationTime(older) == expected, "The sidebar clock is invariant under another calendar culture");
        }
        finally { CultureInfo.CurrentCulture = previousCulture; }

        foreach (var kind in new[] { "direct", "group" })
        foreach (var unreadCount in new[] { 0, 2, 125 })
        {
            var room = fixture.InitialRoom with
            {
                Id = Guid.NewGuid(), Kind = kind, Title = "Uzun isimli kullanıcı veya grup ile bir sohbet",
                LastMessageAt = older, UnreadCount = unreadCount
            };
            using var card = form.CreateConversationCard(room);
            form._conversationList.Controls.Add(card);
            form.EnsurePreviewOwner();
            form._conversationPreviews[room.Id] = new(older, older,
                "Uzun mesaj önizlemesi 😊 saat ve rozetin alanına taşmamalı", null, DateTimeOffset.UtcNow.AddMinutes(1));
            form.ApplyConversationPreviewLabel(room);
            var title = card.Controls.Find("ConversationTitle", true).OfType<Label>().Single();
            var preview = card.Controls.Find("ConversationLastMessagePreview", true).OfType<Label>().Single();
            var clock = card.Controls.Find("ConversationLastMessageTime", true).OfType<Label>().Single();
            var badge = card.Controls.Find("ConversationUnreadBadge", true).OfType<RoundedPanel>().Single();
            var originalFonts = new[] { title.Font, preview.Font, clock.Font };
            foreach (var fontFactor in new[] { 1f, 1.25f })
            {
                using var titleFont = new Font(originalFonts[0].FontFamily, originalFonts[0].Size * fontFactor);
                using var previewFont = new Font(originalFonts[1].FontFamily, originalFonts[1].Size * fontFactor);
                using var clockFont = new Font(originalFonts[2].FontFamily, originalFonts[2].Size * fontFactor);
                title.Font = titleFont; preview.Font = previewFont; clock.Font = clockFont;
                foreach (var logicalWidth in new[] { 180, 240, 352 })
                {
                    card.Width = (int)Math.Round(logicalWidth * form.DeviceDpi / 96f);
                    card.PerformLayout();
                    static Rectangle InCard(Control child, Control parent) => parent.RectangleToClient(child.RectangleToScreen(child.ClientRectangle));
                    var previewRect = InCard(preview, card);
                    var titleRect = InCard(title, card);
                    var suffix = $"{kind}, unread={unreadCount}, width={logicalWidth}, font={fontFactor}, DPI={form.DeviceDpi}";
                    Require(previewRect.Top == clock.Top && previewRect.Height == clock.Height &&
                        previewRect.Right < clock.Left && clock.Right < card.ClientSize.Width,
                        "Preview and clock share the lower row without overlap (" + suffix + ")");
                    Require(!titleRect.IntersectsWith(clock.Bounds) && (!badge.Visible ||
                        !titleRect.IntersectsWith(badge.Bounds) && badge.Bottom <= clock.Top),
                        "Title and unread badge remain separate from the lower clock (" + suffix + ")");
                    Require(clock.Width >= TextRenderer.MeasureText(clock.Text, clock.Font, Size.Empty,
                            TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width &&
                        title.Height >= title.Font.Height && preview.Height >= preview.Font.Height && clock.Height >= clock.Font.Height,
                        "Clock digits and increased text height are not clipped (" + suffix + ")");
                    Require(ReferenceEquals(card.GetChildAtPoint(new Point(clock.Left + clock.Width / 2, clock.Top + clock.Height / 2)), clock) &&
                        (!badge.Visible || ReferenceEquals(card.GetChildAtPoint(new Point(badge.Left + badge.Width / 2, badge.Top + badge.Height / 2)), badge)),
                        "The native text panel cannot occlude the clock or unread badge (" + suffix + ")");
                }
                var layoutPasses = 0;
                LayoutEventHandler countLayout = (_, _) => layoutPasses++;
                card.Layout += countLayout;
                var beforeBounds = new[] { title.Bounds, preview.Bounds, clock.Bounds, badge.Bounds };
                for (var pass = 0; pass < 10; pass++) card.PerformLayout();
                card.Layout -= countLayout;
                Require(layoutPasses == 10 && beforeBounds.SequenceEqual(new[] { title.Bounds, preview.Bounds, clock.Bounds, badge.Bounds }),
                    "Repeated card layout is stable and has no re-entrant loop (" + kind + ", font=" + fontFactor + ")");
                title.Font = originalFonts[0]; preview.Font = originalFonts[1]; clock.Font = originalFonts[2];
            }
            // Keep text/time coherent even if the list summary is still older.
            var newer = older.AddHours(1).AddMinutes(12);
            form._conversationPreviews[room.Id] = new(newer, older, "Daha yeni mesaj", null, DateTimeOffset.UtcNow.AddMinutes(1));
            form.ApplyConversationPreviewLabel(room);
            Require(clock.Text == ConversationTime(newer) && preview.Text == "Daha yeni mesaj",
                "A lagging summary cannot give the newer preview an older clock (" + kind + ")");
            form._conversationPreviews[room.Id] = new(null, older, "", null, DateTimeOffset.UtcNow.AddMinutes(1));
            form.ApplyConversationPreviewLabel(room);
            Require(clock.Text == ConversationTime(newer) && preview.Text == "", "An emptied history blanks plaintext but retains the observed activity clock (" + kind + ")");
            form._conversationPreviews.Remove(room.Id);
        }

        Directory.CreateDirectory(directory);
        var sample = fixture.InitialRoom with { LastMessageAt = older };
        var sampleCard = fixture.Card(fixture.InitialRoom);
        sampleCard.Tag = sample;
        form._conversationPreviews[sample.Id] = new(older, older, "Son mesaj burada, saat yanındaki alt satırda", null, DateTimeOffset.UtcNow.AddMinutes(1));
        form.ApplyConversationPreviewLabel(sample);
        foreach (var width in new[] { 1120, 1380, 1920 })
        {
            form.Size = new Size(width, width == 1120 ? 720 : 860);
            form.PerformLayout(); form.ResizeConversationCards(); Application.DoEvents();
            var clock = sampleCard.Controls.Find("ConversationLastMessageTime", true).OfType<Label>().Single();
            var preview = sampleCard.Controls.Find("ConversationLastMessagePreview", true).OfType<Label>().Single();
            Require(sampleCard.PointToClient(preview.PointToScreen(Point.Empty)).Y == clock.Top && clock.Text == expected,
                "The real sidebar keeps the clock on the preview row at window width " + width);
            using var image = new Bitmap(form.ClientSize.Width, form.ClientSize.Height);
            form.DrawToBitmap(image, form.ClientRectangle);
            image.Save(Path.Combine(directory, $"conversation-clock-{width}.png"));
        }
        return checks;
    }
}
