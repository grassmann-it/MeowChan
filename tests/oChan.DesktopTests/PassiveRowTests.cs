using System.Collections.ObjectModel;
using oChan.Desktop;
using oChan.Downloader;
using oChan.Interfaces;

namespace oChan.DesktopTests;

public sealed class PassiveRowTests
{
    [Fact]
    public void ThreadRowUpdatesInPlaceFromPassiveStateAndRetainsNumericCounts()
    {
        ThreadSnapshot initial = Thread("123", "Loading");
        using ThreadRow row = new ThreadRow(new DesktopThreadEntry(17, initial, "4chan", "/g/"));
        int notifications = 0;
        row.PropertyChanged += (_, _) => notifications++;
        FailureInfo failure = new FailureInfo(FailureKind.Storage, "Fixture disk failure");
        ThreadSnapshot changed = initial with
        {
            Title = "Loaded title",
            KnownMedia = 12,
            CompletedMedia = 3,
            SourceState = ThreadSourceState.Archived,
            Downloads = new DownloadCounts(4, 2),
            MediaFailures = new ReadOnlyDictionary<string, FailureInfo>(new Dictionary<string, FailureInfo> { ["m1"] = failure })
        };

        row.UpdateSnapshot(new DesktopThreadEntry(17, changed, "4chan", "/g/ - Technology"));

        Assert.Equal(17, row.WatchId);
        Assert.Equal(123L, row.ThreadNumber);
        Assert.Equal("Loaded title", row.Title);
        Assert.Equal("/g/ - Technology", row.BoardName);
        Assert.Equal(25, row.Completion);
        Assert.Equal(12, row.KnownMedia);
        Assert.Equal(3, row.CompletedMedia);
        Assert.Equal(4, row.Queued);
        Assert.Equal(2, row.Active);
        Assert.Equal(ThreadSourceState.Archived, row.SourceState);
        Assert.Contains("Fixture disk failure", row.ErrorDetails);
        Assert.Equal(initial.Url, row.Url);
        Assert.Equal(initial.DownloadDirectory, row.DownloadDirectory);
        Assert.Equal(1, notifications);
        Assert.Null(typeof(ThreadRow).GetProperty("Source"));
    }

    [Fact]
    public void RowsCannotBeRetargetedToAnotherWatchAndIgnoreLateUpdatesAfterDisposal()
    {
        ThreadSnapshot thread = Thread("123", "Original");
        ThreadRow row = new ThreadRow(new DesktopThreadEntry(1, thread, "4chan", "g"));
        Assert.Throws<ArgumentException>(() => row.UpdateSnapshot(new DesktopThreadEntry(2, thread, "4chan", "g")));
        row.Dispose();
        row.UpdateSnapshot(new DesktopThreadEntry(1, thread with { Title = "Late update" }, "4chan", "g"));
        Assert.Equal("Original", row.Title);

        BoardSnapshot board = new BoardSnapshot(new ResourceId("4chan", "g"), "https://boards.4chan.org/g/", "Technology", "/assigned/4chan/g", null, null);
        using BoardRow boardRow = new BoardRow(new DesktopBoardEntry(3, board, "4chan", "/g/ - Technology"));
        Assert.Throws<ArgumentException>(() => boardRow.UpdateSnapshot(new DesktopBoardEntry(4, board, "4chan", "g")));
        Assert.Equal("g", boardRow.BoardCode);
        Assert.Equal(board.DownloadDirectory, boardRow.DownloadDirectory);
        Assert.Null(typeof(BoardRow).GetProperty("Source"));
    }

    [Fact]
    public void ExcludedMediaDoNotDiluteSelectedProgressOrPretendToBeSaved()
    {
        ThreadSnapshot snapshot = Thread("123", "Filtered") with
        {
            KnownMedia = 5, CompletedMedia = 2,
            Content = new oChan.Archive.ThreadContentStatus(true, null, null, null, 3)
        };
        using ThreadRow row = new ThreadRow(new DesktopThreadEntry(1, snapshot, "4chan", "g"));
        Assert.Equal(5, row.KnownMedia);
        Assert.Equal(2, row.CompletedMedia);
        Assert.Equal(100, row.Completion);
        Assert.Equal(2, row.Snapshot.SelectedMedia);
        row.UpdateSnapshot(new DesktopThreadEntry(1, snapshot with { CompletedMedia = 0,
            Content = snapshot.Content with { ExcludedMedia = 5 } }, "4chan", "g"));
        Assert.Equal(0, row.Completion);
        Assert.Equal(0, row.CompletedMedia);
    }

    private static ThreadSnapshot Thread(string id, string title) => new ThreadSnapshot(
        new ResourceId("4chan", "g", id), "https://boards.4chan.org/g/thread/" + id, title,
        "/assigned/4chan/g/" + id, ThreadActivity.Pending, ThreadSourceState.Active, null, 0, 0,
        new ReadOnlyDictionary<string, FailureInfo>(new Dictionary<string, FailureInfo>()), null, new DownloadCounts(0, 0));
}
