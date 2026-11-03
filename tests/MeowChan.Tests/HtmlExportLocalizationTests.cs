using System.Net;
using MeowChan.Archive;

namespace MeowChan.Tests;

public sealed class HtmlExportLocalizationTests
{
    [Theory]
    [InlineData("en", "en", "Export result")]
    [InlineData("de", "de", "Exportergebnis")]
    [InlineData("es", "es", "Resultado de la exportación")]
    [InlineData("fr", "fr", "Résultat de l’exportation")]
    [InlineData("ru", "ru", "Результат экспорта")]
    [InlineData("fr-CA", "fr", "Résultat de l’exportation")]
    public void SupportedLanguageUsesLocalizedHtmlFrame(string language, string expectedCode, string expectedResult)
    {
        HtmlExportLocalization localization = new(language);

        Assert.Equal(expectedCode, localization.Language);
        Assert.Equal(expectedResult, WebUtility.HtmlDecode(localization.Html(HtmlExportText.ExportResult)));
    }

    [Theory]
    [InlineData("xx-YY")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("fr-\"><script>alert(1)</script>")]
    public void UnknownLanguageFallsBackToSafeEnglishFrame(string? language)
    {
        HtmlExportLocalization localization = new(language);

        Assert.Equal("en", localization.Language);
        Assert.Equal("Export result", localization.Html(HtmlExportText.ExportResult));
    }

    [Fact]
    public void DynamicFrameValuesAreFormattedAndHtmlEncoded()
    {
        HtmlExportLocalization localization = new("de");

        Assert.Equal("Gespeicherte Datei: 4 Bytes.", localization.Html(HtmlExportText.SavedFile, 4));
        Assert.Equal(WebUtility.HtmlEncode("Größe: <4> Bytes"), localization.Html(HtmlExportText.Size, "<4>"));
    }

    [Theory]
    [InlineData("de")]
    [InlineData("es")]
    [InlineData("fr")]
    [InlineData("ru")]
    public void EveryFrameKeyHasNonEnglishTextWithMatchingFormatArguments(string language)
    {
        HtmlExportLocalization english = new("en");
        HtmlExportLocalization translated = new(language);
        object?[] values = ["<first>", "second&", "third\"", "fourth"];
        foreach (HtmlExportText key in Enum.GetValues<HtmlExportText>())
        {
            string original = english.Get(key);
            string value = translated.Get(key);
            Assert.False(string.IsNullOrWhiteSpace(value));
            Assert.NotEqual(original, value);
            Assert.Equal(Placeholders(original), Placeholders(value));
            Assert.Equal(WebUtility.HtmlEncode(string.Format(System.Globalization.CultureInfo.InvariantCulture, value, values)),
                translated.Html(key, values));
        }
    }

    [Fact]
    public void OldOptionsJsonKeepsEnglishAndOneBooleanConstructorAndDeconstruction()
    {
        ArchiveHtmlExportOptions options = System.Text.Json.JsonSerializer.Deserialize<ArchiveHtmlExportOptions>("{\"IncludeMedia\":false}")!;
        Assert.False(options.IncludeMedia);
        Assert.Equal("en", options.Language);
        options.Deconstruct(out bool media);
        Assert.False(media);
        Assert.Equal(new ArchiveHtmlExportOptions(false), options);
        ArchiveHtmlExportOptions french = options with { Language = "fr" };
        Assert.Equal(french, System.Text.Json.JsonSerializer.Deserialize<ArchiveHtmlExportOptions>(
            System.Text.Json.JsonSerializer.Serialize(french)));
    }

    [Fact]
    public void CapturedWarningTextIsNotTranslatedAsArbitrarySourceData()
    {
        HtmlExportLocalization localization = new("es");

        Assert.Contains("manifiesto", WebUtility.HtmlDecode(localization.WarningHtml("A saved media manifest is unavailable.")));
        Assert.Equal("&lt;untrusted&gt;", localization.WarningHtml("<untrusted>"));
    }
    private static string[] Placeholders(string value) => System.Text.RegularExpressions.Regex
        .Matches(value, @"\{[0-9]+\}").Select(match => match.Value).Order(StringComparer.Ordinal).ToArray();
}
