using System.Text.Json;
using System.Text.RegularExpressions;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

/// <summary>The header tagline: which form fits, how the quip is picked (deterministically), the pool per language, providers and the settings.</summary>
[Collection("Localization")]
public sealed class TaglineTests : IDisposable
{
    private static readonly string[] AllLanguages = { "en", "de", "es", "fr", "ru", "zh-Hans", "ja", "ko", "pt-BR", "cs", "it", "pl" };
    private static readonly DateOnly Day = new(2026, 11, 14);

    public TaglineTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage("en"); }

    private static TaglineEntry Quip(string id, string since = "1.0.0", string? text = null, TaglineTone tone = TaglineTone.Playful) =>
        new(id, Version.Parse(since), tone, text ?? "text " + id);

    private static TaglineEntry[] Pool(params (string Id, string Since)[] entries) => entries.Select(e => Quip(e.Id, e.Since)).ToArray();

    // ---- fit -------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheWholeSentenceIsShownOnlyWhenItFitsWithTheTolerance()
    {
        Assert.Equal(HeaderTaglineStage.Full, HeaderTaglineFit.Choose(400, 330, 160));
        Assert.Equal(HeaderTaglineStage.Full, HeaderTaglineFit.Choose(331, 330, 160));
        Assert.Equal(HeaderTaglineStage.Quip, HeaderTaglineFit.Choose(330.5, 330, 160));
        Assert.Equal(HeaderTaglineStage.Quip, HeaderTaglineFit.Choose(330, 330, 160));
        Assert.Equal(HeaderTaglineStage.Quip, HeaderTaglineFit.Choose(161, 330, 160));
        Assert.Equal(HeaderTaglineStage.None, HeaderTaglineFit.Choose(160.5, 330, 160));
        Assert.Equal(HeaderTaglineStage.None, HeaderTaglineFit.Choose(160, 330, 160));
        Assert.Equal(HeaderTaglineStage.None, HeaderTaglineFit.Choose(40, 330, 160));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void AnUnusableColumnShowsNothing(double available) =>
        Assert.Equal(HeaderTaglineStage.None, HeaderTaglineFit.Choose(available, 10, 5));

    [Theory]
    [InlineData(0, 0)]
    [InlineData(double.NaN, double.NaN)]
    [InlineData(double.PositiveInfinity, double.PositiveInfinity)]
    public void AnUnmeasuredFormNeverCountsAsFitting(double full, double quip) =>
        Assert.Equal(HeaderTaglineStage.None, HeaderTaglineFit.Choose(500, full, quip));

    [Fact]
    public void AnUnmeasuredWholeSentenceStillAllowsTheShortForm() =>
        Assert.Equal(HeaderTaglineStage.Quip, HeaderTaglineFit.Choose(500, double.NaN, 100));

    // ---- picker ----------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheDayHashIsAStableFnv1aOfTheDate()
    {
        // Independent FNV-1a 32 values (not string.GetHashCode, which differs per process).
        Assert.Equal(1880875346u, TaglinePicker.DayHash(new DateOnly(2026, 11, 14)));
        Assert.Equal(1897652965u, TaglinePicker.DayHash(new DateOnly(2026, 11, 15)));
        Assert.Equal(2629323111u, TaglinePicker.DayHash(new DateOnly(2000, 1, 1)));
    }

    [Fact]
    public void TheDailyRuleIsTheSameAllDayAndIgnoresTheOrderOfThePool()
    {
        TaglineEntry[] pool = Pool(("a", "1.0.0"), ("b", "1.0.0"), ("c", "1.0.0"), ("d", "1.0.0"), ("e", "1.0.0"), ("f", "1.0.0"),
            ("g", "1.0.0"), ("h", "1.0.0"), ("i", "1.0.0"), ("j", "1.0.0"), ("k", "1.0.0"), ("l", "1.0.0"));
        TaglinePick first = TaglinePicker.Pick("Daily", "a", pool, Day, null, null, false, new Random(1));
        TaglinePick again = TaglinePicker.Pick("Daily", "a", pool.AsEnumerable().Reverse().ToArray(), Day, null, null, false, new Random(99));
        Assert.Equal("c", first.Entry!.Id); // 1880875346 % 12 == 2 over the id-sorted pool
        Assert.Equal(first.Entry.Id, again.Entry!.Id);
        Assert.False(first.ReleaseHighlight);
        Assert.Equal("b", TaglinePicker.Pick("Daily", "a", pool, new DateOnly(2026, 11, 15), null, null, false, new Random(1)).Entry!.Id);
    }

    [Fact]
    public void ThePerStartRuleUsesTheInjectedRandomAndOnlyThePool()
    {
        TaglineEntry[] pool = Pool(("a", "1.0.0"), ("b", "1.0.0"), ("c", "1.0.0"));
        Random seeded = new(7);
        Random same = new(7);
        for (int i = 0; i < 20; i++)
            Assert.Equal(TaglinePicker.Pick("PerStart", "a", pool, Day, null, null, false, seeded).Entry!.Id,
                TaglinePicker.Pick("PerStart", "a", pool, Day, null, null, false, same).Entry!.Id);
        Assert.Equal(3, Enumerable.Range(0, 200).Select(n => TaglinePicker.Pick("PerStart", "a", pool, Day, null, null, false, new Random(n)).Entry!.Id).Distinct().Count());
    }

    [Fact]
    public void OffShowsNothingAndFixedShowsTheStoredOneWithFallbacks()
    {
        TaglineEntry[] pool = Pool(("booru", "1.1.0"), ("cats", "1.0.0"), ("plain", "1.0.0"));
        Assert.Null(TaglinePicker.Pick("Off", "cats", pool, Day, new Version(1, 1, 0), null, true, new Random(1)).Entry);
        Assert.Null(TaglinePicker.Pick("PerStart", "cats", [], Day, null, null, true, new Random(1)).Entry);
        Assert.Equal("plain", TaglinePicker.Pick("Fixed", "plain", pool, Day, null, null, false, new Random(1)).Entry!.Id);
        Assert.Equal("cats", TaglinePicker.Pick("Fixed", "gone", pool, Day, null, null, false, new Random(1)).Entry!.Id);
        Assert.Equal("booru", TaglinePicker.Pick("Fixed", "gone", Pool(("booru", "1.1.0"), ("plain", "1.0.0")), Day, null, null, false, new Random(1)).Entry!.Id);
    }

    [Fact]
    public void FixedIsNeverOverriddenByTheReleaseHighlight()
    {
        TaglineEntry[] pool = Pool(("cats", "1.0.0"), ("booru", "1.1.0"));
        TaglinePick pick = TaglinePicker.Pick("Fixed", "cats", pool, Day, new Version(1, 1, 0), new Version(1, 0, 0), true, new Random(1));
        Assert.Equal("cats", pick.Entry!.Id);
        Assert.False(pick.ReleaseHighlight);
    }

    [Fact]
    public void TheFirstStartAfterAnUpdateShowsTheReleaseQuipOnce()
    {
        TaglineEntry[] pool = Pool(("cats", "1.0.0"), ("plain", "1.0.0"), ("booru", "1.1.0"), ("flashbang", "1.1.0"), ("future", "1.2.0"));
        Version current = new(1, 1, 0);
        foreach (string mode in new[] { "PerStart", "Daily" })
        {
            TaglinePick first = TaglinePicker.Pick(mode, "cats", pool, Day, current, new Version(1, 0, 0), true, new Random(3));
            Assert.True(first.ReleaseHighlight);
            Assert.Contains(first.Entry!.Id, new[] { "booru", "flashbang" });
            // Once the version counts as seen, the normal rule applies; a pick that is not the start never highlights.
            Assert.False(TaglinePicker.Pick(mode, "cats", pool, Day, current, current, true, new Random(3)).ReleaseHighlight);
            Assert.False(TaglinePicker.Pick(mode, "cats", pool, Day, current, new Version(1, 0, 0), false, new Random(3)).ReleaseHighlight);
        }
        // A quip of a version newer than the running one is never "the release".
        Assert.DoesNotContain(TaglinePicker.ReleaseCandidates(pool, current, new Version(1, 0, 0)), entry => entry.Id == "future");
    }

    [Fact]
    public void AFreshProfileGetsTheNewestReleaseQuipAndAnUnreadableVersionNone()
    {
        TaglineEntry[] pool = Pool(("cats", "1.0.0"), ("booru", "1.1.0"));
        Assert.Equal(["booru"], TaglinePicker.ReleaseCandidates(pool, new Version(1, 1, 0), null).Select(e => e.Id).ToArray());
        Assert.Empty(TaglinePicker.ReleaseCandidates(pool, null, null));
        // Skipping a version shows the newest of the skipped releases' quips only.
        Assert.Equal(["booru"], TaglinePicker.ReleaseCandidates(Pool(("a", "1.0.0"), ("x", "1.1.0"), ("booru", "1.2.0")), new Version(1, 2, 0), new Version(1, 0, 0)).Select(e => e.Id).ToArray());
        // A release without quips of its own leaves the normal rule.
        Assert.Empty(TaglinePicker.ReleaseCandidates(pool, new Version(1, 1, 1), new Version(1, 1, 0)));
    }

    [Fact]
    public void AnotherQuipIsADifferentOneWhenThereIsOne()
    {
        TaglineEntry[] pool = Pool(("a", "1.0.0"), ("b", "1.0.0"), ("c", "1.0.0"));
        for (int seed = 0; seed < 50; seed++)
            Assert.NotEqual("b", TaglinePicker.PickAnother(pool, "b", new Random(seed))!.Id);
        Assert.Equal("a", TaglinePicker.PickAnother(Pool(("a", "1.0.0")), "a", new Random(1))!.Id);
        Assert.Null(TaglinePicker.PickAnother([], "a", new Random(1)));
    }

    [Theory]
    [InlineData("Off", "Off")]
    [InlineData("fixed", "Fixed")]
    [InlineData(" DAILY ", "Daily")]
    [InlineData("perstart", "PerStart")]
    [InlineData("", "PerStart")]
    [InlineData("Sometimes", "PerStart")]
    [InlineData(null, "PerStart")]
    public void AModeReadsCaseInsensitivelyAndAnythingElseIsTheDefault(string? value, string expected) =>
        Assert.Equal(expected, TaglinePicker.NormalizeMode(value));

    // ---- catalog, providers, versions -----------------------------------------------------------------------------------------------

    [Fact]
    public void VersionsReadWithoutSuffixesAndOtherTextIsNoVersion()
    {
        Assert.Equal(new Version(1, 1, 0), TaglineCatalog.ParseVersion("1.1.0"));
        Assert.Equal(new Version(1, 1, 0), TaglineCatalog.ParseVersion("1.1.0+abc123"));
        Assert.Equal(new Version(1, 1, 0), TaglineCatalog.ParseVersion("1.1.0-beta.2"));
        Assert.Equal(new Version(1, 1, 0), TaglineCatalog.ParseVersion("v1.1"));
        Assert.Null(TaglineCatalog.ParseVersion("banana"));
        Assert.Null(TaglineCatalog.ParseVersion(""));
        Assert.Null(TaglineCatalog.ParseVersion(null));
        Assert.Equal("1.1.0", TaglineCatalog.NormalizeVersion("1.1.0+x"));
        Assert.Equal("", TaglineCatalog.NormalizeVersion("junk"));
    }

    [Theory]
    [InlineData("cats", "cats")]
    [InlineData(" booru ", "booru")]
    [InlineData("theme.mila/night-1", "theme.mila/night-1")]
    [InlineData("", "cats")]
    [InlineData("has space", "cats")]
    [InlineData("caf\u00e9", "cats")]
    public void AnIdIsKeptWhenWellFormedAndOtherwiseTheClassicOne(string value, string expected) =>
        Assert.Equal(expected, TaglineCatalog.NormalizeId(value));

    [Fact]
    public void AnOverlongIdIsNotAnId() =>
        Assert.Equal("cats", TaglineCatalog.NormalizeId(new string('a', TaglineCatalog.MaximumIdLength + 1)));

    private sealed class FixedProvider(string id, params TaglineEntry[] entries) : ITaglineProvider
    {
        public string ProviderId => id;
        public IReadOnlyList<TaglineEntry> Entries() => entries;
    }

    private sealed class ThrowingProvider : ITaglineProvider
    {
        public string ProviderId => "throws";
        public IReadOnlyList<TaglineEntry> Entries() => throw new InvalidOperationException("broken theme");
    }

    [Fact]
    public void AnExtraProviderAddsItsFortunesAndAnUnregisteredOneRemovesThem()
    {
        FixedProvider theme = new("theme", Quip("theme.mila/night", "1.2.0", "purrs in the dark"));
        int before = TaglineCatalog.Available().Count;
        using (TaglineCatalog.Register(theme))
        {
            IReadOnlyList<TaglineEntry> pool = TaglineCatalog.Available();
            Assert.Equal(before + 1, pool.Count);
            Assert.Contains(pool, entry => entry.Id == "theme.mila/night" && entry.Text == "purrs in the dark");
        }
        Assert.Equal(before, TaglineCatalog.Available().Count);
    }

    [Fact]
    public void ABrokenOrCarelessProviderIsSkippedAndTheFirstProviderWinsAnIdClash()
    {
        ITaglineProvider good = new FixedProvider("good", Quip("a", "1.0.0", "first"), Quip("b", "1.0.0", "  padded  "));
        ITaglineProvider clash = new FixedProvider("clash", Quip("a", "1.0.0", "second"), Quip("bad id", "1.0.0", "x"),
            Quip("empty", "1.0.0", "   "), Quip("long", "1.0.0", new string('x', TaglineCatalog.MaximumTextLength + 1)),
            Quip("ctl", "1.0.0", "two\nlines"));
        IReadOnlyList<TaglineEntry> pool = TaglineCatalog.Available([new ThrowingProvider(), good, clash]);
        Assert.Equal(["a", "b"], pool.Select(entry => entry.Id).ToArray());
        Assert.Equal("first", pool[0].Text);
        Assert.Equal("padded", pool[1].Text);
    }

    [Fact]
    public void ThePoolOfALanguageHoldsOnlyWhatItsPackTranslated()
    {
        Dictionary<string, string> translated = new() { ["shell.tagline.quip.cats"] = "Katzen", ["shell.tagline.quip.plain"] = "schlicht" };
        BuiltInTaglineProvider provider = new(key => translated.GetValueOrDefault(key));
        Assert.Equal(["cats", "plain"], TaglineCatalog.Available([provider]).Select(entry => entry.Id).ToArray());
    }

    // ---- texts in all twelve languages ---------------------------------------------------------------------------------------------

    [Fact]
    public void EveryLanguageHasTheLeadAndEveryQuipAndTheLeadPlusTheCatQuipIsTheAboutSentence()
    {
        foreach (string language in AllLanguages)
        {
            Texts.SetLanguage(language);
            Assert.Equal(Texts.T("shell.subtitle"), Texts.T("shell.tagline.lead") + " — " + Texts.T("shell.tagline.quip.cats"));
            IReadOnlyList<TaglineEntry> pool = TaglineCatalog.Available();
            Assert.Equal(BuiltInTaglineProvider.Ids.Order(StringComparer.Ordinal).ToArray(), pool.Select(entry => entry.Id).ToArray());
            Assert.Equal(pool.Count, pool.Select(entry => entry.Text).Distinct(StringComparer.Ordinal).Count());
            foreach (TaglineEntry entry in pool)
            {
                Assert.True(entry.Text.Length is > 3 and <= TaglineCatalog.MaximumTextLength, language + " " + entry.Id);
                Assert.DoesNotContain("YChan", entry.Text);
            }
        }
    }

    [Fact]
    public void AllTwelveLanguagesTranslateTheQuipsFreelyNotLeftInEnglish()
    {
        Texts.SetLanguage("en");
        Dictionary<string, string> english = TaglineCatalog.Available().ToDictionary(entry => entry.Id, entry => entry.Text);
        foreach (string language in AllLanguages.Where(code => code != "en"))
        {
            Texts.SetLanguage(language);
            // Tokens like "booru", "KiB" or "404" may stay; a text identical to the English one may not (cats and the others all change).
            foreach (TaglineEntry entry in TaglineCatalog.Available())
                Assert.NotEqual(english[entry.Id], entry.Text);
        }
    }

    [Fact]
    public void TheCatalogShipsBetweenEightAndFifteenQuipsAndTheReleaseOnesAreThere()
    {
        Texts.SetLanguage("en");
        IReadOnlyList<TaglineEntry> pool = TaglineCatalog.Available();
        Assert.InRange(pool.Count, 8, 15);
        Assert.Contains(pool, entry => entry.Id == TaglineCatalog.DefaultId);
        Assert.Equal("now with 100% more cats", pool.Single(entry => entry.Id == "cats").Text);
        Assert.Equal("now with booru support", pool.Single(entry => entry.Id == "booru").Text);
        Assert.Equal("light mode now less flashbang", pool.Single(entry => entry.Id == "flashbang").Text);
        Assert.Contains(pool, entry => entry.Tone == TaglineTone.Plain);
        Assert.NotEmpty(TaglinePicker.ReleaseCandidates(pool, new Version(1, 1, 0), null));
    }

    [Fact]
    public void ThePackKeysAreExactlyTheCatalogKeysAndNoPackHasAStrayTaglineKey()
    {
        HashSet<string> expected = BuiltInTaglineProvider.Ids.Select(BuiltInTaglineProvider.KeyFor).ToHashSet(StringComparer.Ordinal);
        foreach (string language in Texts.BundledPackCodes())
        {
            using Stream stream = typeof(Texts).Assembly.GetManifestResourceStream(Texts.BundledPackResourcePrefix + language + ".json")!;
            using JsonDocument document = JsonDocument.Parse(stream);
            HashSet<string> keys = document.RootElement.GetProperty("strings").EnumerateObject()
                .Select(property => property.Name).Where(name => name.StartsWith("shell.tagline.quip.", StringComparison.Ordinal)).ToHashSet(StringComparer.Ordinal);
            Assert.Equal(expected.Order(StringComparer.Ordinal).ToArray(), keys.Order(StringComparer.Ordinal).ToArray());
        }
    }

    [Fact]
    public void TheSettingAndMenuTextsExistInEveryLanguage()
    {
        string[] keys = { "shell.tagline.menuAnother", "shell.tagline.menuHide", "settings.headerTagline.label", "settings.headerTagline.hint",
            "settings.headerTagline.off", "settings.headerTagline.fixed", "settings.headerTagline.daily", "settings.headerTagline.perStart",
            "settings.headerTagline.fixedLabel", "settings.headerTagline.fixedHint" };
        foreach (string language in AllLanguages)
        {
            Texts.SetLanguage(language);
            foreach (string key in keys)
            {
                string? text = Texts.Own(key);
                Assert.False(string.IsNullOrWhiteSpace(text), language + " " + key);
                Assert.NotEqual(key, text);
            }
        }
    }

    // ---- configuration -------------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheDefaultsAreANewQuipAtEveryStartTheClassicOneAndNoSeenVersion()
    {
        Config config = new();
        Assert.Equal("PerStart", config.HeaderTagline);
        Assert.Equal("cats", config.HeaderTaglineFixedId);
        Assert.Equal("", config.LastTaglineVersion);
    }

    [Fact]
    public void TheValuesSurviveCloneBothCopiesAndSaveReloadAndTheSettingsCopyKeepsTheSeenVersion()
    {
        using TempDirectory directory = new();
        Config original = new() { HeaderTagline = "Fixed", HeaderTaglineFixedId = "booru", LastTaglineVersion = "1.1.0" };
        Config clone = original.Clone();
        Config live = new() { LastTaglineVersion = "1.0.5" };
        live.CopySettingsFieldsFrom(clone);
        // CopySettingsFieldsFrom carries what Settings edits; the seen version is the window's own view preference.
        Assert.Equal("Fixed", live.HeaderTagline);
        Assert.Equal("booru", live.HeaderTaglineFixedId);
        Assert.Equal("1.0.5", live.LastTaglineVersion);
        Config copy = new();
        copy.CopyFrom(original);
        copy.SaveConfig(directory.Path);
        Config loaded = Config.LoadConfig(directory.Path);
        Assert.Null(loaded.LoadWarning);
        foreach (Config config in new[] { clone, copy, loaded })
            Assert.Equal(("Fixed", "booru", "1.1.0"), (config.HeaderTagline, config.HeaderTaglineFixedId, config.LastTaglineVersion));
    }

    [Theory]
    [InlineData("\"Weekly\"", "PerStart")]
    [InlineData("\"daily\"", "Daily")]
    [InlineData("42", "PerStart")]
    [InlineData("null", "PerStart")]
    [InlineData("true", "PerStart")]
    [InlineData("[]", "PerStart")]
    public void AnInvalidModeInTheSettingsFileReadsAsTheDefaultAndKeepsTheRest(string literal, string expected)
    {
        using TempDirectory directory = new();
        new Config { HeaderTagline = "Off", BooruCatalogLimitMiB = 17 }.SaveConfig(directory.Path);
        string file = Path.Combine(directory.Path, "settings.json");
        string text = File.ReadAllText(file);
        string changed = Regex.Replace(text, "(\"HeaderTagline\"\\s*:\\s*)\"Off\"", "${1}" + literal);
        Assert.NotEqual(text, changed);
        File.WriteAllText(file, changed);
        Config loaded = Config.LoadConfig(directory.Path);
        Assert.Null(loaded.LoadWarning);
        Assert.Equal(expected, loaded.HeaderTagline);
        Assert.Equal(17, loaded.BooruCatalogLimitMiB);
    }

    [Fact]
    public void AnOlderFileWithoutTheValuesGetsTheDefaults()
    {
        using TempDirectory directory = new();
        new Config { HeaderTagline = "Off", HeaderTaglineFixedId = "booru", LastTaglineVersion = "1.1.0" }.SaveConfig(directory.Path);
        string file = Path.Combine(directory.Path, "settings.json");
        string text = File.ReadAllText(file);
        string changed = Regex.Replace(text, "\\s*\"(HeaderTagline|HeaderTaglineFixedId|LastTaglineVersion)\"\\s*:\\s*\"[^\"]*\",?", "");
        Assert.NotEqual(text, changed);
        File.WriteAllText(file, changed);
        Config loaded = Config.LoadConfig(directory.Path);
        Assert.Null(loaded.LoadWarning);
        Assert.Equal(("PerStart", "cats", ""), (loaded.HeaderTagline, loaded.HeaderTaglineFixedId, loaded.LastTaglineVersion));
    }

    [Fact]
    public void AMalformedIdOrVersionReadsAsTheDefault()
    {
        Config config = new() { HeaderTaglineFixedId = "not an id", LastTaglineVersion = "soon" };
        Assert.Equal("cats", config.HeaderTaglineFixedId);
        Assert.Equal("", config.LastTaglineVersion);
    }
}
