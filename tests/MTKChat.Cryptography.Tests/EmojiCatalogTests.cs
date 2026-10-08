using MTKChat.Desktop;
using Xunit;

namespace MTKChat.Cryptography.Tests;

public sealed class EmojiCatalogTests
{
    [Fact]
    public void CatalogHasSeventyOneUniqueNamedEmojiAndAllSixCategories()
    {
        Assert.Equal(71, EmojiCatalog.All.Count);
        Assert.Equal(71, EmojiCatalog.All.Select(e => e.Symbol).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(6, EmojiCatalog.All.Select(e => e.Category).Distinct().Count());
        Assert.True(EmojiCatalog.All.Count(e => e.Animated) >= 12);
        Assert.All(EmojiCatalog.All, emoji =>
        {
            Assert.False(string.IsNullOrWhiteSpace(emoji.Name));
            Assert.False(string.IsNullOrWhiteSpace(emoji.Keywords));
            Assert.True(EmojiCatalog.TryGet(emoji.Symbol, out var found));
            Assert.Same(emoji, found);
            Assert.Same(emoji, Assert.Single(EmojiCatalog.ParseEmojiOnly(emoji.Symbol)!));
            Assert.Contains(emoji, EmojiCatalog.Search(emoji.Name));
        });
    }

    [Theory]
    [InlineData("😊")]
    [InlineData("👍")]
    [InlineData("❤️")]
    public void ExistingChoicesRemainSupported(string symbol)
    {
        Assert.True(EmojiCatalog.TryGet(symbol, out var emoji));
        Assert.Equal(symbol, emoji.Symbol);
    }

    [Theory]
    [InlineData("❤", "❤️")]
    [InlineData("❤️", "❤️")]
    [InlineData("✌", "✌️")]
    [InlineData("✌️", "✌️")]
    [InlineData("⭐️", "⭐")]
    [InlineData("☀", "☀️")]
    [InlineData("☺", "☺️")]
    [InlineData("☺️", "☺️")]
    public void SingleOptionalTerminalVs16UsesCanonicalDefinition(string variant, string canonical)
    {
        Assert.True(EmojiCatalog.TryGet(variant, out var emoji));
        Assert.Equal(canonical, emoji.Symbol);
        Assert.Equal(canonical, Assert.Single(EmojiCatalog.ParseEmojiOnly(variant)!).Symbol);
    }

    [Theory]
    [InlineData("😊 👍 ❤️", 3)]
    [InlineData("\t😊\r\n👍 \u2003❤️ ", 3)]
    [InlineData("😊", 1)]
    [InlineData("😊👍", 2)]
    [InlineData("😊😊😊", 3)]
    public void EmojiOnlyParserConsumesAllGraphemesAndHarmlessWhitespace(string text, int count)
    {
        Assert.Equal(count, EmojiCatalog.ParseEmojiOnly(text)!.Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("😊😊😊😊")]
    [InlineData("merhaba 😊")]
    [InlineData("😊!")]
    [InlineData("😊 metin 👍")]
    [InlineData("👍🏽")]
    [InlineData("👍🏻")]
    [InlineData("😊‍😊")]
    [InlineData("👩‍💻")]
    [InlineData("🏳️‍🌈")]
    [InlineData("🇹🇷")]
    [InlineData("😊🇹🇷")]
    [InlineData("😊\u200B")]
    [InlineData("\u200B😊")]
    [InlineData("😊\u200D")]
    [InlineData("\uFE0F😊")]
    [InlineData("❤️️")]
    [InlineData("❤︎")]
    [InlineData("☺︎")]
    [InlineData("1️⃣")]
    [InlineData("😊\u0000")]
    [InlineData("😊\u202E")]
    public void MixedOrUnsupportedSequencesNeverBecomePartialAnimatedEmoji(string text)
    {
        Assert.Null(EmojiCatalog.ParseEmojiOnly(text));
    }

    [Fact]
    public void ParserHonorsCountArgumentAndRejectsLongInputWithoutRecursion()
    {
        Assert.Null(EmojiCatalog.ParseEmojiOnly("😊👍", 1));
        Assert.Null(EmojiCatalog.ParseEmojiOnly("😊", 0));
        Assert.Null(EmojiCatalog.ParseEmojiOnly("😊", -1));
        Assert.Null(EmojiCatalog.ParseEmojiOnly(new string(' ', 1_000_000) + "😊"));
        Assert.Null(EmojiCatalog.ParseEmojiOnly(string.Concat(Enumerable.Repeat("😊", 100_000)), int.MaxValue));
        Assert.Equal(4, EmojiCatalog.ParseEmojiOnly("😊👍❤️😉", 4)!.Count);
    }

    [Theory]
    [InlineData("👍🏽")]
    [InlineData("😊‍😊")]
    [InlineData("🇹🇷")]
    [InlineData("\uFE0F❤")]
    [InlineData("❤️️")]
    public void LookupDoesNotStripModifiersJoinersFlagsOrRepeatedVariationSelectors(string symbol) =>
        Assert.False(EmojiCatalog.TryGet(symbol, out _));

    [Fact]
    public void SearchFoldsTurkishCaseAndAccentsAndAppliesAllTermsAndCategory()
    {
        Assert.Equal(EmojiCatalog.Search("kalp"), EmojiCatalog.Search("KALP"));
        Assert.Equal(EmojiCatalog.Search("gül"), EmojiCatalog.Search("GUL"));
        Assert.Equal(EmojiCatalog.Search("yüz"), EmojiCatalog.Search("yuz"));
        Assert.Equal(EmojiCatalog.Search("ışıltı"), EmojiCatalog.Search("ISILTI"));
        Assert.Contains(EmojiCatalog.All.Single(e => e.Symbol == "😂"), EmojiCatalog.Search("kahkaha gözyaşı"));
        Assert.Equal("💜", Assert.Single(EmojiCatalog.Search("mor kalp", EmojiCategory.Love)).Symbol);
        Assert.Empty(EmojiCatalog.Search("mor kalp", EmojiCategory.Gestures));
        Assert.Equal("❤️", Assert.Single(EmojiCatalog.Search("❤")).Symbol);
        Assert.All(EmojiCatalog.Search("", EmojiCategory.Objects), e => Assert.Equal(EmojiCategory.Objects, e.Category));
    }

    [Fact]
    public void SearchHandlesInvalidUtf16AndOversizedInputWithoutThrowing()
    {
        // Test both malformed code units inside one Fact: xUnit's theory ID
        // serialization replaces them with the same U+FFFD and drops one case.
        foreach (var invalid in new[] { "\uD83D", "\uDE0A" })
        {
            Assert.Null(EmojiCatalog.ParseEmojiOnly(invalid));
            Assert.False(EmojiCatalog.TryGet(invalid, out _));
        }
        Assert.Empty(EmojiCatalog.Search("\uD83D"));
        Assert.Empty(EmojiCatalog.Search("\uDE0A"));
        Assert.Empty(EmojiCatalog.Search("kalp\uD83D"));
        Assert.Empty(EmojiCatalog.Search(new string('x', 1_000_000)));
        Assert.Empty(EmojiCatalog.Search("\u200B"));
        Assert.Empty(EmojiCatalog.Search("\u200D"));
        Assert.Equal(EmojiCatalog.All.Count, EmojiCatalog.Search(null!).Count);
        Assert.False(EmojiCatalog.TryGet(null!, out _));
        Assert.Null(EmojiCatalog.ParseEmojiOnly(null!));
    }
}
