using MeowChan.Archive;
using MeowChan.Booru;
using TempDirectory = MeowChan.Tests.TempDirectory;

namespace MeowChan.Tests.Booru;

/// <summary>
/// S1 measurements. The counters are store-instance counts of full catalog reads
/// (read + JSON parse + validation + freeze) and durable rewrites (write-through + flush + replace); no
/// wall-clock values are asserted. Before the snapshot cache and the single-write admission, one explicit
/// admission cost 4 rewrites and 6 full reads, a watcher admission one more read (see the review, S1).
/// </summary>
public sealed partial class BooruOriginalQueueTests
{
    [Fact]
    public async Task OneExplicitAdmissionCostsOneWriteBeforeTheQueueAndNoFullReadOnAWarmCatalog()
    {
        using TempDirectory directory = new();
        FakeCdn cdn = new();
        BooruPostClaim post = Post("71", Bytes(71), cdn);
        await using Session session = new(directory.Path, cdn);
        await session.Queue.SetPausedAsync(true);
        BooruCatalogStoreCounters before = session.Catalog.Counters;

        BooruOriginalAdmission admission = await session.Booru.AdmitAsync(post, BooruFindingKind.OneShot);
        Assert.Equal(BooruOriginalAdmissionStatus.Queued, admission.Status);
        BooruCatalogStoreCounters admitted = session.Catalog.Counters;
        Assert.Equal(0, admitted.FullReads - before.FullReads);
        Assert.Equal(1, admitted.Writes - before.Writes);
        BooruPostRecord record = Assert.Single(session.Catalog.Read().Posts);
        Assert.Equal(BooruOriginalIntent.Requested, record.OriginalIntent);
        Assert.Equal(BooruFindingKind.OneShot, Assert.Single(record.Findings).Kind);

        await session.Queue.SetPausedAsync(false);
        Assert.Equal(BooruOriginalDownloadState.Linked, (await admission.Completion.WaitAsync(TestWait.Timeout)).State);
        BooruCatalogStoreCounters linked = session.Catalog.Counters;
        Assert.Equal(0, linked.FullReads - before.FullReads);
        Assert.Equal(2, linked.Writes - before.Writes);
        File.WriteAllText(Path.Combine(EvidenceDirectory() ?? directory.Path, "s1-admission-counts.txt"),
            $"one-shot admission: writes before queue={admitted.Writes - before.Writes}, writes incl. link={linked.Writes - before.Writes}, " +
            $"full reads={linked.FullReads - before.FullReads}, cached reads={linked.CachedReads - before.CachedReads}; " +
            "before S1: 4 writes, 6 full reads\n");
    }

    [Fact]
    public async Task TwentyWatcherFindingsCostTwentyWritesAndNoFullRead()
    {
        using TempDirectory directory = new();
        FakeCdn cdn = new();
        await using Session session = new(directory.Path, cdn);
        await session.Queue.SetPausedAsync(true);
        DanbooruWatcherQueueAdmission admission = new(session.Booru, session.Catalog);
        BooruCatalogStoreCounters before = session.Catalog.Counters;
        for (int index = 0; index < 20; index++)
        {
            string postId = (900 + index).ToString(System.Globalization.CultureInfo.InvariantCulture);
            DanbooruWatcherAdmissionResult result = await admission.AdmitAsync(Post(postId, Bytes(index), cdn), "w1", "cat",
                CancellationToken.None);
            Assert.Equal(DanbooruWatcherAdmissionResult.Queued, result);
        }
        BooruCatalogStoreCounters after = session.Catalog.Counters;
        Assert.Equal(0, after.FullReads - before.FullReads);
        Assert.Equal(20, after.Writes - before.Writes);
        Assert.All(session.Catalog.Read().Posts, post => Assert.True(post.HasPendingOriginal));
        File.WriteAllText(Path.Combine(EvidenceDirectory() ?? directory.Path, "s1-watcher-page-counts.txt"),
            $"20 watcher findings (paused queue): writes={after.Writes - before.Writes}, full reads={after.FullReads - before.FullReads}, " +
            $"cached reads={after.CachedReads - before.CachedReads}; before S1: 60 writes, 140 full reads\n");
    }

    [Fact]
    public async Task AKeptCancellationStoresClaimAndWatcherFindingInOneWrite()
    {
        using TempDirectory directory = new();
        FakeCdn cdn = new();
        BooruPostClaim post = Post("72", Bytes(72), cdn);
        await using Session session = new(directory.Path, cdn);
        await session.Queue.SetPausedAsync(true);
        BooruOriginalAdmission first = await session.Booru.AdmitAsync(post, BooruFindingKind.OneShot);
        Assert.True(await session.Booru.CancelAsync("72"));
        await first.Completion.WaitAsync(TestWait.Timeout);
        BooruCatalogStoreCounters before = session.Catalog.Counters;

        DanbooruWatcherAdmissionResult result = await new DanbooruWatcherQueueAdmission(session.Booru, session.Catalog)
            .AdmitAsync(post, "w1", "cat", CancellationToken.None);
        Assert.Equal(DanbooruWatcherAdmissionResult.KeptCancelled, result);
        Assert.Equal(1, session.Catalog.Counters.Writes - before.Writes);
        BooruPostRecord record = Assert.Single(session.Catalog.Read().Posts);
        Assert.Equal(BooruOriginalIntent.Cancelled, record.OriginalIntent);
        Assert.Contains(record.Findings, finding => finding.WatcherId == "w1");
    }

    /// <summary>Evidence folder of the coordinated run, when the caller sets one; never required.</summary>
    private static string? EvidenceDirectory() =>
        Environment.GetEnvironmentVariable("MEOWCHAN_BOORU_EVIDENCE_DIR") is { Length: > 0 } path && Directory.Exists(path) ? path : null;
}
