using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Downloader;
using MeowChan.Interfaces;
using MeowChan.Persistence;

namespace MeowChan.DesktopTests;

[CollectionDefinition("Row localization", DisableParallelization = true)]
public sealed class RowLocalizationCollection { }

[Collection("Row localization")]
public sealed class RowRefreshTests
{
    [Fact]
    public void EquivalentCopiedThreadCapturesDoNotInvalidateBindingsButChangesDo()
    {
        DesktopThreadEntry entry = ThreadEntry();
        using ThreadRow row = new(entry);
        int invalidations = 0;
        row.PropertyChanged += (_, _) => invalidations++;
        ThreadSnapshot copy = entry.Snapshot with
        {
            MediaFailures = new Dictionary<string, FailureInfo>(entry.Snapshot.MediaFailures),
            Work = entry.Snapshot.Work! with { Media = entry.Snapshot.Work.Media.Select(media => media with { }).ToArray() }
        };
        row.UpdateSnapshot(entry with { Snapshot = copy });
        row.UpdateSnapshot(entry with { Snapshot = copy });
        Assert.Equal(0, invalidations);
        Assert.Same(copy, row.Snapshot);

        ThreadSnapshot completed = copy with
        {
            Work = copy.Work! with { Media = copy.Work.Media.Select(media => media with { Status = MediaWorkStatus.Succeeded }).ToArray() },
            Downloads = new DownloadCounts(0, 0)
        };
        row.UpdateSnapshot(entry with { Snapshot = completed });
        Assert.Equal(1, invalidations);
        Assert.Equal(1, row.CompletedMedia);
        Assert.Equal(100, row.Completion);
        Assert.Equal(0, row.Active);
        row.UpdateSnapshot(entry with { Snapshot = completed, HostName = "Renamed source" });
        Assert.Equal(2, invalidations);
        Assert.Equal("Renamed source", row.HostName);

        row.UpdateSnapshot(entry with { Snapshot = completed with { MediaFailures = new Dictionary<string, FailureInfo>
        { ["image"] = new(FailureKind.Storage, "New failure") } }, HostName = "Renamed source" });
        Assert.Equal(3, invalidations);
        Assert.Contains("New failure", row.ErrorDetails);
    }

    [Fact]
    public void EquivalentBoardCapturesDoNotInvalidateBindingsButChangesDo()
    {
        DesktopBoardEntry entry = BoardEntry();
        using BoardRow row = new(entry);
        int invalidations = 0;
        row.PropertyChanged += (_, _) => invalidations++;
        BoardSnapshot copy = entry.Snapshot with { Id = entry.Snapshot.Id with { } };
        row.UpdateSnapshot(entry with { Snapshot = copy });
        Assert.Equal(0, invalidations);
        Assert.Same(copy, row.Snapshot);
        row.UpdateSnapshot(entry with { Snapshot = copy with { LastCheckFailure = new(FailureKind.Network, "Offline") } });
        Assert.Equal(1, invalidations);
        Assert.Equal("Offline", row.ErrorDetails);
        row.UpdateSnapshot(entry with { BoardName = "Renamed board" });
        Assert.Equal(2, invalidations);
        Assert.Equal("Renamed board", row.NiceName);
    }

    [Fact]
    public void PauseLanguageChangeAndCatalogReloadStillRefreshPresentations()
    {
        string previousLanguage = Texts.Language;
        try
        {
            Texts.SetLanguage("en");
            DesktopThreadEntry thread = ThreadEntry();
            DesktopBoardEntry board = BoardEntry();
            using ThreadRow threadRow = new(thread);
            using BoardRow boardRow = new(board);
            int threadChanges = 0, boardChanges = 0;
            threadRow.PropertyChanged += (_, _) => threadChanges++;
            boardRow.PropertyChanged += (_, _) => boardChanges++;
            string active = threadRow.Status;
            threadRow.SetPaused(true);
            Assert.NotEqual(active, threadRow.Status);
            Assert.Equal(1, threadChanges);
            threadRow.UpdateSnapshot(thread);
            threadRow.SetPaused(true);
            Assert.Equal(1, threadChanges);
            threadRow.SetPaused(false);
            Assert.Equal(active, threadRow.Status);
            Assert.Equal(2, threadChanges);

            string englishBoardStatus = boardRow.Status;
            Texts.SetLanguage("de");
            threadRow.UpdateSnapshot(thread);
            boardRow.UpdateSnapshot(board);
            Assert.Equal(3, threadChanges);
            Assert.Equal(1, boardChanges);
            Assert.NotEqual(active, threadRow.Status);
            Assert.NotEqual(englishBoardStatus, boardRow.Status);

            // Initialize can replace a catalog while keeping the language code.
            Texts.Initialize();
            threadRow.UpdateSnapshot(thread);
            boardRow.UpdateSnapshot(board);
            Assert.Equal(4, threadChanges);
            Assert.Equal(2, boardChanges);
            threadRow.UpdateSnapshot(thread);
            boardRow.UpdateSnapshot(board);
            Assert.Equal(4, threadChanges);
            Assert.Equal(2, boardChanges);
        }
        finally { Texts.SetLanguage(previousLanguage); }
    }

    private static DesktopThreadEntry ThreadEntry()
    {
        const string url = "https://fixture.invalid/g/thread/123";
        MediaWork media = new() { ResourceUrl = url, Generation = 1, MediaIdentifier = "image",
            DestinationPath = "/never/read/image.jpg", DownloadUrl = "https://fixture.invalid/image.jpg",
            Status = MediaWorkStatus.Ready };
        return new(1, new ThreadSnapshot(new ResourceId("fixture", "g", "123"), url, "Title", "/never/read",
            ThreadActivity.Downloading, ThreadSourceState.Active, null, 1, 0,
            new Dictionary<string, FailureInfo>(), null, new DownloadCounts(0, 1))
        {
            Work = new ThreadWorkSnapshot(url, WorkIntent.Watching, ThreadSourceStateValue.Active, null, false, new[] { media })
        }, "Fixture", "Technology");
    }

    private static DesktopBoardEntry BoardEntry() => new(2,
        new BoardSnapshot(new ResourceId("fixture", "g"), "https://fixture.invalid/g/", "Technology", "/never/read", null, null),
        "Fixture", "Technology");
}
