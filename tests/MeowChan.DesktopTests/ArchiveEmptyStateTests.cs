using MeowChan.Archive;
using MeowChan.Interfaces;

namespace MeowChan.DesktopTests;

public sealed class ArchiveEmptyStateTests
{
    [Fact]
    public void SortingAloneDoesNotTurnAnEmptyCollectionIntoAFilteredResult()
    {
        foreach (ArchiveThreadSort sort in Enum.GetValues<ArchiveThreadSort>())
            Assert.False(ArchivePage.HasArchiveConstraints(new ArchiveThreadQuery(Sort: sort), 0));
        Assert.False(ArchivePage.HasArchiveConstraints(new ArchiveThreadQuery(Text: " \t "), 0));
    }

    [Fact]
    public void EveryRestrictiveAppliedInputExplainsNullResultsAsFiltered()
    {
        ArchiveThreadQuery[] queries =
        {
            new(Text: "needle"), new(Source: "source"), new(Board: "board"),
            new(SourceState: ThreadSourceState.NotFound), new(CaptureEnabled: false), new(HasPosts: false)
        };
        Assert.All(queries, query => Assert.True(ArchivePage.HasArchiveConstraints(query, 0)));
        foreach (int workFilter in Enumerable.Range(1, 5))
            Assert.True(ArchivePage.HasArchiveConstraints(new ArchiveThreadQuery(), workFilter));
    }
}
