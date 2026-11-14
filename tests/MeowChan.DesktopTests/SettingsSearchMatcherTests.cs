using System.Diagnostics;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

/// <summary>The matcher behind the settings search: ranking, typos, abbreviations, accents, synonyms, English fallback, highlights.</summary>
[Collection("Localization")]
public sealed class SettingsSearchMatcherTests : IDisposable
{
    public SettingsSearchMatcherTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage("en"); }

    private static SettingsSearchDocument Doc(string id, string label, string hint, string group, string? key, string category = "settings.downloads",
        params string[] english) =>
        new(id, label, hint, group, key, SettingsSearchAliases.For(key, category), english);

    private static SettingsSearchIndex English() => SettingsSearchMatcher.Build(new[]
    {
        Doc("theme", "Colour scheme", "Light, dark or follow the system.", "Interface", "Theme", "settings.interface"),
        Doc("language", "Language", "The language of the interface.", "Interface", "Language", "settings.interface"),
        Doc("download", "Download folder", "Where new files are saved.", "Downloads", "DownloadPath"),
        Doc("bandwidth", "Bandwidth limit (MiB/s)", "Zero does not mean unlimited.", "Downloads", "Bandwidth"),
        Doc("parallel", "Parallel downloads", "How many files load at once.", "Downloads", "ParallelDownloads"),
        Doc("reserve", "Free-space reserve (GiB)", "Downloads pause below this.", "Storage and limits", "Reserve", "settings.worker-budgets"),
        Doc("poster", "Poster cache size (MiB)", "Thumbnails of videos are kept here.", "Storage and limits", "PosterCacheLimitMiB", "settings.worker-budgets"),
        Doc("thumb", "Thumbnail cache (tiles)", "Tiles kept in memory.", "Storage and limits", "ThumbnailCacheTiles", "settings.worker-budgets"),
        Doc("clipurls", "Clipboard: URLs checked per read", "At most this many URLs are checked.", "Storage and limits", "ClipboardUrlLimit", "settings.worker-budgets"),
        Doc("monitor", "Monitor the clipboard for links", "Offers to add copied links.", "Behavior", "MonitorClipboardUrls", "settings.behavior"),
        Doc("stats", "Header graph", "What the graph in the title bar shows.", "Behavior › Statistics", "StatsHeaderGraph", "settings.behavior"),
        Doc("tray", "Close to the system tray", "The window hides instead of quitting.", "Behavior", "CloseToTray", "settings.behavior"),
        Doc("size", "Reading font size", "Text size in the reader.", "Reading and media", "ReadingFontSize", "settings.reading-media"),
    });

    private static string? Top(SettingsSearchIndex index, string query) => SettingsSearchMatcher.Search(index, query).FirstOrDefault()?.Document.Id;
    private static string[] Ids(SettingsSearchIndex index, string query) => SettingsSearchMatcher.Search(index, query).Select(hit => hit.Document.Id).ToArray();

    [Theory]
    [InlineData("bandwidth", "bandwidth")]
    [InlineData("bandwith", "bandwidth")]       // a missing letter
    [InlineData("bandwdith", "bandwidth")]      // two letters swapped
    [InlineData("dwnld", "download")]           // letters in order
    [InlineData("dowload", "download")]         // a typo in the start of a longer word
    [InlineData("paralel", "parallel")]
    [InlineData("langauge", "language")]
    [InlineData("tray", "tray")]
    public void TyposAndAbbreviationsFindTheSetting(string query, string expected) => Assert.Equal(expected, Top(English(), query));

    [Fact]
    public void WordStartsAndWordsInAnyOrderMatch()
    {
        SettingsSearchIndex index = English();
        Assert.Contains("clipurls", Ids(index, "clip"));
        Assert.Contains("monitor", Ids(index, "clip"));
        Assert.Equal("clipurls", Top(index, "urls clipboard"));
        Assert.Equal("clipurls", Top(index, "clipboard urls"));
    }

    [Fact]
    public void EveryQueryWordHasToMatchAndNonsenseFindsNothing()
    {
        SettingsSearchIndex index = English();
        Assert.Empty(Ids(index, "zzzzqq"));
        Assert.Empty(Ids(index, "theme banana"));
        Assert.Empty(Ids(index, "   "));
        Assert.Empty(Ids(index, null!));
        Assert.Empty(Ids(index, "x"));
    }

    [Fact]
    public void LabelMatchesRankAboveHintMatchesAndWholeWordsAboveStarts()
    {
        SettingsSearchIndex index = English();
        string[] ids = Ids(index, "download");
        // "Download folder" and "Parallel downloads" have it in the label; the reserve and the group only in a hint.
        Assert.Equal("download", ids[0]);
        Assert.True(Array.IndexOf(ids, "parallel") < Array.IndexOf(ids, "reserve"));
        Assert.Equal("language", Top(index, "language"));
    }

    [Fact]
    public void RankingFollowsTheWholePhraseInTheLabel()
    {
        SettingsSearchIndex index = English();
        Assert.Equal("size", Top(index, "font size"));
        Assert.Equal("poster", Top(index, "poster cache"));
    }

    [Fact]
    public void SynonymsFindTheSettingTheLabelDoesNotName()
    {
        SettingsSearchIndex index = English();
        Assert.Equal("theme", Top(index, "dark mode"));
        Assert.Equal("theme", Top(index, "dunkel"));
        Assert.Equal("download", Top(index, "ordner"));
        Assert.Equal("bandwidth", Top(index, "speed"));
        Assert.Equal("tray", Top(index, "taskbar"));
        // "cache" and "speicher" reach every size and cache setting, "limit" every limit.
        foreach (string word in new[] { "cache", "speicher", "zwischenspeicher" })
        {
            string[] ids = Ids(index, word);
            Assert.Contains("poster", ids);
            Assert.Contains("thumb", ids);
        }
        Assert.Contains("clipurls", Ids(index, "limit"));
        Assert.Contains("poster", Ids(index, "limit"));
    }

    [Fact]
    public void KeysFindSettingsInEveryWritingOfTheName()
    {
        SettingsSearchIndex index = English();
        foreach (string query in new[] { "ClipboardUrlLimit", "clipboardurllimit", "clipboard_url_limit", "clipboard-url-limit", "ClipboardUrlLimitInput".Replace("Input", "") })
            Assert.Equal("clipurls", Top(index, query));
        Assert.Equal("poster", Top(index, "PosterCacheLimitMiB"));
    }

    [Fact]
    public void AccentsAndCaseNeverMatter()
    {
        var index = SettingsSearchMatcher.Build(new[]
        {
            Doc("a", "Schriftgröße", "Die Größe des Textes.", "Lesen", "ReadingFontSize"),
            Doc("b", "Café-Modus", "Für Müde Augen.", "Oberfläche", null),
            Doc("c", "Łódź żółć", "", "Polski", null),
        });
        Assert.Equal("a", Top(index, "grosse"));
        Assert.Equal("a", Top(index, "GRÖSSE"));
        Assert.Equal("a", Top(index, "schriftgrosse"));
        Assert.Equal("b", Top(index, "cafe"));
        Assert.Equal("b", Top(index, "CAFÉ"));
        Assert.Equal("b", Top(index, "muede".Replace("ue", "u")));
    }

    [Fact]
    public void FoldRemovesAccentsAndMapsBackToTheShownText()
    {
        Assert.Equal("grosse", SettingsSearchMatcher.Fold("Größe"));
        Assert.Equal("cafe", SettingsSearchMatcher.Fold("Café"));
        Assert.Equal("lodz zolc", SettingsSearchMatcher.Fold("Łódź żółć"));
        Assert.Equal("ae oe", SettingsSearchMatcher.Fold("Æ Œ"));
        Assert.Equal("clipboard url limit clipboardurllimit", SettingsSearchMatcher.FoldKey("ClipboardUrlLimit"));
        Assert.Equal("download 2 limit download2limit", SettingsSearchMatcher.FoldKey("Download2Limit"));
        var map = new List<int>();
        Assert.Equal("grosse", SettingsSearchMatcher.Fold("Größe", map));
        Assert.Equal(new[] { 0, 1, 2, 3, 3, 4 }, map);
        // "ß" becomes two letters, so a highlight of "ss" covers the one shown letter.
        var hit = SettingsSearchMatcher.Search(SettingsSearchMatcher.Build(new[] { Doc("a", "Größe", "", "G", null) }), "gross").Single();
        Assert.Equal(new[] { new SettingsTextRange(0, 4) }, hit.LabelRanges);
    }

    [Fact]
    public void TheLabelHighlightCoversTheShownLettersOfEveryMatch()
    {
        SettingsSearchIndex index = English();
        SettingsSearchHit prefix = SettingsSearchMatcher.Search(index, "band").Single(hit => hit.Document.Id == "bandwidth");
        Assert.Equal(new[] { new SettingsTextRange(0, 4) }, prefix.LabelRanges);
        SettingsSearchHit typo = SettingsSearchMatcher.Search(index, "bandwith").First();
        Assert.Equal(new[] { new SettingsTextRange(0, 9) }, typo.LabelRanges);
        // Letters in order: "dwnld" lights the five letters of "Downloads" it stands for.
        SettingsSearchHit abbreviated = SettingsSearchMatcher.Search(index, "dwnld").First(hit => hit.Document.Id == "download");
        Assert.Equal(new[] { new SettingsTextRange(0, 1), new SettingsTextRange(2, 3), new SettingsTextRange(7, 1) }, abbreviated.LabelRanges);
        // Two query words, two ranges; hint hits are marked in the hint.
        SettingsSearchHit two = SettingsSearchMatcher.Search(index, "poster size").First();
        Assert.Equal("poster", two.Document.Id);
        Assert.Equal(new[] { new SettingsTextRange(0, 6), new SettingsTextRange(13, 4) }, two.LabelRanges);
        SettingsSearchHit inHint = SettingsSearchMatcher.Search(index, "quitting").Single();
        Assert.Empty(inHint.LabelRanges);
        Assert.Equal(new[] { new SettingsTextRange(28, 8) }, inHint.HintRanges);
    }

    [Fact]
    public void GermanTextsAndTheEnglishOriginalsBothFindASetting()
    {
        var index = SettingsSearchMatcher.Build(new[]
        {
            Doc("bandwidth", "Bandbreitenlimit (MiB/s)", "Null bedeutet nicht unbegrenzt.", "Downloads", "Bandwidth", "settings.downloads", "Bandwidth limit (MiB/s)", "Zero does not mean unlimited."),
            Doc("theme", "Farbschema", "Hell, dunkel oder wie das System.", "Oberfläche", "Theme", "settings.interface", "Colour scheme", "Light, dark or follow the system."),
            Doc("poster", "Poster-Cache (MiB)", "Vorschaubilder von Videos.", "Speicher und Grenzen", "PosterCacheLimitMiB", "settings.worker-budgets", "Poster cache (MiB)", "Thumbnails of videos."),
        });
        Assert.Equal("bandwidth", Top(index, "bandbreite"));
        Assert.Equal("bandwidth", Top(index, "bandbreit"));
        Assert.Equal("bandwidth", Top(index, "bandbrite"));
        Assert.Equal("bandwidth", Top(index, "bandwidth"));   // English fallback in a German interface
        Assert.Equal("bandwidth", Top(index, "bandwith"));
        Assert.Equal("theme", Top(index, "colour"));
        Assert.Equal("theme", Top(index, "dark mode"));
        Assert.Equal("theme", Top(index, "farbscheme"));
        Assert.Equal("poster", Top(index, "speicher"));
        Assert.Equal("poster", Top(index, "zwischenspeicher"));
        Assert.Equal("poster", Top(index, "vorschau"));
        Assert.Equal("poster", Top(index, "thumbnails"));
        Assert.Equal("poster", Top(index, "grenzen poster"));
    }

    [Fact]
    public void EnglishOriginalsComeFromTheCatalogOfTheActiveLanguage()
    {
        Texts.SetLanguage("de");
        Assert.Contains("Workspace view", Texts.EnglishEquivalents(Texts.T("settings.viewMode")));
        Assert.Empty(Texts.EnglishEquivalents("Text that is no catalog value"));
        Texts.SetLanguage("en");
        Assert.Empty(Texts.EnglishEquivalents(Texts.T("settings.viewMode")));
    }

    [Theory]
    [InlineData("bandwidth", "bandwidth", 100)]
    [InlineData("band", "bandwidth", 80)]
    [InlineData("width", "bandwidth", 55)]
    public void WordScoresOrderWholeStartInside(string token, string word, double minimum)
    {
        double score = SettingsSearchMatcher.ScoreWord(token, word, true, out _, out _, out _);
        Assert.InRange(score, minimum, minimum + 15);
    }

    [Fact]
    public void FuzzyMatchingStaysOutOfShortWordsAndHints()
    {
        Assert.Equal(0, SettingsSearchMatcher.ScoreWord("ls", "last", true, out _, out _, out _)); // too short for anything but a start
        Assert.Equal(0, SettingsSearchMatcher.ScoreWord("cast", "last", true, out _, out _, out _)); // first letter differs
        Assert.Equal(0, SettingsSearchMatcher.ScoreWord("dwnld", "downloads", false, out _, out _, out _)); // not in hints
        Assert.Equal(0, SettingsSearchMatcher.ScoreWord("dns", "downloads", true, out _, out _, out _));   // far too sparse
        Assert.True(SettingsSearchMatcher.ScoreWord("下载", "下载文件夹", true, out _, out _, out _) > 0);   // CJK: a part of the text
        Assert.Equal(1, SettingsSearchMatcher.Distance("ab", "ba", 2));
        Assert.Equal(3, SettingsSearchMatcher.Distance("abc", "xyz", 5));
        Assert.Equal(2, SettingsSearchMatcher.Distance("abcdef", "abc", 1)); // limit + 1 once it is beyond the limit
    }

    [Fact]
    public void ChineseAndJapaneseTextIsFoundByAPartOfIt()
    {
        var index = SettingsSearchMatcher.Build(new[]
        {
            Doc("a", "下载文件夹", "新文件保存的位置。", "下载", "DownloadPath"),
            Doc("b", "ダウンロード帯域", "", "ダウンロード", null),
        });
        Assert.Equal("a", Top(index, "文件夹"));
        Assert.Equal("b", Top(index, "帯域"));
    }

    [Fact]
    public void TheOrderOfEqualHitsIsThePageOrder()
    {
        var index = SettingsSearchMatcher.Build(new[]
        {
            Doc("first", "Limit one", "", "G", null), Doc("second", "Limit two", "", "G", null), Doc("third", "Limit three", "", "G", null),
        });
        Assert.Equal(new[] { "first", "second", "third" }, Ids(index, "limit"));
    }

    [Fact]
    public void ASearchStaysFarBelowAKeystrokeBudgetOverEveryKindOfQuery()
    {
        var docs = new List<SettingsSearchDocument>();
        for (int i = 0; i < 300; i++)
            docs.Add(Doc("d" + i, "Setting number " + i + " cache limit bandwidth", "A longer explanation of what the setting does and when to change it, number " + i,
                "Group " + i % 7, "SomeLongSettingKey" + i, "settings.worker-budgets", "English text " + i, "English hint " + i));
        SettingsSearchIndex index = SettingsSearchMatcher.Build(docs);
        string[] queries = { "c", "ca", "cach", "cache", "bandwith", "dwnld", "zzzz", "setting numbr 12", "speicher limit", "somelongsettingkey17", "explanaton" };
        foreach (string query in queries) SettingsSearchMatcher.Search(index, query); // warm up
        var watch = Stopwatch.StartNew();
        const int rounds = 5;
        for (int round = 0; round < rounds; round++)
            foreach (string query in queries) SettingsSearchMatcher.Search(index, query);
        double perQuery = watch.Elapsed.TotalMilliseconds / (rounds * queries.Length);
        // The page has about 100 entries; with three times that, a query still has to fit a keystroke (5 ms), with slack for a busy machine.
        Assert.True(perQuery < 15, "average " + perQuery.ToString("0.0") + " ms per query over 300 entries");
    }

    [Fact]
    public void TheSearchCommandIsPrimaryFInTheSettingsAndCollidesWithNothing()
    {
        CommandDefinition command = CommandCatalog.Get("input.focus-settings-search");
        Assert.Equal("Primary+F", command.Shortcut);
        Assert.Equal(CommandScope.Settings, command.Scope);
        Assert.Equal(["settings"], command.ExecutionContexts);
        foreach (CommandDefinition other in CommandCatalog.All.Where(c => c.Id != command.Id && c.Shortcut == "Primary+F"))
            Assert.False(EffectiveShortcutBindings.ContextsOverlap(command.Id, other.Id), other.Id);
        Assert.Empty(EffectiveShortcutBindings.Build(platform: ShortcutPlatform.Linux).Issues);
        Assert.Empty(EffectiveShortcutBindings.Build(platform: ShortcutPlatform.MacOS).Issues);
        Assert.Contains("input.focus-settings-search", EffectiveShortcutBindings.YieldingDefaultCommandIds);
    }

    [Fact]
    public void TheSearchTextsExistInEveryLanguage()
    {
        string[] keys = { "settings.search.placeholder", "settings.search.results", "settings.search.keys", "settings.search.none",
            "settings.search.noneHint", "command.input.focus-settings-search" };
        foreach (string code in new[] { "en", "de", "es", "fr", "ru", "zh-Hans", "ja", "ko", "pt-BR", "cs", "it", "pl" })
        {
            Texts.SetLanguage(code);
            Assert.Equal(code, Texts.Language);
            foreach (string key in keys) Assert.NotEqual(key, Texts.T(key, "x"));
            Assert.Contains("x", Texts.T("settings.search.none", "x"));
        }
    }
}
