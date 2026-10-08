using System.Globalization;
using System.Text;

namespace MTKChat.Desktop;

internal enum EmojiCategory { Faces, Love, Gestures, Celebration, Nature, Objects }

internal sealed record EmojiDefinition(string Symbol, string Name, string Keywords,
    EmojiCategory Category, bool Animated);

/// <summary>
/// The wire format remains ordinary Unicode text. This catalog is presentation-only:
/// an unsupported grapheme must never be split into a different supported emoji.
/// </summary>
internal static class EmojiCatalog
{
    internal static IReadOnlyList<EmojiDefinition> All { get; } = Array.AsReadOnly(new[]
    {
        new EmojiDefinition("😀", "Gülümseme", "mutlu neşe gül yüz", EmojiCategory.Faces, true),
        new EmojiDefinition("😃", "Kocaman gülümseme", "mutlu heyecan diş yüz", EmojiCategory.Faces, true),
        new EmojiDefinition("😄", "Gülen gözler", "gül mutlu neşe göz yüz", EmojiCategory.Faces, true),
        new EmojiDefinition("😁", "Dişlerini gösteren", "gül sırıt neşe yüz", EmojiCategory.Faces, true),
        new EmojiDefinition("😆", "Kahkaha", "gül komik eğlen yüz", EmojiCategory.Faces, true),
        new EmojiDefinition("😂", "Gözyaşlarıyla gülme", "kahkaha gözyaşı gül komik lol yüz", EmojiCategory.Faces, true),
        new EmojiDefinition("🤣", "Gülmekten kırılma", "kahkaha gül komik lol yüz", EmojiCategory.Faces, true),
        new EmojiDefinition("😊", "İçten gülümseme", "mutlu utangaç sıcak gül yüz", EmojiCategory.Faces, true),
        new EmojiDefinition("🙂", "Hafif gülümseme", "gül sakin mutlu yüz", EmojiCategory.Faces, true),
        // Agents often return the older BMP smiley. Its optional emoji selector
        // gets the same artwork without altering the original message string.
        new EmojiDefinition("☺️", "Sıcak gülümseme", "mutlu dost canlısı gül yüz", EmojiCategory.Faces, true),
        new EmojiDefinition("😉", "Göz kırpma", "şaka göz kırp yüz", EmojiCategory.Faces, true),
        new EmojiDefinition("😍", "Kalp gözler", "sevgi aşk beğen kalp yüz", EmojiCategory.Faces, true),
        new EmojiDefinition("🥰", "Sevgi dolu", "sevgi aşk sarıl kalp yüz", EmojiCategory.Faces, true),
        new EmojiDefinition("😘", "Öpücük", "öpücük sevgi aşk kalp yüz", EmojiCategory.Faces, true),
        new EmojiDefinition("😎", "Havalı", "güneş gözlük cool rahat yüz", EmojiCategory.Faces, true),
        new EmojiDefinition("🤩", "Yıldız gözler", "hayran heyecan yıldız yüz", EmojiCategory.Faces, true),
        new EmojiDefinition("🥳", "Parti yüzü", "kutlama doğum günü eğlence yüz", EmojiCategory.Faces, true),
        new EmojiDefinition("🤔", "Düşünceli", "düşün soru merak yüz", EmojiCategory.Faces, true),
        new EmojiDefinition("😮", "Şaşkın", "şaşır hayret sürpriz yüz", EmojiCategory.Faces, true),
        new EmojiDefinition("😲", "Çok şaşkın", "şaşır şok sürpriz yüz", EmojiCategory.Faces, true),
        new EmojiDefinition("😴", "Uykulu", "uyu uyku dinlen yorgun yüz", EmojiCategory.Faces, true),
        new EmojiDefinition("😢", "Üzgün", "üzgün ağla gözyaşı yüz", EmojiCategory.Faces, true),
        new EmojiDefinition("😭", "Ağlayan", "üzgün ağla gözyaşı hüzün yüz", EmojiCategory.Faces, true),
        new EmojiDefinition("😅", "Terli gülümseme", "ter rahat gül mahcup yüz", EmojiCategory.Faces, true),
        new EmojiDefinition("😬", "Gergin", "gergin mahcup diş yüz", EmojiCategory.Faces, true),
        new EmojiDefinition("😡", "Kızgın", "kızgın öfke sinir yüz", EmojiCategory.Faces, true),
        new EmojiDefinition("🤗", "Kucaklama", "sarıl kucak sevgi yüz", EmojiCategory.Faces, true),
        new EmojiDefinition("🤫", "Sessiz", "sus sessiz sır yüz", EmojiCategory.Faces, true),
        new EmojiDefinition("😋", "Lezzetli", "dil yemek lezzet yüz", EmojiCategory.Faces, true),
        new EmojiDefinition("😜", "Şakacı", "dil şaka göz kırp yüz", EmojiCategory.Faces, true),
        new EmojiDefinition("🙃", "Ters gülümseme", "ters şaka ironi yüz", EmojiCategory.Faces, true),
        new EmojiDefinition("❤️", "Kırmızı kalp", "aşk sevgi kalp beğen", EmojiCategory.Love, true),
        new EmojiDefinition("💜", "Mor kalp", "aşk sevgi kalp mor", EmojiCategory.Love, true),
        new EmojiDefinition("💙", "Mavi kalp", "aşk sevgi kalp mavi", EmojiCategory.Love, true),
        new EmojiDefinition("💚", "Yeşil kalp", "aşk sevgi kalp yeşil", EmojiCategory.Love, true),
        new EmojiDefinition("💛", "Sarı kalp", "aşk sevgi kalp sarı", EmojiCategory.Love, true),
        new EmojiDefinition("🧡", "Turuncu kalp", "aşk sevgi kalp turuncu", EmojiCategory.Love, true),
        new EmojiDefinition("🩷", "Pembe kalp", "aşk sevgi kalp pembe", EmojiCategory.Love, true),
        new EmojiDefinition("💕", "İki kalp", "aşk sevgi kalp birlikte", EmojiCategory.Love, true),
        new EmojiDefinition("💖", "Parıldayan kalp", "aşk sevgi kalp yıldız ışıltı", EmojiCategory.Love, true),
        new EmojiDefinition("💔", "Kırık kalp", "üzgün ayrılık kalp kırık", EmojiCategory.Love, false),
        new EmojiDefinition("👍", "Beğeni", "tamam onay güzel başparmak el", EmojiCategory.Gestures, true),
        new EmojiDefinition("👎", "Beğenmeme", "hayır olumsuz başparmak el", EmojiCategory.Gestures, false),
        new EmojiDefinition("👋", "El sallama", "merhaba selam hoşça kal el", EmojiCategory.Gestures, true),
        new EmojiDefinition("👏", "Alkış", "tebrik bravo el alkış", EmojiCategory.Gestures, true),
        new EmojiDefinition("🙏", "Teşekkür", "teşekkür lütfen dua el", EmojiCategory.Gestures, true),
        new EmojiDefinition("✌️", "Zafer", "barış zafer iki el", EmojiCategory.Gestures, false),
        new EmojiDefinition("👌", "Tamam", "onay tamam mükemmel el", EmojiCategory.Gestures, false),
        new EmojiDefinition("🤝", "Tokalaşma", "anlaşma teşekkür dost el", EmojiCategory.Gestures, true),
        new EmojiDefinition("🎉", "Konfeti", "kutlama parti tebrik doğum günü", EmojiCategory.Celebration, true),
        new EmojiDefinition("🎊", "Kutlama topu", "kutlama parti tebrik konfeti", EmojiCategory.Celebration, true),
        new EmojiDefinition("🎂", "Doğum günü pastası", "kutlama doğum günü pasta mum", EmojiCategory.Celebration, true),
        new EmojiDefinition("🎁", "Hediye", "kutlama hediye sürpriz doğum günü", EmojiCategory.Celebration, false),
        new EmojiDefinition("🎈", "Balon", "kutlama parti doğum günü balon", EmojiCategory.Celebration, true),
        new EmojiDefinition("🏆", "Kupa", "başarı tebrik kupa ödül şampiyon", EmojiCategory.Celebration, false),
        new EmojiDefinition("✨", "Işıltı", "yıldız ışık parıltı sihir", EmojiCategory.Nature, true),
        new EmojiDefinition("⭐", "Yıldız", "yıldız favori ışık", EmojiCategory.Nature, true),
        new EmojiDefinition("🔥", "Ateş", "ateş sıcak harika enerji", EmojiCategory.Nature, true),
        new EmojiDefinition("🌈", "Gökkuşağı", "renk gökyüzü umut doğa", EmojiCategory.Nature, false),
        new EmojiDefinition("☀️", "Güneş", "güneş hava günaydın sıcak doğa", EmojiCategory.Nature, true),
        new EmojiDefinition("🌙", "Ay", "ay gece iyi geceler doğa", EmojiCategory.Nature, false),
        new EmojiDefinition("🌸", "Çiçek", "çiçek bahar doğa pembe", EmojiCategory.Nature, false),
        new EmojiDefinition("🌿", "Yaprak", "yaprak yeşil doğa bitki", EmojiCategory.Nature, false),
        new EmojiDefinition("☕", "Kahve", "kahve çay içecek mola", EmojiCategory.Objects, true),
        new EmojiDefinition("💡", "Fikir", "fikir ampul ışık çözüm", EmojiCategory.Objects, true),
        new EmojiDefinition("🚀", "Roket", "roket uzay hız başarı", EmojiCategory.Objects, true),
        new EmojiDefinition("💬", "Sohbet", "sohbet konuş mesaj", EmojiCategory.Objects, true),
        new EmojiDefinition("🎵", "Müzik", "müzik şarkı nota ses", EmojiCategory.Objects, false),
        new EmojiDefinition("✅", "Onay", "onay tamam doğru tik", EmojiCategory.Objects, false),
        new EmojiDefinition("📎", "Ataş", "ataş ek dosya", EmojiCategory.Objects, false),
        new EmojiDefinition("💻", "Bilgisayar", "bilgisayar kod iş laptop", EmojiCategory.Objects, false)
    });

    private static readonly IReadOnlyDictionary<string, EmojiDefinition> BySymbol =
        All.ToDictionary(e => Canonical(e.Symbol), StringComparer.Ordinal);

    private static readonly IReadOnlyList<(EmojiDefinition Emoji, string Text)> SearchIndex =
        All.Select(e => (e, Fold(e.Name + " " + e.Keywords))).ToArray();

    internal static bool TryGet(string symbol, out EmojiDefinition emoji)
    {
        // Only VS16 is presentation-optional. ZWJ, tags, modifiers and VS15 are meaningful.
        if (symbol is not null && BySymbol.TryGetValue(Canonical(symbol), out var found))
        {
            emoji = found;
            return true;
        }
        emoji = null!;
        return false;
    }

    internal static IReadOnlyList<EmojiDefinition>? ParseEmojiOnly(string text, int maxCount = 3)
    {
        // Emoji-only enlargement has a tiny input budget; long normal messages never
        // need a grapheme walk or an allocation proportional to their payload.
        if (string.IsNullOrEmpty(text) || text.Length > 128 || maxCount < 1 ||
            string.IsNullOrWhiteSpace(text)) return null;
        var result = new List<EmojiDefinition>(Math.Min(maxCount, 3));
        var elements = StringInfo.GetTextElementEnumerator(text);
        while (elements.MoveNext())
        {
            var element = elements.GetTextElement();
            if (string.IsNullOrWhiteSpace(element)) continue;
            if (result.Count >= maxCount || !TryGet(element, out var definition)) return null;
            result.Add(definition);
        }
        return result.Count == 0 ? null : result.AsReadOnly();
    }

    internal static IReadOnlyList<EmojiDefinition> Search(string query, EmojiCategory? category = null)
    {
        // Normalize rejects invalid UTF-16. A pasted isolated surrogate must yield no
        // matches, not escape the popup's TextChanged handler as an exception.
        if (query is not null && (query.Length > 256 || !ValidUtf16(query)))
            return Array.Empty<EmojiDefinition>();
        var terms = Fold(query ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return SearchIndex.Where(entry => (!category.HasValue || entry.Emoji.Category == category) &&
            terms.All(term => entry.Text.Contains(term, StringComparison.Ordinal) ||
                              Canonical(entry.Emoji.Symbol).Contains(Canonical(term), StringComparison.Ordinal)))
            .Select(entry => entry.Emoji).ToArray();
    }

    private static string Canonical(string symbol) => symbol.EndsWith("\uFE0F", StringComparison.Ordinal)
        ? symbol[..^1] : symbol;

    private static bool ValidUtf16(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (!char.IsSurrogate(text[i])) continue;
            if (!char.IsHighSurrogate(text[i]) || i + 1 == text.Length || !char.IsLowSurrogate(text[++i]))
                return false;
        }
        return true;
    }

    private static string Fold(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            builder.Append(c == 'ı' ? 'i' : char.ToLowerInvariant(c));
        }
        return builder.ToString();
    }
}
