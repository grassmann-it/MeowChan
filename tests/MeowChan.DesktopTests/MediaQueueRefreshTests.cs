using MeowChan.Tags;

namespace MeowChan.DesktopTests;

// M2: the session ticks all the time (downloads), and every tick asks the page to read the tagging queue again. While the queue file
// is unreadable the page must not refresh its badges and status line (and, with the queue term, the whole grid) for every tick: only a real
// change of state does. Constructed MediaPage without a window, profile or archive data.
[Collection("Booru exact navigation")]
public sealed class MediaQueueRefreshTests
{
    private static TagQueueSnapshot Snapshot(long revision) => new(revision, 0, [], [], TagQueueLoadState.Ready);

    [Fact]
    public void AnUnreadableQueueIsAppliedOnceNoMatterHowOftenTheSessionTicks() => BooruComponentDispatcher.Run(() =>
    {
        MediaPage page = new(_ => throw new InvalidOperationException("The queue fixture must not open archive data."));
        try
        {
            bool unreadable = true;
            TagQueueSnapshot? snapshot = null;
            page.ReadTagQueue = () => unreadable ? throw new InvalidDataException("synthetic damaged queue") : snapshot;

            Assert.Equal(0, page.QueueChangesApplied);
            for (int tick = 0; tick < 20; tick++) page.RefreshTagQueue();
            Assert.Equal(1, page.QueueChangesApplied); // the change to "unreadable", once
            Assert.Equal(0, page.QueueOpenCount);

            // Readable again (a snapshot, then the very same one on every tick): one more change, then nothing.
            unreadable = false;
            snapshot = Snapshot(1);
            for (int tick = 0; tick < 20; tick++) page.RefreshTagQueue();
            Assert.Equal(2, page.QueueChangesApplied);

            // A new snapshot is a change; unreadable again is one more, once.
            snapshot = Snapshot(2);
            page.RefreshTagQueue();
            Assert.Equal(3, page.QueueChangesApplied);
            unreadable = true;
            for (int tick = 0; tick < 20; tick++) page.RefreshTagQueue();
            Assert.Equal(4, page.QueueChangesApplied);

            // Another kind of failure of the same unreadable queue is no new state either.
            page.ReadTagQueue = () => throw new IOException("synthetic locked queue");
            for (int tick = 0; tick < 5; tick++) page.RefreshTagQueue();
            Assert.Equal(4, page.QueueChangesApplied);
        }
        finally { page.EndLifetime(); }
    });

    [Fact]
    public void AnEmptyQueueThatStaysUnloadedChangesNothing() => BooruComponentDispatcher.Run(() =>
    {
        MediaPage page = new(_ => throw new InvalidOperationException("The queue fixture must not open archive data."));
        try
        {
            page.ReadTagQueue = () => null; // not loaded yet
            for (int tick = 0; tick < 5; tick++) page.RefreshTagQueue();
            Assert.Equal(0, page.QueueChangesApplied);
        }
        finally { page.EndLifetime(); }
    });
}
