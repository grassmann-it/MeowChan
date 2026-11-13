using MeowChan.Booru;
using MeowChan.Desktop.Booru;

namespace MeowChan.DesktopTests;

public sealed class BooruWatcherPersistenceAdmissionTests
{
    [Fact]
    public void WatcherPortKeepsPersistenceFailureCodeForTheUserFacingResult()
    {
        DateTimeOffset now = new(2026, 11, 13, 12, 0, 0, TimeSpan.Zero);
        DanbooruWatcherCheckSummary summary = new(
            DanbooruWatcherCheckKind.Failed, now, now, PagesRead: 2, NewPosts: 1, Queued: 0,
            AlreadyQueuedOrLinked: 0, Unavailable: 0, NotAdmitted: 0, KeptCancelled: 0,
            Code: BooruCodes.PersistFailed, Manual: true);

        BooruWatcherCommandResult result = BooruSchedulerWatcherPort.Check(summary);

        Assert.Equal(BooruWatcherCommandStatus.Failed, result.Status);
        Assert.Equal(BooruCodes.PersistFailed, result.Code);
    }
}
