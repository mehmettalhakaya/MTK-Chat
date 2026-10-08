using MTKChat.Contracts;

namespace MTKChat.Desktop;

// App-owned synthetic rendering checks: no account, network, clipboard or
// production conversation is required to exercise the same layout as MessageText.
internal static class InlineEmojiTextQA
{
    internal static IReadOnlyList<string> Verify(string directory)
    {
        Directory.CreateDirectory(directory);
        var checks = new List<string>();
        void Require(bool value, string name)
        { if (!value) throw new InvalidOperationException("Inline emoji QA: " + name); checks.Add(name); }
        var samples = new[]
        {
            "Merhaba 🙂 Size nasıl yardımcı olabilirim?",
            "Merhaba ☺ Size nasıl yardımcı olabilirim? ☺️",
            "Türkçe: ığüşöç İĞÜŞÖÇ 😊 — İyi akşamlar ❤️",
            "Birinci satır 😊\r\nİkinci satır ❤️\n\nDördüncü satır 👍",
            "Emoji: 🙂🙂🙂🙂 ve metin, sonra 🎉!",
            "Desteklenmeyen: 👩‍💻 👨‍👩‍👧‍👦 👍🏽 🇹🇷 ☺︎; desteklenen 😊",
            "e\u0301 birleşik işaret 😊\tsekme\t❤️",
            "uzun:" + new string('ğ', 2400) + " 😊",
            "boşluk" + new string(' ', 3000) + " 😊",
            "Bozuk UTF-16: \uD800 😊 \uDC00"
        };
        foreach (var dpi in new[] { 96, 120, 144, 192 })
        {
            using var bitmap = new Bitmap(1100, 700); bitmap.SetResolution(dpi, dpi);
            using var graphics = Graphics.FromImage(bitmap);
            using var font = Theme.Font(12);
            foreach (var text in samples)
            {
                var tokens = InlineEmojiTextLayout.Tokenize(text);
                Require(string.Concat(tokens.Select(token => token.Text)) == text,
                    $"Tokenizer preserves exact original UTF-16, whitespace and line breaks at {dpi} DPI");
                foreach (var width in new[] { 48, 180, 620 })
                {
                    var layout = InlineEmojiTextLayout.Create(graphics, text, font, width, tokens);
                    Require(layout.OriginalText == text && layout.EmojiCount > 0 && layout.Size.Width <= width &&
                        layout.Size.Height == layout.LineCount * layout.LineHeight &&
                        layout.Runs.All(run => run.Bounds.X >= 0 && run.Bounds.Y >= 0 &&
                            run.Bounds.Right <= width && run.Bounds.Bottom <= layout.Size.Height),
                        $"Shared measurement/draw bounds fit {width}px at {dpi} DPI without splitting a grapheme");
                    Require(layout.Runs.Where(run => run.Emoji is not null).All(run =>
                        EmojiCatalog.TryGet(run.Text, out var definition) && definition == run.Emoji),
                        "Every vector run corresponds to a complete supported original Unicode grapheme");
                    // Clip to an ordinary viewport just like the real control;
                    // long history text never requires a bitmap of its full height.
                    graphics.Clear(Theme.Canvas);
                    graphics.SetClip(new Rectangle(0, 0, width, bitmap.Height));
                    layout.Draw(graphics, font, Color.White, Point.Empty);
                    graphics.ResetClip();
                }
            }
            var unsupported = InlineEmojiTextLayout.Create(graphics, "👩‍💻 👍🏽 🇹🇷 ☺︎", font, 620);
            Require(unsupported.EmojiCount == 0 && unsupported.Runs.All(run => run.Emoji is null),
                $"Joined, modified, flag and text-presentation sequences never become partial vectors at {dpi} DPI");
            const string tinyText = "A\u00a0e\u0301\t👩‍💻😊\u202fB";
            foreach (var width in new[] { 1, 2, 12 })
            {
                var tiny = InlineEmojiTextLayout.Create(graphics, tinyText, font, width);
                Require(tiny.OriginalText == tinyText && string.Concat(tiny.Runs.Select(run => run.Text)) == tinyText &&
                    tiny.EmojiCount == 1 && tiny.Size.Width <= width &&
                    tiny.Runs.All(run => run.Bounds.X >= 0 && run.Bounds.Right <= width &&
                        run.Bounds.Y >= 0 && run.Bounds.Bottom <= tiny.Size.Height),
                    $"Tiny {width}px layout clips intact graphemes and preserves NBSP, narrow NBSP and tabs at {dpi} DPI");
                graphics.Clear(Theme.Canvas);
                graphics.SetClip(new Rectangle(0, 0, width, bitmap.Height));
                tiny.Draw(graphics, font, Color.White, Point.Empty);
                graphics.ResetClip();
            }
        }
        using (var bitmap = new Bitmap(500, 100))
        using (var graphics = Graphics.FromImage(bitmap))
        using (var font = Theme.Font(12))
        {
            graphics.Clear(Theme.Canvas);
            var cache = new InlineEmojiTextCache();
            var first = cache.Get(graphics, "Merhaba 🙂", font, 480)!;
            var second = cache.Get(graphics, "Merhaba 🙂", font, 220)!;
            var count = cache.BuildCountForQa;
            for (var index = 0; index < 100; index++)
            {
                Require(ReferenceEquals(cache.Get(graphics, "Merhaba 🙂", font, 480), first) &&
                    ReferenceEquals(cache.Get(graphics, "Merhaba 🙂", font, 220), second),
                    "Repaint reuses both measured widths instead of reparsing or relaying out message history");
            }
            Require(cache.BuildCountForQa == count, "One hundred repaints allocate no new inline layouts");
            first.Draw(graphics, font, Color.White, Point.Empty);
            var emoji = first.Runs.Single(run => run.Emoji is not null).Bounds;
            var colored = 0;
            for (var y = emoji.Top; y < emoji.Bottom; y++)
                for (var x = emoji.Left; x < emoji.Right; x++)
                { var pixel = bitmap.GetPixel(x, y); if (pixel.R > 180 && pixel.G > 100 && pixel.B < 130) colored++; }
            Require(colored > 8, "Mixed text contains actual yellow vector emoji pixels, not a monochrome font glyph");
            using var changedFont = Theme.Font(15);
            Require(!ReferenceEquals(cache.Get(graphics, "Merhaba 🙂", changedFont, 480), first),
                "Changing the font invalidates measured inline geometry");
            Require(cache.Get(graphics, "Normal yazı", font, 480) is null,
                "Emoji-free text retains the original TextRenderer path");
            foreach (var text in new[] { "مرحبا 😊 كيف حالك", "שלום 😊 עולם", "abc \u202e😊 xyz" })
                Require(cache.Get(graphics, text, font, 480) is null,
                    "RTL scripts and explicit directional controls retain native paragraph shaping and exact Unicode");
            Require(cache.Get(graphics, "Yeni 😊", font, 480)!.OriginalText == "Yeni 😊",
                "Changing text cannot reuse a stale message layout");
            var richBeforeOversize = cache.BuildCountForQa;
            var oversized = new string('ğ', InlineEmojiTextCache.MaximumRichTextLength) + "\u00a0😊\r\n\uD800";
            for (var index = 0; index < 100; index++)
                Require(cache.Get(graphics, oversized, font, index % 2 == 0 ? 480 : 220) is null,
                    "Oversized original Unicode text selects the native rendering path without a rich layout");
            Require(cache.BuildCountForQa == richBeforeOversize,
                "One hundred oversized-text repaints build no inline geometry or stale previous-message layout");
            var atBudget = "😊" + new string('a', InlineEmojiTextCache.MaximumRichTextLength - "😊".Length);
            Require(cache.Get(graphics, atBudget, font, 480) is { } boundary && boundary.OriginalText == atBudget &&
                boundary.OriginalText.Length == InlineEmojiTextCache.MaximumRichTextLength,
                "The exact 16K UTF-16 presentation boundary still preserves and richly renders complete text");
            Require(cache.Get(graphics, "Sonra 😊", font, 480)!.OriginalText == "Sonra 😊",
                "A normal message resumes rich rendering after an oversized native fallback");
            cache.Clear();
            Require(!ReferenceEquals(cache.Get(graphics, "Merhaba 🙂", font, 480), first),
                "Explicit DPI/text invalidation discards cached geometry safely");
        }

        var registeredBefore = EmojiAnimationScheduler.RegisteredTargetCount;
        var sender = new ChatUser(Guid.NewGuid(), "Inline emoji QA", "inline@invalid.test", false, null);
        var room = Guid.NewGuid();
        StoredMessage Message(string kind = "text", bool deleted = false) => new(Guid.NewGuid(), Guid.NewGuid(),
            room, sender.Id, kind, DateTimeOffset.Now, null, deleted, [], null, null);
        using (var host = new Form
        {
            ClientSize = new Size(860, 640), BackColor = Theme.Canvas, ShowInTaskbar = false,
            Text = "MTK Chat · Inline emoji QA", StartPosition = FormStartPosition.CenterScreen
        })
        {
            var panel = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown,
                WrapContents = false, AutoScroll = true, Padding = new Padding(20)
            };
            host.Controls.Add(panel);
            var rows = samples.Take(6).Select((text, index) =>
                new MessageRow(sender, index % 2 == 1, Message(), text, null) { Width = 790 }).ToArray();
            foreach (var row in rows) panel.Controls.Add(row);
            host.Show(); Application.DoEvents();
            foreach (var row in rows)
            {
                foreach (var width in new[] { 240, 360, 790 }) { row.Width = width; row.VerifyLayout(); }
                row.Width = 790;
                Require(row.StarredPreview == samples[Array.IndexOf(rows, row)],
                    "Received/sent rich messages preserve exact copy, pin and star text");
                row.SetStarred(true); row.VerifyLayout();
            }
            using (var bitmap = new Bitmap(host.Width, host.Height))
            { host.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size)); bitmap.Save(Path.Combine(directory, "inline-emoji-messages.png")); }
            using (var deleted = new MessageRow(sender, false, Message(deleted: true), "Silindi 😊", null))
            using (var file = new MessageRow(sender, false, Message("file"), "Dosya 😊", null))
            { deleted.VerifyLayout(); file.VerifyLayout(); Require(!deleted.CanMarkRead && file.StarredPreview == "Dosya 😊", "Deleted/file flows retain their original non-rich semantics"); }
            host.Close();
        }
        Require(EmojiAnimationScheduler.RegisteredTargetCount == registeredBefore,
            "Mixed-message vector presentation creates no per-emoji HWND, timer or animation subscription");
        return checks;
    }
}
