using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using MeowChan.Archive;
using MeowChan.Booru;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Plugins;
using MeowChan.PluginSdk;

namespace MeowChan.DesktopTests;

// B8: every status, failure and vocabulary code the booru host, plugin and domain code already produces
// has a catalog text in all five languages. The UI shows these texts, never a raw diagnostic message.
[Collection("Localization")]
public sealed class BooruStatusCatalogTests : IDisposable
{
    private static readonly string[] Languages = { "en", "de", "es", "fr", "ru" };
    // Proper nouns, abbreviations and international words may legitimately equal the English text.
    private static readonly HashSet<string> MayEqualEnglish = new(StringComparer.Ordinal)
    {
        "booru.watcher.state.Error", "booru.tagCategory.Meta", "booru.tagCategory.Copyright", "booru.mediaRole.Original",
        "booru.rating.Sensitive", "booru.rating.Explicit", "booru.rating.General", "booru.tagCategory.General",
        "booru.tagCategory.Artist", "booru.tagCategory.Character", "booru.mediaRole.Preview", "booru.rating.Unknown",
        "booru.watcher.state.Unknown", "booru.mediaRole.Other", "booru.rating.Questionable", "booru.watcher.state.Active",
        "booru.watcher.state.Paused", "booru.watcher.state.Removed", "booru.watcher.state.Checking", "booru.tagCategory.Unknown",
        "booru.rating.Questionable", "booru.rating.label"
    };

    private readonly string _previous = Texts.Language;
    public BooruStatusCatalogTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage(_previous); }

    [Fact]
    public void KeysAreUniqueNonEmptyAndKeepTheirPlaceholdersInEveryLanguage()
    {
        Assert.Equal(BooruStatusCatalog.Entries.Length, BooruStatusCatalog.Entries.Select(entry => entry.Key).Distinct().Count());
        foreach (var entry in BooruStatusCatalog.Entries)
        {
            string english = Resolve("en", entry.Key);
            Assert.Equal(entry.English, english);
            foreach (string language in Languages)
            {
                string text = Resolve(language, entry.Key);
                Assert.False(string.IsNullOrWhiteSpace(text), language + " " + entry.Key);
                Assert.NotEqual(entry.Key, text);
                Assert.Equal(Placeholders(english), Placeholders(text));
                // A pack entry that silently fell back to English would hide a missing translation.
                if (language != "en" && !MayEqualEnglish.Contains(entry.Key))
                    Assert.True(text != english, language + " is untranslated: " + entry.Key);
            }
        }
    }

    [Fact]
    public void GermanCatalogIsComplete()
    {
        foreach (var entry in BooruStatusCatalog.Entries)
        {
            Assert.True(BuiltInCatalog.German.TryGetValue(entry.Key, out string? german), entry.Key);
            Assert.Equal(entry.German, german);
        }
    }

    [Fact]
    public void EveryEnumValueOfTheBooruVocabularyHasItsOwnText()
    {
        AssertEveryValue<DanbooruWatcherState>("booru.watcher.state.");
        AssertEveryValue<DanbooruWatcherCreateError>("booru.watcher.create.");
        AssertEveryValue<DanbooruTagQueryError>("booru.query.problem.", "NullInput");
        AssertEveryValue<DanbooruPostReferenceError>("booru.post.problem.");
        AssertEveryValue<BooruOriginalSelectionError>("booru.original.problem.");
        AssertEveryValue<BooruRating>("booru.rating.");
        AssertEveryValue<BooruPostClaimRating>("booru.rating.");
        AssertEveryValue<BooruTagCategory>("booru.tagCategory.");
        AssertEveryValue<BooruPostClaimTagCategory>("booru.tagCategory.");
        AssertEveryValue<BooruMediaRole>("booru.mediaRole.");
        AssertEveryValue<BooruPostClaimMediaRole>("booru.mediaRole.");
        AssertEveryValue<BooruFindingKind>("booru.finding.");
    }

    [Fact]
    public void EveryFailureAndDeferredCodeInTheBooruSourcesIsKnown()
    {
        string root = RepositoryRoot();
        string[] files =
        {
            "MeowChan.PluginHost/Http/BooruHostHttp.cs", "MeowChan.PluginHost/Sources/Booru/BooruHostServices.cs",
            "MeowChan.PluginHost/Sources/Booru/BooruSearchService.cs", "plugins/Official/Danbooru/Danbooru.cs"
        };
        Regex failure = new(@"(?:Failure|Failed|FailureDetail)\(\s*""(?<code>[a-z0-9_-]+)""", RegexOptions.CultureInvariant);
        Regex deferred = new(@"""(?<code>booru-cooldown|provider-[a-z-]+)""", RegexOptions.CultureInvariant);
        HashSet<string> codes = new(StringComparer.Ordinal) { "invalid_query", "post_id" /* BooruSearchService public constants */ };
        HashSet<string> reasons = new(StringComparer.Ordinal);
        foreach (string file in files)
        {
            string text = File.ReadAllText(Path.Combine(root, file));
            foreach (Match match in failure.Matches(text)) codes.Add(match.Groups["code"].Value);
            foreach (Match match in deferred.Matches(text)) reasons.Add(match.Groups["code"].Value);
        }
        Assert.NotEmpty(codes);
        Assert.Contains("booru-budget", codes);
        Assert.Contains("not-found", codes);
        Assert.Contains("booru-cooldown", reasons);
        Assert.Contains("provider-throttled", reasons);
        foreach (string language in Languages)
        {
            Texts.SetLanguage(language);
            foreach (string code in codes.Where(code => code != "http-" /* dynamic "http-" + status */))
                Assert.False(BooruMessages.Failure(code).Contains("(" + code + ")", StringComparison.Ordinal), language + " has no text for the failure code " + code);
            foreach (string reason in reasons)
                Assert.NotEqual(BooruMessages.Deferred("unlisted-reason", DateTimeOffset.UnixEpoch), BooruMessages.Deferred(reason, DateTimeOffset.UnixEpoch));
        }
    }

    [Fact]
    public void EveryWatcherDomainCodeIsKnown()
    {
        string text = File.ReadAllText(Path.Combine(RepositoryRoot(), "libmeowchan/Booru/DanbooruWatcherDomain.cs"));
        string[] codes = Regex.Matches(text, @"(?:Reject|Fail)\(watcher, ""(?<code>[a-z_]+)""\)").Select(match => match.Groups["code"].Value)
            .Distinct(StringComparer.Ordinal).ToArray();
        Assert.Contains("cooldown_active", codes);
        Assert.Contains("page_binding_mismatch", codes);
        foreach (string code in codes.Where(code => code is not ("missing_reason" or "missing_error")))
            Assert.True(BuiltInCatalog.English.ContainsKey("booru.watcher.problem." + code), code);
    }

    [Fact]
    public void UnknownCodesStillProduceACatalogTextWithTheCodeAndNeverARawMessage()
    {
        foreach (string language in Languages)
        {
            Texts.SetLanguage(language);
            Assert.Contains("brand-new-code", BooruMessages.Failure("brand-new-code"));
            Assert.Contains("brand-new-code", BooruMessages.WatcherProblem("brand-new-code"));
            Assert.Contains("503", BooruMessages.Failure("http-503"));
            Assert.Contains("?", BooruMessages.Failure(null));
            Assert.DoesNotContain("{", BooruMessages.Failure("http-503"));
        }
    }

    [Fact]
    public void CooldownTextShowsTheNextPossibleAttemptInTheSelectedCulture()
    {
        DateTimeOffset retry = new(2026, 11, 12, 15, 30, 0, TimeSpan.Zero);
        foreach (string language in Languages)
        {
            Texts.SetLanguage(language);
            string text = BooruMessages.Deferred("provider-throttled", retry);
            Assert.Contains(retry.ToLocalTime().ToString("g", CultureInfo.GetCultureInfo(language)), text);
            Assert.DoesNotContain("{", text);
        }
    }

    [Fact]
    public void RateLimitAndRedirectTextsStayTruthfulAboutTheirBehaviour()
    {
        Texts.SetLanguage("en");
        Assert.Contains("429", Texts.T("booru.deferred.provider-throttled", DateTimeOffset.UnixEpoch));
        Assert.Contains("does not follow", Texts.T("booru.failure.http-redirect"));
        Assert.Contains("does not substitute", Texts.T("booru.original.problem.MissingOriginal"));
        Assert.Contains("does not substitute", Texts.T("booru.original.problem.OriginalUrlMissing"));
    }

    [Fact]
    public void PostSearchHasACapabilityLabelInsteadOfAHexadecimalPlaceholder()
    {
        foreach (string language in Languages)
        {
            Texts.SetLanguage(language);
            string label = PluginContentPresentation.DescribeCapabilities(
                SourceCapabilities.PostSearch | SourceCapabilities.OriginalMedia | SourceCapabilities.Previews);
            Assert.DoesNotContain("0x", label);
            Assert.Contains(Texts.T("plugins.capability.postSearch"), label);
            Assert.Contains(Texts.T("plugins.capability.originalMedia"), label);
        }
    }

    private static void AssertEveryValue<TEnum>(string prefix, params string[] covered) where TEnum : struct, Enum
    {
        foreach (TEnum value in Enum.GetValues<TEnum>())
        {
            string name = value.ToString();
            if (name == "None") continue;
            if (covered.Contains(name)) continue;
            Assert.True(BuiltInCatalog.English.ContainsKey(prefix + name), typeof(TEnum).Name + "." + name);
        }
    }

    private static string Resolve(string language, string key)
    {
        Texts.SetLanguage(language);
        return Texts.T(key);
    }

    private static string[] Placeholders(string text) => Regex.Matches(text, @"\{\d+(:[^}]*)?\}")
        .Select(match => match.Value).Distinct().OrderBy(value => value, StringComparer.Ordinal).ToArray();

    private static string RepositoryRoot([CallerFilePath] string sourcePath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "../.."));
}
