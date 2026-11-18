using System.Globalization;
using System.Text.Json;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

[Collection("Localization")]
public sealed class CountAndSizeTextTests : IDisposable
{
    public CountAndSizeTextTests() => Reset();
    public void Dispose() => Reset();

    private static void Reset()
    {
        Texts.Initialize();
        Texts.SetLanguage("en");
    }

    [Theory]
    [InlineData("en", 0, "0 captured posts")]
    [InlineData("en", 1, "1 captured post")]
    [InlineData("en", 2, "2 captured posts")]
    [InlineData("de", 1, "1 erfasster Post")]
    [InlineData("de", 3, "3 erfasste Posts")]
    [InlineData("es", 1, "1 publicación capturada")]
    [InlineData("es", 0, "0 publicaciones capturadas")]
    [InlineData("fr", 0, "0 message capturé")]
    [InlineData("fr", 1, "1 message capturé")]
    [InlineData("fr", 2, "2 messages capturés")]
    public void CountPicksTheSingularOnlyWhereTheLanguageUsesIt(string language, long count, string expected)
    {
        Texts.SetLanguage(language);

        Assert.Equal(expected, Texts.Count("archive.postsCount", count));
    }

    [Theory]
    [InlineData(1, "пост")]
    [InlineData(2, "поста")]
    [InlineData(4, "поста")]
    [InlineData(5, "постов")]
    [InlineData(11, "постов")]
    [InlineData(12, "постов")]
    [InlineData(21, "пост")]
    [InlineData(22, "поста")]
    [InlineData(25, "постов")]
    [InlineData(101, "пост")]
    [InlineData(111, "постов")]
    public void RussianUsesOneFewAndManyForms(long count, string noun)
    {
        Texts.SetLanguage("ru");

        Assert.Equal("Последний захват: 5.11.2026 · " + count + " " + noun,
            Texts.Count("archive.captured", count, "5.11.2026", count));
    }

    // The limit texts name the counted things after the number, so Russian needs its own form for 1, 2-4 and the rest.
    [Theory]
    [InlineData("limit.favoriteEntries", 1, "запись,")]
    [InlineData("limit.favoriteEntries", 3, "записи,")]
    [InlineData("limit.favoriteEntries", 5, "записей,")]
    [InlineData("limit.postBookmarks", 22, "закладки постов")]
    [InlineData("limit.postBookmarks", 11, "закладок постов")]
    [InlineData("limit.postBookmarks", 21, "закладку постов")]
    [InlineData("limit.savedSearches", 2, "сохранённых поиска")]
    [InlineData("limit.savedSearches", 1, "сохранённый поиск")]
    [InlineData("limit.savedSearches", 12, "сохранённых поисков")]
    [InlineData("limit.readerGroups", 4, "группы для чтения")]
    [InlineData("limit.readerGroups", 1, "группу для чтения")]
    [InlineData("limit.readerGroups", 14, "групп для чтения")]
    public void RussianLimitTextsAgreeWithTheLimit(string key, long limit, string expected)
    {
        Texts.SetLanguage("ru");

        Assert.Contains(limit + " " + expected, Texts.Count(key, limit, limit, "A", "B"));
    }

    [Fact]
    public void CountPhrasesFormatEveryCounterIndependently()
    {
        Assert.Equal("Last capture: today · 1 post", Texts.Count("archive.captured", 1, "today", 1));
        Assert.Equal("1 medium in 12 threads", CountPhrases.MediaSummary(1, 12));
        Assert.Equal("12 media in 1 thread", CountPhrases.MediaSummary(12, 1));
        Assert.Equal("12 media in 3 threads", CountPhrases.MediaSummary(12, 3));
        Assert.Equal("12 media in 1 thread, 2 missing", CountPhrases.MediaSummary(12, 1, 2));
        Texts.SetLanguage("de");
        Assert.Equal("12 Medien in 1 Thread", CountPhrases.MediaSummary(12, 1));
        Assert.Equal("1 Medium in 12 Threads", CountPhrases.MediaSummary(1, 12));
    }

    [Fact]
    public void CountWithoutAnySingularFormUsesTheBaseKeyAndUnknownKeysShowTheKey()
    {
        Assert.Equal("3 results", Texts.Count("downloads.searchCount", 3));
        Assert.Equal("1 result", Texts.Count("downloads.searchCount", 1));
        Assert.Equal("1 Treffer", WithLanguage("de", () => Texts.Count("downloads.searchCount", 1)));
        Assert.Equal("no.such.key", Texts.Count("no.such.key", 1));
    }

    [Fact]
    public void PackWithoutSingularFormFallsBackToItsOwnBaseText()
    {
        using TempDirectory directory = new TempDirectory();
        WritePack(directory, "xx", new Dictionary<string, string> { ["archive.postsCount"] = "{0} xx-posts" });
        Texts.InitializeWithoutBundlesForTests(directory.Path);
        Texts.SetLanguage("xx");

        Assert.Equal("1 xx-posts", Texts.Count("archive.postsCount", 1));
        // A key the pack lacks entirely shows the English text, with English number rules.
        Assert.Equal("1 result", Texts.Count("downloads.searchCount", 1));
    }

    [Fact]
    public void PackMayAddPluralVariantsOfEnglishKeysButTheyNeedMatchingPlaceholders()
    {
        using TempDirectory directory = new TempDirectory();
        WritePack(directory, "ru", new Dictionary<string, string>
        {
            ["archive.postsCount"] = "{0} постов",
            ["archive.postsCount.one"] = "{0} пост",
            ["archive.postsCount.few"] = "{0} поста"
        });
        Texts.InitializeWithoutBundlesForTests(directory.Path);
        Texts.SetLanguage("ru");

        Assert.Empty(Texts.Warnings);
        Assert.Equal(new[] { "21 пост", "23 поста", "25 постов" },
            new[] { 21L, 23L, 25L }.Select(count => Texts.Count("archive.postsCount", count)));

        using TempDirectory bad = new TempDirectory();
        WritePack(bad, "ru", new Dictionary<string, string>
        {
            ["archive.postsCount"] = "{0} постов",
            ["archive.postsCount.few"] = "{0} {1} поста"
        });
        Texts.SetLanguage("en");
        Texts.InitializeWithoutBundlesForTests(bad.Path);

        Assert.Contains("ru.json", Assert.Single(Texts.Warnings));
    }

    [Fact]
    public void EverySingularKeyBelongsToABaseKeyWithTheSamePlaceholdersInEnglishAndGerman()
    {
        foreach (IReadOnlyDictionary<string, string> catalog in new[] { BuiltInCatalog.English, BuiltInCatalog.German })
        {
            string[] ones = catalog.Keys.Where(key => key.EndsWith(".one", StringComparison.Ordinal)).ToArray();
            Assert.NotEmpty(ones);
            foreach (string key in ones)
            {
                string baseKey = key[..^4];
                Assert.True(catalog.ContainsKey(baseKey), "No base text for " + key);
                Assert.Equal(Placeholders(catalog[baseKey]), Placeholders(catalog[key]));
            }
        }
        Assert.Empty(Texts.ValidateBuiltInCatalogs());
    }

    [Theory]
    [InlineData("en", 1, "one")] [InlineData("en", 0, "other")] [InlineData("en", 2, "other")]
    [InlineData("de-AT", 1, "one")] [InlineData("de", 5, "other")]
    [InlineData("es", 1, "one")] [InlineData("es", 0, "other")]
    [InlineData("fr", 0, "one")] [InlineData("fr", 1, "one")] [InlineData("fr", 2, "other")]
    [InlineData("ru", 1, "one")] [InlineData("ru", 3, "few")] [InlineData("ru", 0, "many")] [InlineData("ru", 12, "many")] [InlineData("ru", 22, "few")]
    [InlineData("ru", -1, "one")] [InlineData("ru", long.MinValue, "many")]
    [InlineData("pl", 5, "many")] [InlineData("pl", long.MinValue, "many")]
    [InlineData("cs", 3, "few")] [InlineData("cs", long.MinValue, "other")] [InlineData("ja", 1, "other")]
    // CLDR cardinal examples (integers). zh-Hans/ja: no plural forms, every count is "other".
    [InlineData("zh-Hans", 0, "other")] [InlineData("zh-Hans", 1, "other")] [InlineData("zh-Hans", 2, "other")] [InlineData("zh-Hans", 100, "other")]
    [InlineData("zh", 1, "other")] [InlineData("ja", 0, "other")] [InlineData("ja", 2, "other")] [InlineData("ja", 1000, "other")]
    // pt (Brazil): one = 0 and 1; pt-PT: one = 1 only.
    [InlineData("pt-BR", 0, "one")] [InlineData("pt-BR", 1, "one")] [InlineData("pt-BR", 2, "other")] [InlineData("pt-BR", 11, "other")] [InlineData("pt-BR", 100, "other")]
    [InlineData("pt", 0, "one")] [InlineData("pt", 1, "one")] [InlineData("pt-PT", 0, "other")] [InlineData("pt-PT", 1, "one")] [InlineData("pt-PT", 2, "other")]
    // cs: one = 1, few = 2-4, other = 0 and 5+ ("many" is for fractions only and never produced for whole counts).
    [InlineData("cs", 0, "other")] [InlineData("cs", 1, "one")] [InlineData("cs", 2, "few")] [InlineData("cs", 4, "few")] [InlineData("cs", 5, "other")]
    [InlineData("cs", 11, "other")] [InlineData("cs", 12, "other")] [InlineData("cs", 22, "other")] [InlineData("cs", 100, "other")] [InlineData("cs-CZ", 3, "few")] [InlineData("cs", -2, "few")]
    // it: one = 1; ko: no forms; pl: one = 1, few = 2-4 except 12-14, many = 0, 5+ and 12-14 (the pack's base key).
    [InlineData("it", 0, "other")] [InlineData("it", 1, "one")] [InlineData("it", 2, "other")] [InlineData("it-IT", 1, "one")]
    [InlineData("ko", 0, "other")] [InlineData("ko", 1, "other")] [InlineData("ko", 2, "other")] [InlineData("ko-KR", 1, "other")]
    [InlineData("pl", 0, "many")] [InlineData("pl", 1, "one")] [InlineData("pl", 2, "few")] [InlineData("pl", 4, "few")]
    [InlineData("pl", 12, "many")] [InlineData("pl", 14, "many")] [InlineData("pl", 21, "many")] [InlineData("pl", 22, "few")] [InlineData("pl", 112, "many")] [InlineData("pl", 122, "few")] [InlineData("pl-PL", 3, "few")]
    public void PluralCategories(string language, long count, string expected) =>
        Assert.Equal(expected, PluralRules.Category(language, count));

    [Theory]
    [InlineData("zh-Hans")] [InlineData("ja")] [InlineData("ko")] [InlineData("cs")] [InlineData("pt-BR")] [InlineData("it")]
    public void OnlyEastSlavicAndPolishProduceManyForWholeCounts(string language) =>
        Assert.DoesNotContain("many", Enumerable.Range(0, 2000).Select(count => PluralRules.Category(language, count)));

    [Fact]
    public void CountedTextWithoutVariantsUsesTheBaseKeyForEveryNumberInChinese()
    {
        Texts.SetLanguage("zh-Hans");
        try
        {
            // zh-Hans has no .one keys; 1 and 2 both use the base text, formatted in the zh-Hans culture.
            string one = Texts.Count("archive.postsCount", 1);
            string two = Texts.Count("archive.postsCount", 2);
            Assert.NotEqual(BuiltInCatalog.English["archive.postsCount.one"].Replace("{0}", "1"), one);
            Assert.Equal(one.Replace("1", "2"), two);
            Assert.Contains("1", one);
        }
        finally { Texts.SetLanguage("en"); }
    }

    [Theory]
    [InlineData(-5L, "0 B")]
    [InlineData(0L, "0 B")]
    [InlineData(1L, "1 B")]
    [InlineData(999L, "999 B")]
    [InlineData(1_000L, "1000 B")]
    [InlineData(1_023L, "1023 B")]
    [InlineData(1_024L, "1 KiB")]
    [InlineData(1_536L, "1.5 KiB")]
    [InlineData(10_239L, "10 KiB")]
    [InlineData(10_240L, "10 KiB")]
    [InlineData(45_000L, "44 KiB")]
    [InlineData(1_048_063L, "1023 KiB")]
    [InlineData(1_048_064L, "1 MiB")]
    [InlineData(1_048_576L, "1 MiB")]
    [InlineData(1_572_864L, "1.5 MiB")]
    [InlineData(5_800_000L, "5.5 MiB")]
    [InlineData(1_072_693_248L, "1023 MiB")]
    [InlineData(1_073_689_600L, "1 GiB")]
    [InlineData(1_073_741_824L, "1 GiB")]
    [InlineData(2_684_354_560L, "2.5 GiB")]
    [InlineData(1_099_511_627_776L, "1 TiB")]
    [InlineData(long.MaxValue, "8388608 TiB")]
    public void SizeTextUsesBinaryUnitsAndNeverRoundsASmallFileToZeroMebibytes(long bytes, string expected) =>
        Assert.Equal(expected, SizeText.Format(bytes, CultureInfo.InvariantCulture));

    [Fact]
    public void SizeTextFollowsTheSelectedLanguageButNeverTranslatesTheUnits()
    {
        Texts.SetLanguage("de");
        Assert.Equal("1,5 KiB", SizeText.Format(1_536));
        Assert.Equal("1,5 MiB", SizeText.Format(1_572_864));
        Assert.Equal("1 MiB", SizeText.Format(1_048_576));
        Assert.Equal("2,5 GiB", SizeText.Format(2_684_354_560));
        Assert.Equal("1023 B", SizeText.Format(1023));
        Texts.SetLanguage("en");
        Assert.Equal("1.5 KiB", SizeText.Format(1_536));
        Assert.Equal("1.5 MiB", SizeText.Format(1_572_864));
        Assert.Equal("1 MiB", SizeText.Format(1_048_576));
        Assert.Equal("2.5 GiB", SizeText.Format(2_684_354_560));
        Assert.Equal("1,5 KiB", SizeText.Format(1_536, CultureInfo.GetCultureInfo("de-DE")));
        Assert.Equal("1.5 KiB", SizeText.Format(1_536, CultureInfo.GetCultureInfo("en-US")));
    }

    private static string WithLanguage(string language, Func<string> action)
    {
        Texts.SetLanguage(language);
        try { return action(); }
        finally { Texts.SetLanguage("en"); }
    }

    private static HashSet<string> Placeholders(string format) =>
        System.Text.RegularExpressions.Regex.Matches(format, @"\{\d+[^}]*\}").Select(match => match.Value).ToHashSet(StringComparer.Ordinal);

    private static void WritePack(TempDirectory directory, string code, Dictionary<string, string> strings) =>
        File.WriteAllText(Path.Combine(directory.Path, code + ".json"), JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["schemaVersion"] = 1, ["code"] = code, ["nativeName"] = code, ["strings"] = strings
        }));
}
