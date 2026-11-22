using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using MeowChan.Archive;
using MeowChan.Booru;
using MeowChan.Desktop.Booru;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Plugins;
using MeowChan.PluginSdk;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

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
        Regex codeFormat = new(@"^[a-z0-9_-]+$", RegexOptions.CultureInvariant);
        Regex deferred = new(@"^(booru-cooldown|provider-[a-z-]+)$", RegexOptions.CultureInvariant);
        HashSet<string> codes = new(StringComparer.Ordinal);
        HashSet<string> reasons = new(StringComparer.Ordinal);
        foreach (string file in files)
        {
            var source = CSharpSource.Parse(File.ReadAllText(Path.Combine(root, file)));
            var failures = source.Calls("Failure").Concat(source.Calls("Failed"))
                .Select(call => call.ArgumentList.Arguments)
                .Concat(source.DescendantNodes().OfType<ObjectCreationExpressionSyntax>()
                    .Where(creation => creation.Type is IdentifierNameSyntax type && type.Identifier.ValueText == "FailureDetail")
                    .Select(creation => creation.ArgumentList!.Arguments));
            var namedCodes = source.DescendantNodes().OfType<FieldDeclarationSyntax>()
                .Where(field => field.Modifiers.Any(SyntaxKind.ConstKeyword))
                .SelectMany(field => field.Declaration.Variables)
                .Where(variable => variable.Identifier.ValueText.EndsWith("Code", StringComparison.Ordinal))
                .Select(variable => variable.Initializer?.Value);
            codes.UnionWith(failures.Select(arguments => arguments.FirstOrDefault()?.Expression).Concat(namedCodes)
                .OfType<LiteralExpressionSyntax>().Where(literal => literal.IsKind(SyntaxKind.StringLiteralExpression))
                .Select(literal => literal.Token.ValueText).Where(code => codeFormat.IsMatch(code)));
            reasons.UnionWith(source.DescendantNodes().OfType<LiteralExpressionSyntax>()
                .Where(literal => literal.IsKind(SyntaxKind.StringLiteralExpression))
                .Select(literal => literal.Token.ValueText).Where(reason => deferred.IsMatch(reason)));
        }
        Assert.NotEmpty(codes);
        Assert.Contains("booru-budget", codes);
        Assert.Contains("not-found", codes);
        Assert.Contains("stopped", codes); // MS-R1: the search facade's "worker switched off mid-call" outcome
        Assert.Contains("booru-cooldown", reasons);
        Assert.Contains("provider-throttled", reasons);
        foreach (string language in Languages.Concat(Texts.BundledPackCodes()).Distinct(StringComparer.Ordinal))
        {
            Texts.SetLanguage(language);
            foreach (string code in codes)
                Assert.True(BooruMessages.Failure(code) != Texts.T("booru.failure.unknown", code),
                    language + " has no text for the failure code " + code);
            foreach (string reason in reasons)
                Assert.NotEqual(BooruMessages.Deferred("unlisted-reason", DateTimeOffset.UnixEpoch), BooruMessages.Deferred(reason, DateTimeOffset.UnixEpoch));
        }
    }

    [Fact]
    public void InvalidPageSizeHasItsOwnTextInEveryBundledPack()
    {
        const string key = "booru.failure.invalid_limit";
        foreach (string language in Texts.BundledPackCodes())
        {
            using Stream stream = typeof(Texts).Assembly.GetManifestResourceStream(Texts.BundledPackResourcePrefix + language + ".json")!;
            using JsonDocument document = JsonDocument.Parse(stream);
            Assert.True(document.RootElement.GetProperty("strings").TryGetProperty(key, out JsonElement text), language);
            Assert.False(string.IsNullOrWhiteSpace(text.GetString()), language);
            Texts.SetLanguage(language);
            Assert.Equal(text.GetString(), BooruMessages.Failure("invalid_limit"));
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
        Assert.Contains(BooruCodes.WatcherNotResumable, codes);
        Assert.Contains(BooruCodes.CooldownNotActive, codes);
        foreach (string code in codes)
        {
            Assert.True(BooruCodes.IsKnown(code), code);
            string key = BooruCodes.Key(code);
            Assert.True(BuiltInCatalog.English.ContainsKey(key), "en " + code);
            Assert.True(BuiltInCatalog.German.ContainsKey(key), "de " + code);
            foreach (string language in new[] { "en", "de" })
            {
                Texts.SetLanguage(language);
                Assert.Equal(Texts.T(key), BooruMessages.WatcherProblem(code));
                Assert.Equal(Texts.T(key), BooruCodeTexts.Localized(code));
            }
        }
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
            // No code at all gets its own sentence (no "(?)" placeholder): the next step and where the details are.
            Assert.Equal(Texts.T("booru.failure.unknownNoCode"), BooruMessages.Failure(null));
            Assert.DoesNotContain("{", BooruMessages.Failure(null));
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
