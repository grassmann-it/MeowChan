using MeowChan.Desktop.Localization;
using MeowChan.Persistence;
using MeowChan.Tags;

namespace MeowChan.DesktopTests;

/// <summary>
/// A bookmark note (8000) and an own tag name (128) are limited in Unicode code points, not in what an editor shows as
/// characters; the texts that name the two limits say so in every language, with the numbers the code enforces.
/// </summary>
[Collection("Localization")]
public sealed class CodepointLengthLocalizationTests : IDisposable
{
    // Load the bundled packs too: an earlier test of the collection may have left only the built-in languages.
    public CodepointLengthLocalizationTests()
    {
        Texts.Initialize();
        Texts.SetLanguage("en");
    }

    public void Dispose()
    {
        Texts.Initialize();
        Texts.SetLanguage("en");
    }

    private static readonly (string Language, string CodePoints)[] Languages =
    {
        ("en", "code points"), ("de", "Codepoints"), ("es", "puntos de código"), ("fr", "points de code"), ("ru", "кодовые точки"),
    };

    [Fact]
    public void TheNoteAndTagNameLimitsAreNamedAsCodePointsWithTheEnforcedNumbers()
    {
        Assert.Equal(8000, PostBookmarkStore.MaximumNoteCodepoints);
        Assert.Equal(128, PrivateTagName.MaximumScalars);
        // 128 scalars of at most 4 UTF-8 bytes fit the byte bound, so the code point count is the only one a user meets.
        Assert.True(PrivateTagName.MaximumScalars * 4 <= PrivateTagName.MaximumUtf8Bytes);
        string previous = Texts.Language;
        try
        {
            foreach ((string language, string codePoints) in Languages)
            {
                Texts.SetLanguage(language);
                string note = Texts.T("budgets.postBookmarksHint");
                string name = Texts.T("tags.error.invalidName");
                Assert.Contains("8000", note);
                Assert.Contains(codePoints, note);
                Assert.Contains("128", name);
                Assert.Contains(codePoints, name);
            }
        }
        finally { Texts.SetLanguage(previous); }
    }

    [Fact]
    public void ANameOf128EmojiIsAcceptedAndOneMoreCodePointIsRefused()
    {
        string emoji = char.ConvertFromUtf32(0x1F431);
        Assert.True(PrivateTagName.TryCreate(string.Concat(Enumerable.Repeat(emoji, 128)), out _, out _));
        Assert.False(PrivateTagName.TryCreate(string.Concat(Enumerable.Repeat(emoji, 128)) + "a", out _, out _));
    }
}
