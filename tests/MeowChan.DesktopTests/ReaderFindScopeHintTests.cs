using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

/// <summary>
/// RD-02: Primary+F finds in the thread with the focus inside the reader (embedded or large) and searches the archive list elsewhere.
/// The key of the reader's Find button is therefore true only "with the focus in the reader": the tooltip says so on its second line.
/// </summary>
[Collection("Localization")]
public sealed class ReaderFindScopeHintTests
{
    // Texts and the shortcut display are process-global: run serially with the other language tests, in English.
    public ReaderFindScopeHintTests() => Texts.SetLanguage("en");

    [Theory]
    [InlineData("en", "With the focus in the reader")]
    [InlineData("de", "Mit Fokus im Reader")]
    [InlineData("es", "Con el foco en el lector")]
    [InlineData("fr", "Avec le focus dans le lecteur")]
    [InlineData("ru", "Когда фокус в читалке")]
    public void TheFindButtonsTooltipNamesTheKeyOnTheFirstLineAndItsScopeOnTheSecond(string language, string scope)
    {
        Texts.SetLanguage(language);
        try
        {
            string label = CommandCatalog.Label("reader.find");
            string? shortcut = CommandCatalog.ShortcutTooltipDisplay("reader.find");
            Assert.Equal(scope, CommandCatalog.ScopeHint("reader.find"));
            Assert.Equal(label + (shortcut is null ? "" : " · " + shortcut) + "\n" + scope, CommandBindings.Tooltip("reader.find"));
        }
        finally { Texts.SetLanguage("en"); }
    }

    [Fact]
    public void OnlyTheReaderFindCommandCarriesThatHintAndTheSearchHintStaysTheArchives()
    {
        Assert.Null(CommandCatalog.ScopeHint("reader.find-next"));
        Assert.Null(CommandCatalog.ScopeHint("reader.find-previous"));
        Assert.Equal("Archive · search results", CommandCatalog.ScopeHint("input.focus-search"));
    }
}
