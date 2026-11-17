using System.Collections.Immutable;
using System.Text.RegularExpressions;
using MeowChan.Desktop.Booru;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;
using MeowChan.PluginSdk;
using MeowChan.Tests.Shared;

namespace MeowChan.DesktopTests;

[Collection("Localization")]
public sealed class BooruLocalizationTests : IDisposable
{
    private static readonly string[] Languages = { "en", "de", "es", "fr", "ru" };
    private static readonly Regex Placeholder = new(@"\{\d+\}", RegexOptions.Compiled);

    public BooruLocalizationTests()
    {
        Texts.Initialize();
        Texts.SetLanguage("en");
    }

    public void Dispose() => Texts.SetLanguage("en");

    private static string[] Placeholders(string text) => Placeholder.Matches(text).Select(match => match.Value).Order().ToArray();

    [BooruSharedFact]
    public void EveryKeyTheUiCanRequestExistsInEnglishAndGerman()
    {
        string[] required = BooruText.RequiredKeys().ToArray();
        Assert.Equal(required.Length, required.Distinct().Count());
        string[] missingEnglish = required.Where(key => !BuiltInCatalog.English.ContainsKey(key)).ToArray();
        string[] missingGerman = required.Where(key => !BuiltInCatalog.German.ContainsKey(key)).ToArray();
        Assert.True(missingEnglish.Length == 0, "Missing English: " + string.Join(", ", missingEnglish));
        Assert.True(missingGerman.Length == 0, "Missing German: " + string.Join(", ", missingGerman));
    }

    [BooruSharedFact]
    public void NoCatalogKeyIsUnusedOrAnOrphanOfTheUi()
    {
        HashSet<string> required = BooruText.RequiredKeys().ToHashSet(StringComparer.Ordinal);
        string[] extra = BooruUiCatalog.Entries.Select(entry => entry.Key).Where(key => !required.Contains(key)).ToArray();
        Assert.True(extra.Length == 0, "Catalog keys the UI never requests: " + string.Join(", ", extra));
    }

    [BooruSharedTheory]
    [InlineData("de")]
    [InlineData("es")]
    [InlineData("fr")]
    [InlineData("ru")]
    public void EveryLanguageHasEveryKeyWithTheSamePlaceholders(string code)
    {
        Texts.SetLanguage(code);
        Assert.Equal(code, Texts.Language);
        foreach (string key in BooruText.RequiredKeys())
        {
            string english = BuiltInCatalog.English[key];
            string translated = Texts.T(key);
            Assert.False(string.IsNullOrWhiteSpace(translated), key);
            Assert.NotEqual(key, translated);
            Assert.Equal(Placeholders(english), Placeholders(translated));
        }
    }

    [BooruSharedTheory]
    [InlineData("en")]
    [InlineData("de")]
    [InlineData("es")]
    [InlineData("fr")]
    [InlineData("ru")]
    public void TheWorkbenchAndTheDetailDialogNameTheProviderRatingAndTagCategoryAlike(string code)
    {
        Texts.SetLanguage(code);
        // "Unknown" differs on purpose: the workbench says the provider sent none, the dialog says it is unknown.
        foreach (BooruRating rating in Enum.GetValues<BooruRating>().Where(value => value != BooruRating.Unknown))
            Assert.Equal(BooruMessages.Rating(rating), BooruText.Rating(rating));
        foreach (BooruTagCategory category in Enum.GetValues<BooruTagCategory>())
            Assert.Equal(BooruMessages.TagCategory(category), BooruText.TagCategory(category));
    }

    [BooruSharedFact]
    public void TheBundledPacksCarryTheBooruKeysAsMachineTranslations()
    {
        foreach (string code in new[] { "es", "fr", "ru" })
        {
            Texts.SetLanguage(code);
            int differing = BooruText.RequiredKeys().Count(key => Texts.T(key) != BuiltInCatalog.English[key]);
            // Proper names and technical terms may equal English, but the window text itself is translated.
            Assert.True(differing > BooruText.RequiredKeys().Count() * 0.9, code + " has too few translated texts: " + differing);
        }
    }

    [BooruSharedFact(BooruSharedNeed.OpenCommand)]
    public void TheCommandIsListedWithItsLocalizedLabel()
    {
        Assert.True(CommandCatalog.TryGet("booru.open", out CommandDefinition? definition));
        Assert.Equal(CommandScope.Application, definition!.Scope);
        foreach (string code in Languages)
        {
            Texts.SetLanguage(code);
            Assert.Equal(Texts.T("booru.ui.command.open"), CommandCatalog.Label("booru.open"));
        }
    }

    [BooruSharedFact]
    public void NoBooruTextIntroducesAnAgeGateOrAHiddenRatingFilter()
    {
        Regex forbidden = new(@"\b18\b|adult|age verif|Altersfreigabe|Altersbest|mayor de edad|majeur|18\+|совершеннолет", RegexOptions.IgnoreCase);
        foreach ((string key, string english, string german) in BooruUiCatalog.Entries)
        {
            Assert.DoesNotMatch(forbidden, english);
            Assert.DoesNotMatch(forbidden, german);
        }
        Assert.DoesNotContain(BooruText.RequiredKeys(), key => key.Contains("age", StringComparison.OrdinalIgnoreCase)
            && !key.Contains("page", StringComparison.OrdinalIgnoreCase) && !key.Contains("message", StringComparison.OrdinalIgnoreCase)
            && !key.Contains("msg", StringComparison.OrdinalIgnoreCase));
        // The notes the user sees state that nothing is added to the search.
        Assert.Contains("no rating filter", BuiltInCatalog.English["booru.ui.noFilterNote"]);
        Assert.Contains("adds nothing", BuiltInCatalog.English["booru.ui.query.hint"]);
    }

    [BooruSharedFact]
    public void TheRemoveNoteSaysLocalMediaStays()
    {
        Assert.Contains("stay in your library", BuiltInCatalog.English["booru.ui.watchers.removeNote"]);
        Assert.Contains("not deleted", BuiltInCatalog.English["booru.ui.watcher.removeAsk"]);
        Assert.Contains("bleiben in deiner Bibliothek", BuiltInCatalog.German["booru.ui.watchers.removeNote"]);
        Assert.Contains("blieben erhalten", BuiltInCatalog.German["booru.ui.watcher.msg.removed"]);
    }

    // ---- every state renders in every language without a raw key or an unfilled placeholder ----------------

    private static IEnumerable<string> RenderEverything()
    {
        BooruInputMode[] modes = Enum.GetValues<BooruInputMode>();
        BooruPostView post = BooruUiFixtures.Post("1");
        DateTimeOffset now = new(2026, 11, 12, 10, 0, 0, TimeSpan.Zero);
        foreach (BooruSearchStatus status in Enum.GetValues<BooruSearchStatus>())
            foreach (BooruInputMode mode in modes)
                foreach (bool postResult in new[] { false, true })
                    foreach (bool paused in new[] { false, true })
                    {
                        BooruSearchView view = new("danbooru", "Danbooru", mode, "cat", new BooruParsedInput(BooruInputKind.Invalid, Issue: BooruInputIssue.Rating),
                            status, postResult ? null : "cat", postResult ? "1" : null, 2, false,
                            ImmutableArray.Create(new BooruResultRowView(post)), now.AddMinutes(1), "code", null);
                        yield return BooruText.SearchStatus(view, paused);
                    }
        foreach (BooruInputIssue issue in Enum.GetValues<BooruInputIssue>().Where(issue => issue != BooruInputIssue.None))
            yield return BooruText.Issue(issue);
        foreach (BooruRating rating in Enum.GetValues<BooruRating>()) yield return BooruText.Rating(rating);
        foreach (BooruTagCategory category in Enum.GetValues<BooruTagCategory>()) yield return BooruText.TagCategory(category);
        foreach (BooruOriginalAccess access in Enum.GetValues<BooruOriginalAccess>())
            foreach (bool withdrawn in new[] { false, true })
                yield return BooruText.Original(access, withdrawn);
        foreach (BooruRowLoad load in Enum.GetValues<BooruRowLoad>())
            if (BooruText.Load(new BooruResultRowView(post, load, "code")) is { } text) yield return text;
        foreach (BooruWatcherStatus status in Enum.GetValues<BooruWatcherStatus>())
            foreach (bool cooling in new[] { false, true })
                yield return BooruText.WatcherStatus(new BooruWatcherRowView(BooruUiFixtures.Watcher("w", "cat", status)), cooling);
        foreach (BooruWatcherBlock block in Enum.GetValues<BooruWatcherBlock>().Where(block => block != BooruWatcherBlock.None))
            yield return BooruText.WatcherBlock(block, now);
        foreach (string code in new[] { "watcher_removed", "watcher_paused", "check_already_running", "cooldown_active",
            "watcher_already_paused", "watcher_already_active", "something_new" })
            yield return BooruText.Notice(new BooruNotice(BooruNoticeKind.Warning, "booru.ui.watcher.reject", new object?[] { code }));
        foreach (string key in BooruText.RequiredKeys().Where(key => key.StartsWith("booru.ui.watcher.msg.", StringComparison.Ordinal)
            || key is "booru.ui.watcher.created" or "booru.ui.watcher.duplicate" or "booru.ui.watcher.createFailed"))
            yield return BooruText.Notice(new BooruNotice(BooruNoticeKind.Info, key, new object?[] { now, 3 }));
    }

    [BooruSharedTheory]
    [InlineData("en")]
    [InlineData("de")]
    [InlineData("es")]
    [InlineData("fr")]
    [InlineData("ru")]
    public void EveryStateRendersInTheLiveLanguage(string code)
    {
        Texts.SetLanguage(code);
        foreach (string text in RenderEverything())
        {
            Assert.False(string.IsNullOrWhiteSpace(text));
            Assert.DoesNotContain("booru.ui.", text);
            Assert.DoesNotMatch(Placeholder, text);
        }
    }

    [BooruSharedFact]
    public void ARateLimitTextNamesTheTimeAndStatesNothingIsSentUntilThen()
    {
        DateTimeOffset until = new(2026, 11, 12, 10, 5, 0, TimeSpan.Zero);
        BooruSearchView view = new("danbooru", "Danbooru", BooruInputMode.Tags, "cat", new BooruParsedInput(BooruInputKind.Tags, "cat"),
            BooruSearchStatus.RateLimited, "cat", null, 1, false, ImmutableArray<BooruResultRowView>.Empty, until, "x", null);
        string text = BooruText.SearchStatus(view, paused: true);
        Assert.Contains("429", text);
        Assert.Contains(BooruText.Time(until), text);
        Assert.Contains("nothing is sent", text);
        Assert.Contains("pause is over", BooruText.SearchStatus(view, paused: false));
    }

    [BooruSharedFact]
    public void FailureSentencesShowOnlyTheMachineCodeAndTheCodeMappingCanBeReplaced()
    {
        Func<string?, string> original = BooruText.CodeText;
        try
        {
            BooruSearchView view = new("danbooru", "Danbooru", BooruInputMode.Tags, "cat", new BooruParsedInput(BooruInputKind.Tags, "cat"),
                BooruSearchStatus.Failed, "cat", null, 1, false, ImmutableArray<BooruResultRowView>.Empty, null, "http-timeout", null);
            Assert.Contains("(http-timeout)", BooruText.SearchStatus(view, false));
            BooruText.CodeText = code => "mapped:" + code;
            Assert.Contains("(mapped:http-timeout)", BooruText.SearchStatus(view, false));
            Assert.Contains("(mapped:store)", BooruText.Notice(new BooruNotice(BooruNoticeKind.Error, "booru.ui.watcher.createFailed", new object?[] { "store" })));
            Assert.Contains("(mapped:x)", BooruText.Load(new BooruResultRowView(BooruUiFixtures.Post("1"), BooruRowLoad.Failed, "x"))!);
        }
        finally { BooruText.CodeText = original; }
    }

    [BooruSharedFact]
    public void APluginBuildForAnotherSdkSaysWhatToDoInsteadOfARawCode()
    {
        BooruSearchView view = new("danbooru", "Danbooru", BooruInputMode.Tags, "blue_eyes", new BooruParsedInput(BooruInputKind.Tags, "blue_eyes"),
            BooruSearchStatus.Failed, "blue_eyes", null, 1, false, ImmutableArray<BooruResultRowView>.Empty, null, BooruText.SdkMismatchCode, null);
        string text = BooruText.SearchStatus(view, false);
        Assert.Equal(Texts.T("booru.ui.status.pluginMismatch", Texts.T("plugins.repair.review")), text);
        Assert.Contains("(sdk-mismatch)", text);
        // The whole route in one sentence: the dialog's shared name, confirm, restart.
        Assert.Contains("“Review bundled repairs”", text);
        Assert.Contains("confirm the current bundled Danbooru build", text);
        Assert.Contains("restart MeowChan", text);
        Assert.Contains("Nothing was loaded", text);
        // Every other failure keeps the generic sentence with its code.
        Assert.Contains("(plugin-error)", BooruText.SearchStatus(view with { Code = "plugin-error" }, false));
    }
}
