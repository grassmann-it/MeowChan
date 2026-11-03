using MeowChan.Desktop;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

[Collection("Localization")]
public sealed class AboutActionErrorLocalizationTests : IDisposable
{
    private readonly string _language = Texts.Language;

    public AboutActionErrorLocalizationTests() => Texts.Initialize();
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage(_language); }

    [Theory]
    [InlineData(null)]
    [InlineData("https://synthetic.invalid/project")]
    public void ErrorWrapperRerendersWithoutTranslatingPlatformDetails(string? link)
    {
        const string details = "unchanged synthetic platform detail ü";
        AboutActionError error = new(link, details);
        foreach (string language in new[] { "en", "de", "es", "fr", "ru", "en" })
        {
            Texts.SetLanguage(language);
            Assert.Equal(link is null ? Texts.T("about.copyFailed", details)
                : Texts.T("about.linkFailed", link, details), error.Render());
            Assert.Contains(details, error.Render());
            Assert.Equal(link, error.Link);
            Assert.Equal(details, error.Details);
        }
    }

    [Fact]
    public void ReplacingLinkErrorWithCopyErrorCannotResurrectThePreviousLink()
    {
        AboutActionError? current = new("https://synthetic.invalid/old", "old link detail");
        current = new(null, "latest copy detail");
        Texts.SetLanguage("fr");
        Assert.Equal(Texts.T("about.copyFailed", "latest copy detail"), current.Render());
        Assert.DoesNotContain("old link detail", current.Render());
        current = null;
        Assert.Equal(string.Empty, current?.Render() ?? string.Empty);
    }
}
