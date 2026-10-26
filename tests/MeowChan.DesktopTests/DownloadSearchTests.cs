using MeowChan.Desktop;
using MeowChan.Downloader;
using MeowChan.Interfaces;

namespace MeowChan.DesktopTests;

public sealed class DownloadSearchTests
{
    [Fact]
    public void SearchesTitleBoardIdAndUrlCaseInsensitivelyWithoutASearchLanguage()
    {
        using ThreadRow row = new(new DesktopThreadEntry(1, new ThreadSnapshot(new ResourceId("fixture", "g", "1234"),
            "https://fixture.invalid/g/thread/1234", "Literal [Example]", "/never/read", ThreadActivity.Pending,
            ThreadSourceState.Active, null, 0, 0, new Dictionary<string, FailureInfo>(), null, new DownloadCounts(0, 0)), "Fixture", "Technology"));
        Assert.True(new DownloadSearch.Query("  [EXAMPLE]  ").Matches(row));
        Assert.True(default(DownloadSearch.Query).Matches(row));
        foreach (string query in new[] { "[EXAMPLE]", "technOLOGY", "g", "1234", "FIXTURE.INVALID", "  " })
            Assert.True(DownloadSearch.Matches(row, query));
        Assert.False(DownloadSearch.Matches(row, ".*"));
        Assert.False(DownloadSearch.Matches(row, "unmatched"));
    }

    [Fact]
    public void BoardSnapshotsUseTheSameLiteralQueryAndMissingFieldsAreEmpty()
    {
        using BoardRow row = new(new DesktopBoardEntry(2, new BoardSnapshot(new ResourceId("fixture", "sci"),
            "https://fixture.invalid/sci/", "Science", "/never/read", null, null), "Fixture", ""));
        Assert.True(new DownloadSearch.Query("  SCIENCE  ").Matches(row));
        Assert.True(default(DownloadSearch.Query).Matches(row));
        Assert.True(DownloadSearch.Matches(row, "SCIENCE"));
        Assert.True(DownloadSearch.Matches(row, "sci/"));
        Assert.True(DownloadSearch.Matches(null, null, string.Empty));
        Assert.False(DownloadSearch.Matches("missing", null, string.Empty));
    }
}
