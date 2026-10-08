using System.Buffers;
using System.Globalization;
using System.Text;

namespace MTKChat.Desktop;

// Unicode remains the source of truth. This is a local presentation layout, not
// a replacement string or an image embedded in the encrypted message payload.
// Supported complete graphemes become vector runs; all other graphemes stay text.
internal sealed class InlineEmojiTextLayout
{
    internal const TextFormatFlags TextFlags = TextFormatFlags.SingleLine |
        TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.TextBoxControl;

    internal sealed record Run(string Text, EmojiDefinition? Emoji, Rectangle Bounds);
    internal sealed record Token(string Text, EmojiDefinition? Emoji, bool Whitespace, bool NewLine);

    internal string OriginalText { get; }
    internal IReadOnlyList<Run> Runs { get; }
    internal Size Size { get; }
    internal int EmojiCount { get; }
    internal int LineCount { get; }
    internal int LineHeight { get; }

    private InlineEmojiTextLayout(string text, IReadOnlyList<Run> runs, Size size,
        int emojiCount, int lines, int lineHeight)
    { OriginalText = text; Runs = runs; Size = size; EmojiCount = emojiCount; LineCount = lines; LineHeight = lineHeight; }

    internal static IReadOnlyList<Token> Tokenize(string text)
    {
        var tokens = new List<Token>();
        var elements = StringInfo.GetTextElementEnumerator(text);
        var pendingStart = -1;
        var pendingEnd = 0;
        var pendingWhitespace = false;
        void Flush()
        {
            if (pendingStart < 0) return;
            tokens.Add(new Token(text[pendingStart..pendingEnd], null, pendingWhitespace, false));
            pendingStart = -1;
        }
        while (elements.MoveNext())
        {
            var element = elements.GetTextElement();
            var start = elements.ElementIndex;
            if (element is "\r" or "\n" or "\r\n" or "\u0085" or "\u2028" or "\u2029")
            {
                Flush(); tokens.Add(new Token(element, null, false, true)); continue;
            }
            if (EmojiCatalog.TryGet(element, out var emoji))
            {
                Flush(); tokens.Add(new Token(element, emoji, false, false)); continue;
            }
            var whitespace = string.IsNullOrWhiteSpace(element);
            if (pendingStart >= 0 && pendingWhitespace != whitespace) Flush();
            if (pendingStart < 0) { pendingStart = start; pendingWhitespace = whitespace; }
            pendingEnd = start + element.Length;
        }
        Flush();
        return tokens.AsReadOnly();
    }

    internal static InlineEmojiTextLayout Create(Graphics graphics, string text, Font font, int width) =>
        Create(graphics, text, font, width, Tokenize(text));

    internal static InlineEmojiTextLayout Create(Graphics graphics, string text, Font font, int width,
        IReadOnlyList<Token> tokens)
    {
        var available = Math.Max(1, width);
        var height = Math.Max(1, (int)Math.Ceiling(font.GetHeight(graphics)) + 1);
        var emojiSide = Math.Min(available, height);
        var runs = new List<Run>();
        var x = 0; var y = 0; var maximumX = 0; var lines = 1; var emojiCount = 0;
        int Measure(string value) => value.Length == 0 ? 0 : TextRenderer.MeasureText(graphics,
            value, font, new Size(int.MaxValue, int.MaxValue), TextFlags).Width;
        void NewLine() { maximumX = Math.Max(maximumX, x); x = 0; y += height; lines++; }
        void Add(string value, EmojiDefinition? emoji, int runWidth)
        {
            runs.Add(new Run(value, emoji, new Rectangle(x, y, runWidth, height)));
            x += runWidth; maximumX = Math.Max(maximumX, x);
        }
        foreach (var token in tokens)
        {
            if (token.NewLine) { NewLine(); continue; }
            if (token.Emoji is not null)
            {
                if (x > 0 && x + emojiSide > available) NewLine();
                Add(token.Text, token.Emoji, emojiSide); emojiCount++; continue;
            }
            // Explicit tab stops avoid depending on how GDI expands a tab inside
            // a separately drawn word. The original tab is retained in Unicode.
            if (token.Whitespace)
            {
                var space = Math.Max(1, Measure(" "));
                var pending = 0; var whiteStart = 0;
                void FlushWhite(int end)
                {
                    if (end == whiteStart) return;
                    Add(token.Text[whiteStart..end], null, pending);
                    pending = 0; whiteStart = end;
                }
                for (var index = 0; index < token.Text.Length; index++)
                {
                    var c = token.Text[index];
                    var gap = c == '\t' ? space * 4 - (x + pending) % (space * 4) :
                        c == ' ' ? space : Measure(c.ToString());
                    if (x + pending > 0 && x + pending + gap > available)
                    { FlushWhite(index); NewLine(); if (c == '\t') gap = space * 4; }
                    pending += Math.Min(gap, available);
                }
                FlushWhite(token.Text.Length);
                continue;
            }
            // Never ask GDI to measure an arbitrarily long URL as one run; the
            // bounded grapheme chunks below handle it in viewport-sized pieces.
            var wordWidth = token.Text.Length <= 1024 ? Measure(token.Text) : available + 1;
            if (wordWidth <= available)
            {
                if (x > 0 && x + wordWidth > available) NewLine();
                Add(token.Text, null, wordWidth); continue;
            }
            if (x > 0) NewLine();
            // A long URL/word wraps only at grapheme boundaries. Binary search is
            // capped to 512 graphemes per piece so a huge unbroken payload cannot
            // repeatedly measure the entire remaining string on the UI thread.
            var boundaries = StringInfo.ParseCombiningCharacters(token.Text);
            var offset = 0;
            while (offset < boundaries.Length)
            {
                var low = 1; var high = Math.Min(512, boundaries.Length - offset); var fit = 0;
                string Slice(int count)
                {
                    var begin = boundaries[offset];
                    var end = offset + count == boundaries.Length ? token.Text.Length : boundaries[offset + count];
                    return token.Text[begin..end];
                }
                while (low <= high)
                {
                    var middle = (low + high) / 2;
                    if (Measure(Slice(middle)) <= available) { fit = middle; low = middle + 1; }
                    else high = middle - 1;
                }
                fit = Math.Max(1, fit);
                var piece = Slice(fit);
                // A single unsupported joined grapheme may be wider than the
                // viewport. Keep it intact and clip; never change its meaning.
                Add(piece, null, Math.Min(available, Measure(piece)));
                offset += fit;
                if (offset < boundaries.Length) NewLine();
            }
        }
        return new InlineEmojiTextLayout(text, runs.AsReadOnly(),
            new Size(Math.Min(available, Math.Max(1, maximumX)), y + height), emojiCount, lines, height);
    }

    internal void Draw(Graphics graphics, Font font, Color color, Point origin)
    {
        foreach (var run in Runs)
        {
            var bounds = run.Bounds; bounds.Offset(origin);
            if (!graphics.IsVisible(bounds)) continue;
            if (run.Emoji is { } emoji)
            {
                var side = Math.Min(bounds.Width, bounds.Height);
                EmojiPainter.Draw(graphics, new RectangleF(bounds.Left,
                    bounds.Top + (bounds.Height - side) / 2f, side, side), emoji, 0, animate: false);
            }
            else if (!string.IsNullOrWhiteSpace(run.Text))
                TextRenderer.DrawText(graphics, run.Text, font, bounds, color, TextFlags);
        }
    }
}

// Two widths cover MessageRow's maximum-width probe and its final bubble width.
// Painting reuses those runs, so scrolling/repainting never reparses a history.
internal sealed class InlineEmojiTextCache
{
    // Rich presentation is optional. Do not allocate/measure every grapheme of
    // a multi-megabyte decrypted message on the UI thread merely for one emoji.
    // Above this local budget the original native text path still displays the
    // complete string; sending, copying, stars and pins are never truncated.
    internal const int MaximumRichTextLength = 16 * 1024;

    // Most messages contain no emoji. SIMD screening skips the token allocations
    // entirely unless a possible catalog lead code unit occurs in the text.
    private static readonly SearchValues<char> EmojiStarters = SearchValues.Create(
        EmojiCatalog.All.Select(emoji => emoji.Symbol[0]).Distinct().ToArray());
    private string? _text;
    private IReadOnlyList<InlineEmojiTextLayout.Token> _tokens = [];
    private bool _hasEmoji;
    private readonly List<(Font Font, int Width, float DpiX, float DpiY, InlineEmojiTextLayout Layout)> _layouts = [];
    internal int BuildCountForQa { get; private set; }

    internal InlineEmojiTextLayout? Get(Graphics graphics, string text, Font font, int width)
    {
        if (!string.Equals(_text, text, StringComparison.Ordinal))
        {
            _text = text;
            _tokens = text.Length > MaximumRichTextLength ||
                text.AsSpan().IndexOfAny(EmojiStarters) < 0 || RequiresNativeDirectionality(text)
                ? [] : InlineEmojiTextLayout.Tokenize(text);
            _hasEmoji = _tokens.Any(token => token.Emoji is not null); _layouts.Clear();
        }
        if (!_hasEmoji) return null;
        width = Math.Max(1, width);
        var cached = _layouts.FirstOrDefault(item => ReferenceEquals(item.Font, font) && item.Width == width &&
            item.DpiX == graphics.DpiX && item.DpiY == graphics.DpiY);
        if (cached.Layout is not null) return cached.Layout;
        var layout = InlineEmojiTextLayout.Create(graphics, text, font, width, _tokens);
        BuildCountForQa++;
        if (_layouts.Count == 2) _layouts.RemoveAt(0);
        _layouts.Add((font, width, graphics.DpiX, graphics.DpiY, layout));
        return layout;
    }

    internal void Clear() { _text = null; _tokens = []; _hasEmoji = false; _layouts.Clear(); }

    private static bool RequiresNativeDirectionality(string text)
    {
        // The small inline layout is left-to-right. Keep full-paragraph native
        // BiDi shaping for RTL scripts/explicit directional controls rather than
        // reorder Arabic/Hebrew words merely to colour one emoji.
        foreach (var rune in text.EnumerateRunes())
        {
            var value = rune.Value;
            if (value is >= 0x0590 and <= 0x08ff or >= 0xfb1d and <= 0xfdff or
                >= 0xfe70 and <= 0xfeff or >= 0x10800 and <= 0x10fff or
                >= 0x1e800 and <= 0x1eeff or 0x200e or 0x200f or
                >= 0x202a and <= 0x202e or >= 0x2066 and <= 0x2069) return true;
        }
        return false;
    }
}
