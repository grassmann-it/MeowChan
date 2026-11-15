using System.Text.RegularExpressions;
using MeowChan.Desktop.Booru;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

// Review finding M2: every machine code the Danbooru window can show has a sentence in every language, and the
// core/port sources produce no code that is missing from BooruCodes.
[Collection("Localization")]
public sealed class BooruCodeTextTests : IDisposable
{
    // Snake-case string literals in these sources are watcher/port codes (identifiers such as "w-" are not).
    private static readonly string[] CodeSources =
    {
        "libmeowchan/Booru/DanbooruWatcherScheduler.cs",
        "libmeowchan/Booru/DanbooruWatcherDomain.cs",
        "MeowChan.PluginHost/Sources/Booru/BooruWatcherSearchPort.cs",
        "Desktop/Booru/BooruProductionPorts.cs",
        "Desktop/Booru/BooruAdapters.cs",
        "Desktop/Booru/BooruWatcherModel.cs",
        "Desktop/Booru/BooruSearchModel.cs"
    };

    // Literals in those sources that are not codes shown to the user.
    private static readonly HashSet<string> NotCodes = new(StringComparer.Ordinal)
    {
        // Worker reasons: the watcher port maps all of them to source_unavailable before the window sees them.
        "capability", "stopped", "worker-exit", "restart-limit", "unresponsive", "start", "capacity", "memory-limit",
        "temporary-limit", "temporary-budget", "intervention-required", "timeout",
        // Host failure codes with booru.failure.* texts (BooruStatusCatalog).
        "http-network", "http-timeout", "booru-budget", "access-denied", "not-found", "invalid_page", "invalid-request",
        "post-id",
        // Log-only reasons used while reading the provider cooldown; the surfaced fault is
        // provider_cooldown_unavailable, which has its own catalog text.
        "transport_fence_read", "transport_cooldown_read", "maximum_cooldown_read"
    };

    private static readonly Regex Literal = new("\"([a-z][a-z0-9]*(?:[_-][a-z0-9]+)+|cancelled|canceled|rejected|busy|closed|exception|error)\"",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public BooruCodeTextTests()
    {
        Texts.Initialize();
        Texts.SetLanguage("en");
    }

    public void Dispose() => Texts.SetLanguage("en");

    [Fact]
    public void EverySurfacedCodeHasEnglishAndGermanCatalogText()
    {
        Assert.Equal(BooruCodes.All.Count, BooruCodes.All.Distinct(StringComparer.Ordinal).Count());
        string[] missingEnglish = BooruCodes.All.Where(code => !BuiltInCatalog.English.ContainsKey(BooruCodes.Key(code))).ToArray();
        string[] missingGerman = BooruCodes.All.Where(code => !BuiltInCatalog.German.ContainsKey(BooruCodes.Key(code))).ToArray();
        Assert.True(missingEnglish.Length == 0, "Codes without English text: " + string.Join(", ", missingEnglish));
        Assert.True(missingGerman.Length == 0, "Codes without German text: " + string.Join(", ", missingGerman));
        // The new texts are complete sentences (they are shown inside "… ({0})." and on their own).
        Assert.All(BooruCodeCatalog.Entries, entry => Assert.DoesNotContain("{", entry.English));
    }

    [Theory]
    [InlineData("de")]
    [InlineData("es")]
    [InlineData("fr")]
    [InlineData("ru")]
    public void EverySurfacedCodeIsTranslatedInEveryShippedLanguage(string language)
    {
        Texts.SetLanguage(language);
        Assert.Equal(language, Texts.Language);
        string[] untranslated = BooruCodeCatalog.Entries.Select(entry => entry.Key)
            .Where(key => Texts.T(key) == key || Texts.T(key) == BuiltInCatalog.English[key]).ToArray();
        Assert.True(untranslated.Length == 0, language + " lacks: " + string.Join(", ", untranslated));
    }

    [Fact]
    public void TheWindowCodeSeamShowsASentenceForEveryCodeInsteadOfTheRawCode()
    {
        foreach (string code in BooruCodes.All)
        {
            string text = BooruCodeTexts.Localized(code);
            Assert.NotEqual(code, text);
            Assert.Equal(BuiltInCatalog.English[BooruCodes.Key(code)], text);
        }
        // Host failure codes keep their B8 texts; an unknown code stays short instead of nesting a sentence.
        Assert.Equal(BooruMessages.Failure("http-network"), BooruCodeTexts.Localized("http-network"));
        Assert.Equal("some_new_code", BooruCodeTexts.Localized("some_new_code"));
        // MS-R1: a plugin switched off during a search reaches the search window as "stopped", which needs a sentence.
        Assert.Equal(BuiltInCatalog.English["booru.failure.stopped"], BooruCodeTexts.Localized("stopped"));
    }

    [Fact]
    public void TheCoreAndPortSourcesProduceNoCodeWithoutText()
    {
        string root = RepositoryRoot();
        List<string> unknown = new();
        foreach (string relative in CodeSources)
        {
            string path = Path.Combine(root, relative);
            Assert.True(File.Exists(path), path);
            foreach (Match match in Literal.Matches(File.ReadAllText(path)))
            {
                string code = match.Groups[1].Value;
                if (NotCodes.Contains(code) || BooruCodes.IsKnown(code)) continue;
                unknown.Add(relative + ": " + code);
            }
        }
        Assert.True(unknown.Count == 0, "Codes without a BooruCodes entry and text: " + string.Join(", ", unknown.Distinct()));
    }

    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MeowChan.sln"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root missing.");
    }
}
