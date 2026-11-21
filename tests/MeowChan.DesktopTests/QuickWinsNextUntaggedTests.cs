using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

// Quick wins 1.1.1 (QW1, QW4): the catalog entry of "next untagged medium" (no default key, Media page) and the wording of the
// empty Booru page that must not claim the source is empty.
[Collection("Localization")]
public sealed class QuickWinsNextUntaggedTests : IDisposable
{
    public QuickWinsNextUntaggedTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage("en"); }

    [Fact]
    public void NextUntaggedIsACatalogCommandOfTheMediaPageWithoutDefaultKey()
    {
        CommandDefinition command = CommandCatalog.Get("media.next-untagged");
        Assert.Null(command.Shortcut);
        Assert.Empty(command.DefaultGestures);
        Assert.Contains("media-page", command.Contexts);
        Assert.True(command.IsImplemented);
        Assert.Equal("Next untagged medium", command.Label);
        Texts.SetLanguage("de");
        Assert.Equal("Nächstes Medium ohne Tags", command.Label);
    }

    [Theory]
    [InlineData("en", "pages loaded here")]
    [InlineData("de", "hier geladenen Seiten")]
    public void EmptyBooruPageSaysOnlyLoadedPagesWereChecked(string language, string phrase)
    {
        Texts.SetLanguage(language);
        string text = Texts.T("booru.ui.status.empty", "cat", 2);
        Assert.Contains("cat", text);
        Assert.Contains("2", text);
        Assert.Contains(language == "en" ? "Only the pages loaded here" : phrase, text);
    }
}
