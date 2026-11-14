using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

/// <summary>The sentences the settings search gives a screen reader for the hit reached with the arrow keys and for no match.</summary>
[Collection("Localization")]
public sealed class SettingsSearchAnnouncementTests : IDisposable
{
    public SettingsSearchAnnouncementTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage("en"); }

    [Fact]
    public void CurrentNamesPositionCountLabelAndGroup()
    {
        Assert.Equal("3 of 12: Download folder, Downloads", SettingsSearchAnnouncement.Current(3, 12, "Download folder", "Downloads"));
        Assert.Equal("1 of 1: Language, Interface", SettingsSearchAnnouncement.Current(1, 1, "Language", "Interface"));
    }

    [Fact]
    public void CurrentLeavesOutAnEmptyGroup()
    {
        Assert.Equal("2 of 5: Language", SettingsSearchAnnouncement.Current(2, 5, "Language", ""));
    }

    [Theory]
    [InlineData(0, 5)]
    [InlineData(6, 5)]
    [InlineData(1, 0)]
    public void CurrentIsEmptyOutsideTheList(int position, int count)
    {
        Assert.Equal(string.Empty, SettingsSearchAnnouncement.Current(position, count, "Language", "Interface"));
    }

    [Fact]
    public void NoneNamesTheTrimmedQuery()
    {
        Assert.Equal("No setting matches “zzz”.", SettingsSearchAnnouncement.None("  zzz "));
    }

    [Fact]
    public void GermanUsesItsOwnSentence()
    {
        Texts.SetLanguage("de");
        Assert.Equal("3 von 12: Download-Ordner, Downloads", SettingsSearchAnnouncement.Current(3, 12, "Download-Ordner", "Downloads"));
    }

    [Fact]
    public void EveryBundledLanguageCarriesAllFourValues()
    {
        foreach (string code in Texts.BundledPackCodes())
        {
            Texts.SetLanguage(code);
            string text = SettingsSearchAnnouncement.Current(3, 12, "LABEL", "GROUP");
            Assert.True(text.Contains('3') && text.Contains("12") && text.Contains("LABEL") && text.Contains("GROUP"), code + ": " + text);
        }
    }
}
