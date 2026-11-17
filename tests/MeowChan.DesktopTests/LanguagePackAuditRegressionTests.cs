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
    public void CooldownControlsAreTranslatedButProductNamesAreNot(string code)
    {
        Texts.SetLanguage(code);
        foreach (string key in new[]
        {
            "settings.unitHours", "budgets.booruRetryAfterCooldown", "budgets.booruRetryAfterCooldownHint",
            "booru.ui.watchers.resetCooldown", "booru.ui.watchers.resetCooldownHint", "booru.ui.watcher.msg.cooldownReset"
        })
        {
            Assert.False(string.IsNullOrWhiteSpace(Texts.T(key)));
            Assert.NotEqual(BuiltInCatalog.English[key], Texts.T(key));
        }
        foreach (string key in new[] { "nav.booru", "command.navigation.booru", "workview.page.booru", "booru.ui.title", "booru.media.source", "booru.media.badge", "detail.url", "search.scope.hash" })
            Assert.Equal(BuiltInCatalog.English[key], Texts.T(key));
        Assert.Empty(Texts.Warnings);
    }

    [Theory]
    [InlineData("ja")]
    [InlineData("ko")]
    [InlineData("zh-Hans")]
    public void EveryOmittedSingularUsesTheTranslatedBaseForAllCounts(string code)
    {
        using Stream stream = typeof(Texts).Assembly.GetManifestResourceStream(Texts.BundledPackResourcePrefix + code + ".json")!;
        using JsonDocument document = JsonDocument.Parse(stream);
        JsonElement strings = document.RootElement.GetProperty("strings");
        Texts.SetLanguage(code);
        string[] singularKeys = BuiltInCatalog.English.Keys.Where(key => key.EndsWith(".one", StringComparison.Ordinal)).ToArray();
        Assert.NotEmpty(singularKeys);
        foreach (string singular in singularKeys)
        {
            string key = singular[..^4];
            Assert.False(strings.TryGetProperty(singular, out _));
            Assert.True(strings.TryGetProperty(key, out JsonElement translation), "Missing translated base: " + key);
            Assert.Equal(translation.GetString(), Texts.T(key));
            foreach (int count in new[] { 0, 1, 2, 4, 21, 100 })
            {
                Assert.Equal(PluralRules.Other, PluralRules.Category(code, count));
                object?[] arguments = Enumerable.Repeat<object?>(count, 10).ToArray();
                Assert.Equal(Texts.T(key, arguments), Texts.Count(key, count, arguments));
            }
        }
        Assert.Empty(Texts.Warnings);
    }

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
