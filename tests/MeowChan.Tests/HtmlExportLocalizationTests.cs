using System.Globalization;
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
    [InlineData("cs", "cs", "Výsledek exportu")]
    [InlineData("it", "it", "Risultato dell'esportazione")]
    [InlineData("ja", "ja", "エクスポート結果")]
    [InlineData("ko", "ko", "내보내기 결과")]
    [InlineData("pl", "pl", "Wynik eksportu")]
    [InlineData("pt-BR", "pt-BR", "Resultado da exportação")]
    [InlineData("zh-Hans", "zh-Hans", "导出结果")]
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
    [InlineData("fr-")]
    [InlineData("fr--CA")]
    [InlineData("pt-PT")]
    [InlineData("pt-BR-extra")]
    [InlineData("zh-Hant")]
    [InlineData("zh-Hans-extra")]
    public void UnknownLanguageFallsBackToSafeEnglishFrame(string? language)
    {
        HtmlExportLocalization localization = new(language);

        Assert.Equal("en", localization.Language);
        Assert.Equal("Export result", localization.Html(HtmlExportText.ExportResult));
    }

    [Theory]
    [InlineData(" CS_cz ", "cs")]
    [InlineData("IT-it", "it")]
    [InlineData("ja_JP", "ja")]
    [InlineData("KO-kr", "ko")]
    [InlineData("pl-PL", "pl")]
    [InlineData("PT_br", "pt-BR")]
    [InlineData("zh_hans", "zh-Hans")]
    public void SupportedLanguageTagsAreNormalizedToExportCodes(string language, string expectedCode)
    {
        HtmlExportLocalization localization = new(language);

        Assert.Equal(expectedCode, localization.Language);
        Assert.NotEqual("Export result", localization.Get(HtmlExportText.ExportResult));
    }

    [Fact]
    public void AdditionalTranslationsCoverEveryFrameKeyExactlyOnce()
    {
        HtmlExportText[] keys = Enum.GetValues<HtmlExportText>();
        string[] languages = ["cs", "it", "ja", "ko", "pl", "pt-BR", "zh-Hans"];

        foreach (string language in languages)
        {
            Assert.True(HtmlExportLocalization.AdditionalTranslations.TryGetValue(language, out IReadOnlyDictionary<HtmlExportText, string>? translation));
            Assert.NotNull(translation);
            Assert.Equal(keys.Length, translation!.Count);
            foreach (HtmlExportText key in keys)
                Assert.True(translation.ContainsKey(key), $"{language} is missing {key}");
        }
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
    [InlineData("cs")]
    [InlineData("it")]
    [InlineData("ja")]
    [InlineData("ko")]
    [InlineData("pl")]
    [InlineData("pt-BR")]
    [InlineData("zh-Hans")]
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
    public void FrameFormattingUsesInvariantCulture()
    {
        CultureInfo original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            HtmlExportLocalization localization = new("it");

            Assert.Equal(WebUtility.HtmlEncode("Dimensione: 1.5 byte"), localization.Html(HtmlExportText.Size, 1.5));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
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
