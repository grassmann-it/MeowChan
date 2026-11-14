using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

[Collection("Localization")]
public sealed class HeaderIoIconLocalizationTests : IDisposable
{
    public HeaderIoIconLocalizationTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage("en"); }

    [Theory]
    [InlineData("en")]
    [InlineData("de")]
    [InlineData("es")]
    [InlineData("fr")]
    [InlineData("ru")]
    [InlineData("ja")]
    [InlineData("ko")]
    [InlineData("zh-Hans")]
    [InlineData("cs")]
    [InlineData("pl")]
    [InlineData("pt-BR")]
    [InlineData("it")]
    public void OptionAndAllFourFullDirectionTipsAreTranslated(string language)
    {
        Texts.SetLanguage(language);
        Assert.Equal(language, Texts.Language);
        foreach (string key in new[] { "stats.headerIcons.label", "stats.headerIcons.hint" })
        {
            string text = Texts.T(key);
            Assert.NotEqual(key, text);
            Assert.False(string.IsNullOrWhiteSpace(text));
        }
        HashSet<string> distinct = new(StringComparer.Ordinal);
        foreach (string direction in new[] { "down", "up", "read", "write" })
        {
            string key = "stats.headerIcons." + direction + "Tip";
            string text = Texts.T(key, "1,27 MiB/s");
            Assert.NotEqual(key, text);
            Assert.Contains("1,27 MiB/s", text);
            Assert.DoesNotContain("{0}", text);
            Assert.True(distinct.Add(text), $"{language}: directions need distinct full names.");
        }
    }
}
