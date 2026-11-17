using MeowChan.Desktop;

namespace MeowChan.DesktopTests;

// UX-LEDGER-26 supersedes the old frame-to-posts arithmetic tests: it was that
// arithmetic which capped the media. Measured geometry, resizing and neighbour
// independence now live in ReaderMediaSidebarLayoutTests, not in a mock layout.
public sealed class ReaderSidebarAlignmentTests
{
    private static string Source(string relative, [System.Runtime.CompilerServices.CallerFilePath] string sourcePath = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "../..", relative)));

    [Fact]
    public void FullHeightSidebarKeepsTheStableHeaderAndPostsColumnIsolation()
    {
        Assert.Equal(32, ReaderMediaSidebar.HeaderLineHeight);
        Assert.Equal(8, ReaderMediaSidebar.HeaderGap);
        Assert.Equal(MediaPage.PanelHeaderLineHeight, ReaderMediaSidebar.HeaderLineHeight);
        string host = Source("Desktop/Views/Archive/ArchivePage.MediaSidebar.cs");
        Assert.Contains("sidebar.HeaderHeight = ReaderMediaSidebar.HeaderLineHeight;", host);
        Assert.Contains("Grid.SetRow(sidebar, 0);", host);
        Assert.Contains("Grid.SetRowSpan(sidebar, archiveView ? 2 : 1);", host);
        Assert.Contains("Grid.SetRowSpan(splitter, archiveView ? 2 : 1);", host);
        Assert.DoesNotContain("AlignMediaSidebarFrame", host);
        Assert.DoesNotContain("sidebar.Classes.Add(\"workspace-section-flush\")", host);
        Assert.DoesNotContain("Grid.SetColumnSpan(child, 3)", host);
        Assert.Contains("Grid.SetColumn(headerRows, postsColumn)", host);
    }
}
