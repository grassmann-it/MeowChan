using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

// Scoped native-transfer-preview catalog coverage. The generic language-pack tests own
// schema, discovery, custom packs and whole-catalog completeness; this class checks the
// real preview catalog and its enum-to-label selectors for all bundled languages.
[Collection("Localization")]
public sealed class NativeTransferPreviewLocalizationTests : IDisposable
{
    private const string ProviderDetail = "PROVIDER-DETAIL {0}";

    private readonly string _previousLanguage = Texts.Language;

    public NativeTransferPreviewLocalizationTests()
    {
        Texts.Initialize();
        Texts.SetLanguage("en");
    }

    public void Dispose()
    {
        Texts.Initialize();
        Texts.SetLanguage(_previousLanguage);
    }

    public static TheoryData<string> Languages => new() { "en", "de", "es", "fr", "ru" };

    [Theory]
    [MemberData(nameof(Languages))]
    public void EveryProductionPreviewCatalogStringIsBundledAndLoadedExactlyOnce(string language)
    {
        (string Key, string English, string German)[] source = NativeTransferPreviewCatalog.Entries;
        Assert.NotEmpty(source);
        Assert.All(source, entry => Assert.StartsWith("transfer.preview.", entry.Key, StringComparison.Ordinal));

        IReadOnlyDictionary<string, string> allStrings = ProductionStrings(language);
        IReadOnlyDictionary<string, string> scoped = allStrings
            .Where(pair => pair.Key.StartsWith("transfer.preview.", StringComparison.Ordinal))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        Assert.Equal(
            source.Select(entry => entry.Key).Order(StringComparer.Ordinal),
            scoped.Keys.Order(StringComparer.Ordinal));

        Texts.SetLanguage(language);
        Assert.Equal(language, Texts.Language);
        Assert.Empty(Texts.Warnings);
        foreach ((string key, string english, string german) in source)
        {
            string expected = language switch
            {
                "en" => english,
                "de" => german,
                _ => scoped[key]
            };
            string actual = Assert.IsType<string>(scoped.TryGetValue(key, out string? bundled) ? bundled : null);
            Assert.False(string.IsNullOrWhiteSpace(actual), $"{language}: empty {key}");
            Assert.NotEqual(key, actual);
            Assert.DoesNotContain(ProviderDetail, actual, StringComparison.Ordinal);
            Assert.Equal(expected, actual);
            Assert.Equal(expected, Texts.T(key));
        }
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public void PreviewStringsKeepTheirExactPlaceholderTokenSets(string language)
    {
        IReadOnlyDictionary<string, string> strings = ScopedProductionStrings(language);
        Texts.SetLanguage(language);

        foreach ((string key, string value) in strings)
        {
            string[] expected = PlaceholderContracts.TryGetValue(key, out string[]? tokens)
                ? tokens
                : [];
            HashSet<string> actual = PlaceholderTokens(value);
            Assert.True(actual.SetEquals(expected),
                $"{language}/{key}: expected [{string.Join(", ", expected)}] but got [{string.Join(", ", actual)}]");
        }
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public void EveryPreviewStateMapsToAResolvedCatalogLabel(string language)
    {
        IReadOnlyDictionary<string, string> strings = ScopedProductionStrings(language);
        Texts.SetLanguage(language);

        foreach (NativeTransferPreviewState state in Enum.GetValues<NativeTransferPreviewState>())
        {
            string key = StateKey(state);
            Assert.Equal(StateLabelKeys[state], key);
            AssertResolvedLabel(strings, language, key);
        }
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public void EveryPreviewErrorMapsToAResolvedCatalogLabel(string language)
    {
        IReadOnlyDictionary<string, string> strings = ScopedProductionStrings(language);
        Texts.SetLanguage(language);

        foreach (NativeTransferPreviewError error in Enum.GetValues<NativeTransferPreviewError>())
        {
            // None is rendered as an empty error field by the dialog, not as a text label.
            if (error == NativeTransferPreviewError.None) continue;
            string key = ErrorKey(error);
            Assert.Equal(ErrorLabelKeys[error], key);
            AssertResolvedLabel(strings, language, key);
        }
    }

    private static void AssertResolvedLabel(
        IReadOnlyDictionary<string, string> scopedStrings, string language, string key)
    {
        Assert.True(scopedStrings.TryGetValue(key, out string? expected), $"{language}: missing {key}");
        string actual = Texts.T(key);
        Assert.Equal(expected, actual);
        Assert.False(string.IsNullOrWhiteSpace(actual), $"{language}: empty mapping {key}");
        Assert.NotEqual(key, actual);
        Assert.DoesNotContain(ProviderDetail, actual, StringComparison.Ordinal);
        Assert.DoesNotContain("NativeTransferPreviewState.", actual, StringComparison.Ordinal);
        Assert.DoesNotContain("NativeTransferPreviewError.", actual, StringComparison.Ordinal);
    }

    private static IReadOnlyDictionary<string, string> ScopedProductionStrings(string language) =>
        ProductionStrings(language)
            .Where(pair => pair.Key.StartsWith("transfer.preview.", StringComparison.Ordinal))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

    private static IReadOnlyDictionary<string, string> ProductionStrings(string language) => language switch
    {
        "en" => BuiltInCatalog.English,
        "de" => BuiltInCatalog.German,
        "es" or "fr" or "ru" => ReadBundledStrings(language),
        _ => throw new ArgumentOutOfRangeException(nameof(language), language, null)
    };

    private static IReadOnlyDictionary<string, string> ReadBundledStrings(string language)
    {
        string resourceName = "MeowChan.Desktop.Localization.Packs." + language + ".json";
        using Stream stream = typeof(Texts).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException("Missing embedded preview language: " + language);
        using JsonDocument document = JsonDocument.Parse(stream);
        Assert.Equal(language, document.RootElement.GetProperty("code").GetString());
        return document.RootElement.GetProperty("strings").EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value.GetString()!,
                StringComparer.Ordinal);
    }

    private static HashSet<string> PlaceholderTokens(string value)
    {
        // CompositeFormat applies the production format grammar before the scoped selector.
        _ = System.Text.CompositeFormat.Parse(value);
        HashSet<string> tokens = new(StringComparer.Ordinal);
        foreach (Match match in PlaceholderPattern.Matches(value)) tokens.Add(match.Value);
        return tokens;
    }

    private static string StateKey(NativeTransferPreviewState state)
    {
        MethodInfo method = Assert.IsAssignableFrom<MethodInfo>(typeof(NativeTransferPreviewDialog).GetMethod(
            "StateKey", BindingFlags.NonPublic | BindingFlags.Static));
        NativeTransferPreviewSnapshot snapshot = new(1, state, NativeTransferPreviewError.None, null, false);
        return Assert.IsType<string>(method.Invoke(null, [snapshot]));
    }

    private static string ErrorKey(NativeTransferPreviewError error)
    {
        MethodInfo method = Assert.IsAssignableFrom<MethodInfo>(typeof(NativeTransferPreviewDialog).GetMethod(
            "ErrorKey", BindingFlags.NonPublic | BindingFlags.Static));
        return Assert.IsType<string>(method.Invoke(null, [error]));
    }

    private static readonly Regex PlaceholderPattern =
        new(@"\{\d+(?:,[^}:]+)?(?::[^}]+)?\}", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly IReadOnlyDictionary<string, string[]> PlaceholderContracts =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["transfer.preview.source"] = ["{0}"],
            ["transfer.preview.summary"] = ["{0}", "{1}", "{2}", "{3:N0}"],
            ["transfer.preview.thread"] = ["{0}", "{1}", "{2}", "{3}", "{4}", "{5}", "{6}"],
            ["transfer.preview.originals"] = ["{0}", "{1}", "{2}", "{3}", "{4}", "{5}"],
            ["transfer.preview.previews"] = ["{0}", "{1}", "{2}", "{3}", "{4}", "{5}"],
            ["transfer.preview.retained"] = ["{0}", "{1}", "{2}", "{3}", "{4}", "{5}"]
        };

    private static readonly IReadOnlyDictionary<NativeTransferPreviewState, string> StateLabelKeys =
        new Dictionary<NativeTransferPreviewState, string>
        {
            [NativeTransferPreviewState.Idle] = "transfer.preview.idle",
            [NativeTransferPreviewState.Reading] = "transfer.preview.reading",
            [NativeTransferPreviewState.Inventory] = "transfer.preview.ready",
            [NativeTransferPreviewState.Rejected] = "transfer.preview.rejected",
            [NativeTransferPreviewState.Canceled] = "transfer.preview.canceled",
            [NativeTransferPreviewState.Closed] = "transfer.preview.closed"
        };

    private static readonly IReadOnlyDictionary<NativeTransferPreviewError, string> ErrorLabelKeys =
        new Dictionary<NativeTransferPreviewError, string>
        {
            [NativeTransferPreviewError.InvalidSource] = "transfer.preview.error.source",
            [NativeTransferPreviewError.InvalidWorkDirectory] = "transfer.preview.error.workDirectory",
            [NativeTransferPreviewError.UnsupportedPlatform] = "transfer.preview.error.platform",
            [NativeTransferPreviewError.InvalidPackage] = "transfer.preview.error.package",
            [NativeTransferPreviewError.PrivateMetadataNotYetSupported] = "transfer.preview.error.private",
            [NativeTransferPreviewError.LimitExceeded] = "transfer.preview.error.limit",
            [NativeTransferPreviewError.ReadFailure] = "transfer.preview.error.read"
        };
}
