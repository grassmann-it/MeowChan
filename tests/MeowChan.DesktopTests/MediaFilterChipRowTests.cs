using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

// C3: every active media filter is a removable chip, the toggle counts them and "Reset all" clears them.
[Collection("Localization")]
public sealed class MediaFilterChipRowTests : IDisposable
{
    // The page itself needs Avalonia's asset loader (icons), which this test host does not start; the chip row's behaviour
    // (labels per field, removal, Reset all, count, empty-state reset) is driven in the media-page smoke (FilterChipRowAsync).
    // Here: the texts and the source contract that the row is outside the filter surface and covers every field.
    public MediaFilterChipRowTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage("en"); }

    private static string Source(string relative, [System.Runtime.CompilerServices.CallerFilePath] string sourcePath = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "../..", relative)));

    [Fact]
    public void TheChipRowSitsInTheHeaderAndCoversEveryFilterField()
    {
        string page = Source("Desktop/Views/Media/MediaPage.cs"), chips = Source("Desktop/Views/Media/MediaPage.FilterChips.cs");
        // In the header (visible while the surface is closed), not inside the surface built from BuildFilterRow().
        Assert.Contains("header.Children.Add(BuildFilterChipRow());", page);
        Assert.Contains("_emptyReset.IsVisible = _filterActive;", page);
        foreach (string key in new[] { "\"tag+\"", "\"tag-\"", "\"tagMatch\"", "\"thread\"", "\"untagged\"", "\"name\"", "\"kind\"", "\"sound\"",
            "\"source\"", "\"board\"", "\"rating\"", "\"favorites\"" })
            Assert.Contains("new FilterChip(" + key, chips);
        Assert.Contains("ArchivePage.CreateFilterTag(chip.Label", chips); // the archive's chip pattern, styled chip-filter
        Assert.Contains("Texts.T(\"media.filters.count\", count)", Source("Desktop/Views/Media/MediaPage.FilterSurface.cs"));
        Assert.Contains("_filterError.Classes.Add(\"workspace-error\");", chips);
    }

    [Theory]
    [InlineData("en"), InlineData("de"), InlineData("es"), InlineData("fr"), InlineData("ru")]
    public void ChipRowTextsAreTranslatedWithTheirPlaceholders(string language)
    {
        Texts.SetLanguage(language);
        Assert.Contains("7", Texts.T("media.filters.count", 7));
        Assert.Contains("7", Texts.Count("media.filters.countName", 7));
        Assert.Contains("r-6", Texts.T("media.filter.nameChip", "r-6"));
        Assert.Contains("GIF", Texts.T("media.filter.kindChip", "GIF"));
        Assert.Contains("4", Texts.T("media.filter.ratingChip", 4));
        foreach (string key in new[] { "media.filter.resetAll", "media.filter.resetAllHint" })
            Assert.False(string.IsNullOrWhiteSpace(Texts.T(key)) || Texts.T(key) == key, key);
        if (language == "de") Assert.Equal("Alle zurücksetzen", Texts.T("media.filter.resetAll"));
        if (language == "es") Assert.Equal("Filtros, 1 activo", Texts.Count("media.filters.countName", 1));
        if (language == "de") Assert.Equal("Filter (3)", Texts.T("media.filters.count", 3));
    }
}
