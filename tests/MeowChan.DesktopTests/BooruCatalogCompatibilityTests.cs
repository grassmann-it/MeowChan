using System.Text.RegularExpressions;
using MeowChan.Desktop.Booru;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

/// <summary>
/// Checks of the window texts that need no shared wiring: they read <see cref="BooruUiCatalog"/> directly, so they
/// hold before and after it is concatenated into <c>BuiltInCatalog</c>. The tests that go through
/// <c>BuiltInCatalog</c>, the bundled packs or the command catalog are in <see cref="BooruLocalizationTests"/> and
/// wake up once that wiring exists.
/// </summary>
public sealed class BooruCatalogCompatibilityTests
{
    private static readonly Regex Placeholder = new(@"\{\d+\}", RegexOptions.Compiled);

    private static string[] Placeholders(string text) => Placeholder.Matches(text).Select(match => match.Value).Order().ToArray();

    [Fact]
    public void WindowKeysAreUniqueAndExactlyTheOnesTheUiRequests()
    {
        string[] keys = BooruUiCatalog.Entries.Select(entry => entry.Key).ToArray();
        Assert.Equal(keys.Length, keys.Distinct(StringComparer.Ordinal).Count());
        string[] required = BooruText.RequiredKeys().ToArray();
        Assert.Equal(required.Length, required.Distinct(StringComparer.Ordinal).Count());
        string[] statusKeys = BooruStatusCatalog.Entries.Select(entry => entry.Key).ToArray();
        Assert.Empty(required.Except(keys).Except(statusKeys));
        // booru.ui.command.open is requested by the command catalog, not by a window text.
        Assert.Empty(keys.Except(required).Where(key => key != "booru.ui.command.open"));
    }

    [Fact]
    public void WindowKeysNeverCollideWithTheStatusCatalogSoBothCanBeConcatenated()
    {
        // BuiltInCatalog builds its dictionaries with ToDictionary: one shared key would throw at type initialisation.
        HashSet<string> status = BooruStatusCatalog.Entries.Select(entry => entry.Key).ToHashSet(StringComparer.Ordinal);
        string[] collisions = BooruUiCatalog.Entries.Select(entry => entry.Key).Where(status.Contains).ToArray();
        Assert.True(collisions.Length == 0, "Keys in both catalogs: " + string.Join(", ", collisions));
        Assert.All(BooruUiCatalog.Entries, entry => Assert.StartsWith("booru.ui.", entry.Key, StringComparison.Ordinal));
    }

    [Fact]
    public void EnglishAndGermanAreFilledAndShareTheirPlaceholders()
    {
        foreach ((string key, string english, string german) in BooruUiCatalog.Entries)
        {
            Assert.False(string.IsNullOrWhiteSpace(english), key);
            Assert.False(string.IsNullOrWhiteSpace(german), key);
            Assert.Equal(Placeholders(english), Placeholders(german));
        }
    }

    [Fact]
    public void NoWindowTextIntroducesAnAgeGateOrAHiddenRatingFilter()
    {
        Regex forbidden = new(@"\b18\b|adult|age verif|Altersfreigabe|Altersbest|18\+", RegexOptions.IgnoreCase);
        foreach ((string key, string english, string german) in BooruUiCatalog.Entries)
        {
            Assert.DoesNotMatch(forbidden, english);
            Assert.DoesNotMatch(forbidden, german);
        }
        string english2 = BooruUiCatalog.Entries.Single(entry => entry.Key == "booru.ui.noFilterNote").English;
        Assert.Contains("no rating filter", english2);
    }
}
