using System.Text.Json;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

[Collection("Localization")]
public sealed class LanguagePackAuditRegressionTests : IDisposable
{
    public LanguagePackAuditRegressionTests() => Texts.Initialize();
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage("en"); }

    [Theory]
    [MemberData(nameof(BundledLanguagePackCompletenessTests.BundledCodes), MemberType = typeof(BundledLanguagePackCompletenessTests))]
    public void NoEditSentinelsRemainEmptyInEveryLanguageWithoutInvalidPackEntries(string code)
    {
        using Stream stream = typeof(Texts).Assembly.GetManifestResourceStream(Texts.BundledPackResourcePrefix + code + ".json")!;
        using JsonDocument document = JsonDocument.Parse(stream);
        Texts.SetLanguage(code);
        foreach (string key in new[] { "savedSearch.confirm.None", "savedSearch.prompt.None" })
        {
            Assert.Equal("", BuiltInCatalog.English[key]);
            Assert.Equal("", BuiltInCatalog.German[key]);
            Assert.False(document.RootElement.GetProperty("strings").TryGetProperty(key, out _));
            Assert.Equal("", Texts.T(key));
            Assert.Equal("", Texts.T(key, "unused editor name"));
        }
        Assert.Empty(Texts.Warnings);
    }
}
