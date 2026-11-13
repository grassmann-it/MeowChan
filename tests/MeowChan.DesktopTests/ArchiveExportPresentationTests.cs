using System.Globalization;
using MeowChan.Archive;
using MeowChan.Desktop;

namespace MeowChan.DesktopTests;

public sealed class ArchiveExportPresentationTests
{
    [Fact]
    public void FreshFolderSuggestionNeverReusesAnExistingDirectoryOrFile()
    {
        using TempDirectory directory = new();
        ResourceId id = new("4chan", "g", "123");
        string first = ArchivePage.FreshExportDestination(directory.Path, id);
        Directory.CreateDirectory(first);
        string second = ArchivePage.FreshExportDestination(directory.Path, id);
        Assert.NotEqual(first, second);
        File.WriteAllText(second, "unrelated file");
        string third = ArchivePage.FreshExportDestination(directory.Path, id);
        Assert.NotEqual(first, third);
        Assert.NotEqual(second, third);
        Assert.Equal(directory.Path, Path.GetDirectoryName(third));
    }

    // th-TH counts years in the Buddhist era and ar-SA in the Hijri calendar; file names and the ISO date shown for
    // a saved search must not follow the operating system's calendar.
    [Theory]
    [InlineData("th-TH")]
    [InlineData("ar-SA")]
    public void ExportFolderNamesAndSavedSearchDatesIgnoreTheOperatingSystemCalendar(string osCulture)
    {
        CultureInfo original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(osCulture);
            using TempDirectory directory = new();
            int year = DateTime.Now.Year;
            string single = Path.GetFileName(ArchivePage.FreshExportDestination(directory.Path, new ResourceId("4chan", "g", "123")));
            string collection = Path.GetFileName(ArchivePage.FreshCollectionExportDestination(directory.Path, 3));
            Assert.Matches(@"^meowchan-4chan-g-123-\d{8}-\d{6}$", single);
            Assert.Matches(@"^meowchan-collection-3-\d{8}-\d{6}$", collection);
            Assert.InRange(int.Parse(single.AsSpan(single.Length - 15, 4), CultureInfo.InvariantCulture), year - 1, year + 1);
            Assert.InRange(int.Parse(collection.AsSpan(collection.Length - 15, 4), CultureInfo.InvariantCulture), year - 1, year + 1);

            ArchiveSearchSpec spec = new(dateRange: new ArchiveDateRange(new DateOnly(2026, 3, 29), new DateOnly(2026, 4, 2), "Europe/Berlin"));
            Assert.Contains("2026-03-29 – 2026-04-02", SavedArchiveSearchDescription.Describe(spec));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }
}
