using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

// Release 1.1.1 quick wins U04/U09/U05: the labels exist in every language and the wiring is by source contract
// (the pages need Avalonia's asset loader, which this host does not start; the looks are the Xvfb smokes).
[Collection("Localization")]
public sealed class QuickWinArchiveQueueTests : IDisposable
{
    public QuickWinArchiveQueueTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage("en"); }

    private static string Source(string relative, [System.Runtime.CompilerServices.CallerFilePath] string sourcePath = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "../..", relative)));

    private static void EveryLanguageHas(params string[] keys)
    {
        foreach (string code in Texts.BundledPackCodes().Append("en").Append("de"))
        {
            Texts.SetLanguage(code);
            foreach (string key in keys)
                Assert.False(string.IsNullOrWhiteSpace(Texts.T(key)) || Texts.T(key) == key, code + " lacks " + key);
        }
    }

    [Fact]
    public void TheArchiveFilterSurfaceNamesItsApplyActionAndShowsTheUnappliedDraft()
    {
        EveryLanguageHas("archive.applyFilters", "archive.filterDraftNote");
        string axaml = Source("Desktop/Views/Archive/ArchivePage.axaml");
        Assert.Contains("x:Name=\"ArchiveFilterApply\" Grid.Row=\"1\" Tag=\"archive.applyFilters\"", axaml);
        Assert.Contains("x:Name=\"ArchiveFilterDraftNote\"", axaml);
        Assert.Contains("ArchiveFilterDraftNote.IsVisible = draft;", Source("Desktop/Views/Archive/ArchivePage.Presentation.cs"));
        Texts.SetLanguage("en");
        Assert.Equal("Apply filters", Texts.T("archive.applyFilters"));
        Texts.SetLanguage("de");
        Assert.Equal("Filter anwenden", Texts.T("archive.applyFilters"));
    }
}
