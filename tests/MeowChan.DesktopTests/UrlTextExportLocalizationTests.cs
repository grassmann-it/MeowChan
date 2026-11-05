using System.Globalization;
using System.Text.Json;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

// Catalog/embedded-resource checks, not translation quality or native layout.
[Collection("Localization")]
public sealed class UrlTextExportLocalizationTests : IDisposable
{
    private readonly string _language = Texts.Language;
    public UrlTextExportLocalizationTests() => Texts.Initialize();
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage(_language); }

    [Theory]
    [InlineData("en")]
    [InlineData("de")]
    [InlineData("es")]
    [InlineData("fr")]
    [InlineData("ru")]
    public void EveryConsentAndResultKeyIsRegisteredAndPreservesOnlyItsDeclaredArgument(string language)
    {
        Assert.Equal(29, UrlTextExportCatalog.Entries.Length);
        Assert.Equal(29, UrlTextExportCatalog.Entries.Select(e => e.Key).Distinct(StringComparer.Ordinal).Count());
        Texts.SetLanguage(language);
        JsonDocument? pack = null;
        try
        {
            if (language is not ("en" or "de"))
            {
                using Stream stream = typeof(Texts).Assembly.GetManifestResourceStream(
                    "MeowChan.Desktop.Localization.Packs." + language + ".json")!;
                Assert.NotNull(stream);
                pack = JsonDocument.Parse(stream);
                var strings = pack.RootElement.GetProperty("strings");
                Assert.Equal(UrlTextExportCatalog.Entries.Select(e => e.Key).Order(),
                    strings.EnumerateObject().Where(p => p.Name.StartsWith("urltext.export.", StringComparison.Ordinal))
                        .Select(p => p.Name).Order());
            }
            foreach (var entry in UrlTextExportCatalog.Entries)
            {
                string expected = language switch
                {
                    "en" => entry.English, "de" => entry.German,
                    _ => pack!.RootElement.GetProperty("strings").GetProperty(entry.Key).GetString()!
                };
                Assert.False(string.IsNullOrWhiteSpace(expected));
                bool hasArgument = entry.Key is "urltext.export.source" or "urltext.export.board"
                    or "urltext.export.thread" or "urltext.export.url";
                Assert.Equal(hasArgument ? 1 : 0, CompositeFormatArgumentCount(expected));
                const string value = "frozen_{0}_Ä/原";
                Assert.Equal(hasArgument ? string.Format(CultureInfo.CurrentUICulture, expected, value) : expected,
                    hasArgument ? Texts.T(entry.Key, value) : Texts.T(entry.Key));
            }
        }
        finally { pack?.Dispose(); }
    }

    private static int CompositeFormatArgumentCount(string format) => System.Text.CompositeFormat.Parse(format).MinimumArgumentCount;
}
