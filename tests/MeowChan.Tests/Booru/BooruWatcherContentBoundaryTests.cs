using MeowChan.Archive;
using MeowChan.Booru;
using TempDirectory = MeowChan.Tests.TempDirectory;

namespace MeowChan.Tests.Booru;

public sealed class BooruWatcherContentBoundaryTests
{
    [Fact]
    public async Task CancellationAfterAHiddenClaimReplaysTheRestOfThePage()
    {
        using TempDirectory directory = new();
        await using SchedulerProfile profile = new(directory.Path);
        SchedulerClock clock = new(new DateTimeOffset(2026, 11, 27, 10, 0, 0, TimeSpan.Zero));
        DanbooruWatcherSchedulerOptions options = new() { CheckStartSpacing = TimeSpan.Zero };
        FakeDanbooruSearch search = new(clock);
        using CancellationTokenSource cancel = new();
        CancellingAdmission admission = new(cancel.Cancel);
        await using DanbooruWatcherScheduler scheduler = new(profile.Catalog, search, admission, clock, options);
        scheduler.Load();
        Assert.True(scheduler.AddWatcher("cat").Accepted);
        search.Publish("cat", 41);
        Assert.Equal(DanbooruWatcherCheckKind.BaselineEstablished, (await scheduler.RunNextAsync())!.Kind);

        clock.Advance(options.Interval);
        search.Publish("cat", 43, 42, 41);
        DanbooruWatcherCheckSummary stopped = (await scheduler.RunNextAsync(cancel.Token))!;
        Assert.Equal(DanbooruWatcherCheckKind.Cancelled, stopped.Kind);
        Assert.Empty(admission.Delivered);
        Assert.Equal("41", Assert.Single(profile.Catalog.Read().Watchers).HighWaterMark);

        DanbooruWatcherCheckSummary replayed = (await scheduler.RunNextAsync())!;
        Assert.Equal(DanbooruWatcherCheckKind.Completed, replayed.Kind);
        Assert.Equal(1, replayed.HiddenCount);
        Assert.Equal("43", Assert.Single(admission.Delivered));
    }

    private sealed class CancellingAdmission(Action cancel) : IDanbooruWatcherAdmissionPort
    {
        internal List<string> Delivered { get; } = new();

        public Task<DanbooruWatcherAdmissionResult> AdmitAsync(BooruPostClaim post, string watcherId, string query,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (post.PostId == "42")
            {
                cancel();
                return Task.FromResult(DanbooruWatcherAdmissionResult.ContentBlocked);
            }
            Delivered.Add(post.PostId);
            return Task.FromResult(DanbooruWatcherAdmissionResult.Queued);
        }
    }

    [Fact]
    public async Task HiddenClaimsAreCountedAndTheNextCycleStillAdmitsAllowedPosts()
    {
        using TempDirectory directory = new();
        using SchedulerCdn cdn = new();
        using HttpClient client = new(cdn, disposeHandler: false);
        await using SchedulerProfile profile = new(directory.Path, withQueue: true, cdn);
        SchedulerClock clock = new(new DateTimeOffset(2026, 11, 27, 10, 0, 0, TimeSpan.Zero));
        DanbooruWatcherSchedulerOptions options = new() { CheckStartSpacing = TimeSpan.Zero };
        BooruContentFilter filter = new(1, new(BooruPostClaimRating.Sensitive, "blocked_tag", []));
        BooruOriginalQueue originals = new(profile.Queue!, profile.Catalog, profile.OutputRoot, client,
            allowsContent: claim => filter.Evaluate(claim).IsAllowed);
        FakeDanbooruSearch search = new(clock)
        {
            ClaimFactory = id =>
            {
                BooruPostClaim post = cdn.Claim(id);
                return post with
                {
                    Fields = post.Fields | BooruPostClaimPresence.Rating,
                    Rating = id == 42 ? BooruPostClaimRating.Explicit : BooruPostClaimRating.General,
                    Tags = id == 43 ? [new("blocked_tag", BooruPostClaimTagCategory.General)] : post.Tags
                };
            }
        };
        await using DanbooruWatcherScheduler scheduler = new(profile.Catalog, search,
            new DanbooruWatcherQueueAdmission(originals, profile.Catalog, clock), clock, options);
        scheduler.Load();
        Assert.True(scheduler.AddWatcher("cat").Accepted);
        search.Publish("cat", 41);
        Assert.Equal(DanbooruWatcherCheckKind.BaselineEstablished, (await scheduler.RunNextAsync())!.Kind);

        clock.Advance(options.Interval);
        search.Publish("cat", 43, 42, 41);
        DanbooruWatcherCheckSummary blocked = (await scheduler.RunNextAsync())!;
        Assert.Equal(DanbooruWatcherCheckKind.Completed, blocked.Kind);
        Assert.Equal(2, blocked.HiddenCount);
        Assert.Equal(0, blocked.Queued);
        Assert.Empty(profile.Catalog.Read().Posts);
        Assert.Empty(cdn.Requests);

        clock.Advance(options.Interval);
        search.Publish("cat", 44, 43, 42, 41);
        DanbooruWatcherCheckSummary next = (await scheduler.RunNextAsync())!;
        Assert.Equal(0, next.HiddenCount);
        Assert.Equal(1, next.Queued);
        await TestWait.UntilAsync(() => profile.Catalog.Read().Posts.SingleOrDefault()?.LocalAssetId != null);
        Assert.Equal(SchedulerCdn.OriginalUrl("44"), Assert.Single(cdn.Requests));
    }
}
