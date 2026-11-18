using System.Net;
using System.Security.Cryptography;
using System.Text;
using MeowChan.Archive;
using MeowChan.Booru;
using MeowChan.PluginHost.Http;
using MeowChan.PluginHost.Sources.Booru;
using MeowChan.PluginSdk;

namespace MeowChan.PluginRuntime.Tests;

/// <summary>
/// B9 integrated acceptance gates for the 1.1.0 Danbooru slice (work plan section B9, scenarios 1–10).
/// Each gate drives the whole chain with synthetic data — real plugin parser, real host transport, real
/// claim mapping, real watcher scheduler, real shared download queue, real catalog and media store — and
/// restarts the profile where the scenario asks for it. See <see cref="B9Session"/> for the wiring.
/// These gates do not cover UI rendering (B6/B7) or a real 1.0.2 profile; see review/booru-b9-gates-2026-11-12.md.
/// </summary>
public sealed class BooruB9AcceptanceTests
{
    // ---- Scenario 1–3: search → preview → explicit selection → shared queue ------------------------------

    [Fact]
    public async Task S1_SearchPreviewAndExplicitSelectionQueueOnlyTheChosenOriginalWithoutAHiddenRatingFilter()
    {
        using B9World world = new();
        B9Post sensitive = world.Post(3001, rating: "s");
        world.Post(3002, rating: "g", restricted: true);
        world.Post(3003, rating: "e");
        world.Provider.Publish("cat_ears solo", 3001, 3002, 3003);
        await using B9Session session = new(world);
        await session.StartAsync();

        BooruSearchSnapshot result = Assert.IsType<BooruSearchSnapshot>(await session.SearchAsync("cat_ears solo"));

        // Exactly one bounded provider request with the user's two provider tags and nothing injected.
        B9WireRequest wire = Assert.Single(world.Provider.Requests);
        Assert.Equal("/posts.json", wire.Path);
        Assert.Equal("cat_ears solo", wire.Tags);
        Assert.Equal(1, wire.Page);
        Assert.Equal(20, wire.Limit);
        // Every provider rating comes back; the explicit post is not hidden by a default filter.
        Assert.Equal(["3003", "3002", "3001"], result.Page.Posts.Select(post => post.Id.PostId).ToArray());
        Assert.Equal([BooruRating.Explicit, BooruRating.General, BooruRating.Sensitive],
            result.Page.Posts.Select(post => post.Rating).ToArray());

        // Preview data: preview/sample/original claims, provider rating and provider tags; nothing fetched yet.
        BooruPostClaim preview = BooruClaimMapper.Map(result.Page.Posts.Single(post => post.Id.PostId == "3001"));
        Assert.Equal(BooruPostClaimRating.Sensitive, preview.Rating);
        // Raw rating text is only kept for values outside g/s/q/e.
        Assert.Null(preview.RawRating);
        Assert.Contains(preview.Tags, tag => tag is { Name: "cat_ears", Category: BooruPostClaimTagCategory.General });
        Assert.Contains(preview.Tags, tag => tag is { Name: "artist_b9", Category: BooruPostClaimTagCategory.Artist });
        Assert.Equal(sensitive.PreviewUrl, preview.Media.Single(media => media.Role == BooruPostClaimMediaRole.Preview).Url);
        Assert.Equal(sensitive.OriginalUrl, preview.Media.Single(media => media.Role == BooruPostClaimMediaRole.Original).Url);
        Assert.Empty(world.Cdn.Requests);
        // A search alone stores nothing.
        Assert.Empty(session.Catalog.Read().Posts);

        // The user selects two posts explicitly: one normal, one without an accessible original.
        BooruOriginalAdmission chosen = await session.AdmitAsync(result.Page.Posts.Single(post => post.Id.PostId == "3001"),
            BooruFindingKind.Search, "cat_ears solo");
        BooruOriginalAdmission restricted = await session.AdmitAsync(result.Page.Posts.Single(post => post.Id.PostId == "3002"),
            BooruFindingKind.Search, "cat_ears solo");
        Assert.Equal(BooruOriginalAdmissionStatus.Queued, chosen.Status);
        Assert.Equal(BooruOriginalAdmissionStatus.Unavailable, restricted.Status);
        Assert.NotEqual(BooruOriginalSelectionError.None, restricted.SelectionError);

        BooruOriginalDownloadOutcome linked = await chosen.Completion.WaitAsync(B9Wait.Timeout);
        Assert.Equal(BooruOriginalDownloadState.Linked, linked.State);
        Assert.Equal(sensitive.Sha256Hex, linked.Sha256);
        // Only the selected original was transferred: no sample, no preview, nothing for the restricted post.
        Assert.Equal([sensitive.OriginalUrl], world.Cdn.Requests.ToArray());

        BooruPostRecord record = session.Record("3001");
        Assert.Equal(linked.AssetId, record.LocalAssetId);
        Assert.Equal(BooruOriginalIntent.Linked, record.OriginalIntent);
        BooruFinding finding = Assert.Single(record.Findings);
        Assert.Equal(BooruFindingKind.Search, finding.Kind);
        Assert.Equal("cat_ears solo", finding.Query);
        Assert.Equal(BooruOriginalIntent.Unavailable, session.Record("3002").OriginalIntent);
        Assert.DoesNotContain(session.Catalog.Read().Posts, post => post.PostId == "3003");
        // Provider data stays provider data: no watcher, no private tag store, no chan journal row.
        Assert.Empty(session.Catalog.Read().Watchers);
        Assert.False(File.Exists(Path.Combine(session.ProfileDirectory, MeowChan.Tags.PrivateTagStore.FileName)));
        Assert.Empty(session.Work.LoadResources());
        Assert.Equal(sensitive.Bytes, await B9Wait.ReadVerifiedAssetAsync(world.OutputRoot, "3001", linked.AssetId!));
        world.AssertCleanWire();
    }

    [Fact]
    public async Task S3_OneShotPostLinkLoadsExactlyOneOriginalReportsMissingOnesAndCreatesNoWatcher()
    {
        using B9World world = new();
        B9Post normal = world.Post(4001);
        world.Post(4002, restricted: true);
        await using B9Session session = new(world);
        await session.StartAsync();

        // Link parsing is strict; a malformed link never reaches the provider.
        Assert.False(DanbooruPostReference.TryParse("https://danbooru.donmai.us/posts/4001?q=cat", out _, out _));
        Assert.False(DanbooruPostReference.TryParse("https://danbooru.donmai.us.invalid/posts/4001", out _, out _));
        Assert.True(DanbooruPostReference.TryParse(" https://danbooru.donmai.us/posts/4001 ", out DanbooruPostReference? reference, out _));
        Assert.Equal(0, world.Provider.RequestCount);

        BooruPostSnapshot post = Assert.IsType<BooruPostSnapshot>(await session.ReadPostAsync(reference!.PostId));
        Assert.Equal("/posts/4001.json", Assert.Single(world.Provider.Requests).Path);
        BooruOriginalAdmission admission = await session.AdmitAsync(post.Post, BooruFindingKind.OneShot);
        Assert.Equal(BooruOriginalAdmissionStatus.Queued, admission.Status);
        BooruOriginalDownloadOutcome linked = await admission.Completion.WaitAsync(B9Wait.Timeout);
        Assert.Equal(BooruOriginalDownloadState.Linked, linked.State);
        Assert.Equal(1, world.Cdn.CountFor(normal.OriginalUrl));
        Assert.Single(world.Cdn.Requests);

        // A restricted post is a visible "no original" result, never a sample or preview download.
        BooruPostSnapshot restrictedPost = Assert.IsType<BooruPostSnapshot>(await session.ReadPostAsync("4002"));
        BooruOriginalAdmission restricted = await session.AdmitAsync(restrictedPost.Post, BooruFindingKind.OneShot);
        Assert.Equal(BooruOriginalAdmissionStatus.Unavailable, restricted.Status);
        Assert.Equal(BooruOriginalDownloadState.Unavailable, (await restricted.Completion.WaitAsync(B9Wait.Timeout)).State);
        Assert.Single(world.Cdn.Requests);

        // A missing post is NotFound and stores nothing.
        BooruPostNotFound missing = Assert.IsType<BooruPostNotFound>(await session.ReadPostAsync("4404"));
        Assert.Equal(404, missing.HttpStatusCode);
        Assert.DoesNotContain(session.Catalog.Read().Posts, item => item.PostId == "4404");

        // Asking again for the same post re-reads metadata but never transfers the original twice.
        BooruPostSnapshot again = Assert.IsType<BooruPostSnapshot>(await session.ReadPostAsync("4001"));
        Assert.Equal(BooruOriginalAdmissionStatus.AlreadyLinked, (await session.AdmitAsync(again.Post, BooruFindingKind.OneShot)).Status);
        Assert.Single(world.Cdn.Requests);

        // One-shots never create a watcher.
        Assert.Empty(session.Catalog.Read().Watchers);
        Assert.Empty(session.Scheduler.GetSnapshot().Watchers);
        Assert.All(session.Record("4001").Findings, item => Assert.Equal(BooruFindingKind.OneShot, item.Kind));
        world.AssertCleanWire();
    }

    // ---- Scenario 4: one post from search, one-shot and two watchers → one admission -------------------

    [Fact]
    public async Task S4_SamePostFromSearchOneShotAndTwoWatchersIsTransferredOnceWithFourFindings()
    {
        using B9World world = new();
        B9Post old = world.Post(5000);
        B9Post fresh = world.Post(5001);
        world.Provider.Publish("cat_ears", 5000);
        world.Provider.Publish("solo", 5000);
        await using B9Session session = new(world, queuePaused: true);
        await session.StartAsync();
        string ears = session.AddWatcher("cat_ears");
        string solo = session.AddWatcher("solo");
        Assert.Equal(DanbooruWatcherCheckKind.BaselineEstablished, (await session.RunNextAsync())!.Kind);
        Assert.Equal(DanbooruWatcherCheckKind.BaselineEstablished, (await session.RunNextAsync())!.Kind);
        Assert.Null(await session.RunNextAsync());

        world.Provider.Publish("cat_ears", 5001);
        world.Provider.Publish("solo", 5001);
        world.Provider.Publish("cat_ears solo", 5001, 5000);

        // Search + explicit selection (queue is paused, so every later request overlaps the live job).
        BooruSearchSnapshot search = Assert.IsType<BooruSearchSnapshot>(await session.SearchAsync("cat_ears solo"));
        BooruOriginalAdmission first = await session.AdmitAsync(search.Page.Posts.Single(post => post.Id.PostId == "5001"),
            BooruFindingKind.Search, "cat_ears solo");
        Assert.Equal(BooruOriginalAdmissionStatus.Queued, first.Status);

        // One-shot of the same post: reused, not queued again.
        BooruPostSnapshot oneShot = Assert.IsType<BooruPostSnapshot>(await session.ReadPostAsync("5001"));
        Assert.Equal(BooruOriginalAdmissionStatus.AlreadyQueued, (await session.AdmitAsync(oneShot.Post, BooruFindingKind.OneShot)).Status);

        // Both watchers find it as new; both hand it off, neither creates a second job.
        world.Clock.Advance(B9World.Interval);
        DanbooruWatcherCheckSummary a = (await session.RunNextAsync())!;
        DanbooruWatcherCheckSummary b = (await session.RunNextAsync())!;
        foreach (DanbooruWatcherCheckSummary check in new[] { a, b })
        {
            Assert.Equal(DanbooruWatcherCheckKind.Completed, check.Kind);
            Assert.Equal(1, check.NewPosts);
            Assert.Equal(0, check.Queued);
            Assert.Equal(1, check.AlreadyQueuedOrLinked);
        }
        Assert.Empty(world.Cdn.Requests);

        await session.Queue.SetPausedAsync(false);
        Assert.Equal(BooruOriginalDownloadState.Linked, (await first.Completion.WaitAsync(B9Wait.Timeout)).State);
        Assert.Equal(1, world.Cdn.CountFor(fresh.OriginalUrl));
        Assert.Equal(0, world.Cdn.CountFor(old.OriginalUrl));

        BooruPostRecord record = session.Record("5001");
        Assert.Equal(4, record.Findings.Count);
        Assert.Single(record.Findings, item => item.Kind == BooruFindingKind.Search);
        Assert.Single(record.Findings, item => item.Kind == BooruFindingKind.OneShot);
        Assert.Equal(new[] { ears, solo }.Order(), record.Findings.Where(item => item.Kind == BooruFindingKind.Watcher)
            .Select(item => item.WatcherId!).Order());

        // After the link every further path answers from the catalog: no new transfer.
        Assert.Equal(BooruOriginalAdmissionStatus.AlreadyLinked, (await session.AdmitAsync(oneShot.Post, BooruFindingKind.OneShot)).Status);
        world.Clock.Advance(B9World.Interval);
        await session.RunNextAsync();
        await session.RunNextAsync();
        Assert.Equal(1, world.Cdn.CountFor(fresh.OriginalUrl));
        world.AssertCleanWire();
    }

    // ---- Scenario 5: watcher lifecycle through the whole chain -------------------------------------------

    [Fact]
    public async Task S5_WatcherBaselineNewPostsPauseManualCheckResumeRemoveAndRestartEndToEnd()
    {
        using B9World world = new();
        B9Post baseline = world.Post(6000);
        B9Post[] fresh = [world.Post(6001), world.Post(6002), world.Post(6003)];
        world.Provider.Publish("cat_ears", 6000);
        string watcher;
        await using (B9Session session = new(world))
        {
            await session.StartAsync();
            watcher = session.AddWatcher("cat_ears");

            // Baseline from now: nothing historical is downloaded or even stored as a finding.
            Assert.Equal(DanbooruWatcherCheckKind.BaselineEstablished, (await session.RunNextAsync())!.Kind);
            Assert.Empty(world.Cdn.Requests);
            Assert.Empty(session.Catalog.Read().Posts);
            Assert.Null(await session.RunNextAsync());

            // New posts after one interval: each admitted once and linked.
            world.Provider.Publish("cat_ears", 6001, 6002);
            world.Clock.Advance(B9World.Interval);
            DanbooruWatcherCheckSummary check = (await session.RunNextAsync())!;
            Assert.Equal(DanbooruWatcherCheckKind.Completed, check.Kind);
            Assert.Equal(2, check.NewPosts);
            Assert.Equal(2, check.Queued);
            await B9Wait.UntilAsync(() => session.Catalog.Read().Posts.Count(post => post.LocalAssetId != null) == 2, "two linked originals");
            Assert.Equal("6002", session.Catalog.Read().Watchers.Single(item => item.WatcherId == watcher).HighWaterMark);

            // Pause: no provider request however much time passes.
            Assert.True(session.Scheduler.Pause(watcher).Accepted);
            int before = world.Provider.RequestCount;
            world.Clock.Advance(B9World.Interval * 4);
            Assert.Null(await session.RunNextAsync());
            Assert.Equal(before, world.Provider.RequestCount);
            Assert.Equal(DanbooruWatcherActivity.Paused, session.Scheduler.GetStatus(watcher)!.Activity);

            // Manual check of a paused watcher: exactly one bounded check, the watcher stays paused.
            world.Provider.Publish("cat_ears", 6003);
            Task<DanbooruWatcherCheckSummary> manual = session.Scheduler.CheckNowAsync(watcher);
            await session.RunNextAsync();
            DanbooruWatcherCheckSummary manualResult = await manual.WaitAsync(B9Wait.Timeout);
            Assert.True(manualResult.Manual);
            Assert.Equal(1, manualResult.Queued);
            Assert.Equal(before + 1, world.Provider.RequestCount);
            Assert.Equal(DanbooruWatcherState.Paused, session.Scheduler.GetStatus(watcher)!.State);

            // Resume → regular checks again; nothing new means nothing admitted.
            Assert.True(session.Scheduler.Resume(watcher).Accepted);
            world.Clock.Advance(B9World.Interval);
            DanbooruWatcherCheckSummary resumed = (await session.RunNextAsync())!;
            Assert.Equal(DanbooruWatcherCheckKind.Completed, resumed.Kind);
            Assert.Equal(0, resumed.NewPosts);
            await B9Wait.UntilAsync(() => session.Catalog.Read().Posts.Count(post => post.LocalAssetId != null) == 3, "three linked originals");

            // Remove: observation ends, local posts, findings and media stay.
            Assert.True(session.Scheduler.Remove(watcher).Accepted);
            int removedAt = world.Provider.RequestCount;
            world.Clock.Advance(B9World.Interval * 2);
            Assert.Null(await session.RunNextAsync());
            Assert.Equal("watcher_removed", (await session.Scheduler.CheckNowAsync(watcher)).Code);
            Assert.Equal(removedAt, world.Provider.RequestCount);
        }

        // Restart: the removed watcher stays removed; posts, findings and verified media survive.
        await using (B9Session restarted = new(world))
        {
            await restarted.StartAsync();
            Assert.Empty(restarted.Scheduler.GetSnapshot().Watchers);
            int requests = world.Provider.RequestCount;
            world.Clock.Advance(B9World.Interval * 2);
            Assert.Null(await restarted.RunNextAsync());
            Assert.Equal(requests, world.Provider.RequestCount);
            foreach (B9Post post in fresh)
            {
                BooruPostRecord record = restarted.Record(post.PostId);
                Assert.Equal(BooruFindingKind.Watcher, Assert.Single(record.Findings).Kind);
                Assert.Equal(watcher, record.Findings[0].WatcherId);
                Assert.Equal(post.Bytes, await B9Wait.ReadVerifiedAssetAsync(world.OutputRoot, post.PostId, record.LocalAssetId!));
                Assert.Equal(1, world.Cdn.CountFor(post.OriginalUrl));
            }
            Assert.Equal(0, world.Cdn.CountFor(baseline.OriginalUrl));
        }
        world.AssertCleanWire();
    }

    [Fact]
    public async Task S5_S9_Provider429DefersOnlyFurtherRequestsAndTheCooldownSurvivesARestart()
    {
        using B9World world = new();
        world.Post(7000);
        world.Provider.Publish("cat_ears", 7000);
        world.Provider.Respond = (_, index) => index == 0
            ? B9Provider.Status(HttpStatusCode.TooManyRequests, retryAfterSeconds: 600)
            : null;
        DateTimeOffset start = world.Clock.GetUtcNow();
        string watcher;
        await using (B9Session session = new(world))
        {
            await session.StartAsync();
            watcher = session.AddWatcher("cat_ears");
            DanbooruWatcherCheckSummary deferred = (await session.RunNextAsync())!;
            Assert.Equal(DanbooruWatcherCheckKind.Deferred, deferred.Kind);
            Assert.Equal(start + TimeSpan.FromSeconds(600), deferred.RetryAtUtc);
            Assert.Equal(DanbooruWatcherState.Cooldown, session.Scheduler.GetStatus(watcher)!.State);
            Assert.Equal(1, world.Provider.RequestCount);

            // During the cooldown neither a search nor a one-shot touches the provider.
            Assert.IsType<BooruSearchDeferred>(await session.SearchAsync("cat_ears"));
            Assert.IsType<BooruPostDeferred>(await session.ReadPostAsync("7000"));
            world.Clock.Advance(TimeSpan.FromSeconds(300));
            Assert.Null(await session.RunNextAsync());
            Assert.Equal(1, world.Provider.RequestCount);
        }

        // Restart: the transport's in-memory cooldown is gone, the persisted watcher retry time is not.
        await using (B9Session restarted = new(world))
        {
            await restarted.StartAsync();
            Assert.Equal(DanbooruWatcherState.Cooldown, restarted.Scheduler.GetStatus(watcher)!.State);
            Assert.Null(await restarted.RunNextAsync());
            DanbooruWatcherCheckSummary manual = await restarted.Scheduler.CheckNowAsync(watcher).WaitAsync(B9Wait.Timeout);
            Assert.Equal(DanbooruWatcherCheckKind.Rejected, manual.Kind);
            Assert.Equal(1, world.Provider.RequestCount);

            world.Clock.Advance(TimeSpan.FromSeconds(300));
            Assert.Equal(DanbooruWatcherCheckKind.BaselineEstablished, (await restarted.RunNextAsync())!.Kind);
            Assert.Equal(2, world.Provider.RequestCount);
            Assert.True(world.Provider.Requests[1].At - world.Provider.Requests[0].At >= TimeSpan.FromSeconds(600));
            Assert.Equal(DanbooruWatcherState.Active, restarted.Scheduler.GetStatus(watcher)!.State);
        }
        world.AssertCleanWire();
    }

    // ---- Scenario 6: queue pause / cancel / restart / damaged bytes --------------------------------------

    [Fact]
    public async Task S6_PausedQueueAcrossRestartRecoversTheOriginalFromLocalDataOnly()
    {
        using B9World world = new();
        B9Post post = world.Post(8001);
        await using (B9Session first = new(world, queuePaused: true))
        {
            await first.StartAsync();
            BooruPostSnapshot read = Assert.IsType<BooruPostSnapshot>(await first.ReadPostAsync("8001"));
            Assert.Equal(BooruOriginalAdmissionStatus.Queued, (await first.AdmitAsync(read.Post, BooruFindingKind.OneShot)).Status);
            await Task.Delay(100);
            Assert.Empty(world.Cdn.Requests);
        }
        int metadata = world.Provider.RequestCount;

        await using B9Session second = new(world);
        BooruOriginalAdmission recovered = Assert.Single(await second.StartAsync());
        Assert.Equal(BooruOriginalAdmissionStatus.Queued, recovered.Status);
        Assert.Equal(BooruOriginalDownloadState.Linked, (await recovered.Completion.WaitAsync(B9Wait.Timeout)).State);
        Assert.Equal(1, world.Cdn.CountFor(post.OriginalUrl));
        // Recovery uses the stored claim; it sends no metadata request.
        Assert.Equal(metadata, world.Provider.RequestCount);
        Assert.Single(second.Record("8001").Findings);
    }

    /// <summary>
    /// B9 finding F1 (review/booru-b9-gates-2026-11-12.md): an app shutdown during a transfer must not drop the
    /// user's request. Fixed by 2d57d64e (a queue stop settles as <c>Interrupted</c> and keeps the durable
    /// <see cref="BooruOriginalIntent.Requested"/>); this gate is active again and checks the running and the
    /// waiting job and their single transfer after the restart.
    /// </summary>
    [Fact]
    public async Task S6_AppShutdownDuringATransferKeepsRunningAndWaitingOriginalsPendingForTheNextStart()
    {
        using B9World world = new();
        B9Post running = world.Post(8101);
        B9Post waiting = world.Post(8102);
        world.Cdn.Gate = new SemaphoreSlim(0);
        BooruOriginalDownloadOutcome interrupted;
        await using (B9Session first = new(world))
        {
            await first.StartAsync();
            BooruPostSnapshot runningPost = Assert.IsType<BooruPostSnapshot>(await first.ReadPostAsync("8101"));
            BooruPostSnapshot waitingPost = Assert.IsType<BooruPostSnapshot>(await first.ReadPostAsync("8102"));
            BooruOriginalAdmission admission = await first.AdmitAsync(runningPost.Post, BooruFindingKind.OneShot);
            await B9Wait.UntilAsync(() => world.Cdn.CountFor(running.OriginalUrl) == 1, "transfer started");
            // The queue runs one job at a time, so the second original is admitted but still waiting.
            Assert.Equal(BooruOriginalAdmissionStatus.Queued, (await first.AdmitAsync(waitingPost.Post, BooruFindingKind.OneShot)).Status);
            await first.DisposeAsync();
            interrupted = await admission.Completion.WaitAsync(B9Wait.Timeout);
        }
        // Shutdown is not a success.
        Assert.NotEqual(BooruOriginalDownloadState.Linked, interrupted.State);
        world.Cdn.Gate = null;

        await using B9Session second = new(world);
        IReadOnlyList<BooruOriginalAdmission> recovered = await second.StartAsync();
        foreach (string postId in new[] { "8101", "8102" })
        {
            BooruOriginalIntent intent = second.Record(postId).OriginalIntent;
            Assert.True(intent == BooruOriginalIntent.Requested,
                $"An app shutdown left post {postId} with intent {intent}; a restart must resume a requested original " +
                "that the user did not cancel.");
        }
        Assert.Equal(2, recovered.Count);
        foreach (BooruOriginalAdmission item in recovered)
            Assert.Equal(BooruOriginalDownloadState.Linked, (await item.Completion.WaitAsync(B9Wait.Timeout)).State);
        Assert.Equal(1, world.Cdn.CountFor(waiting.OriginalUrl));
    }

    [Fact]
    public async Task S6_UserCancelIsDurableAcrossRestartAndAWatcherDoesNotRequeueIt()
    {
        using B9World world = new();
        B9Post post = world.Post(8201);
        world.Post(8200);
        world.Provider.Publish("cat_ears", 8200);
        world.Cdn.Gate = new SemaphoreSlim(0);
        string watcher;
        await using (B9Session first = new(world))
        {
            await first.StartAsync();
            watcher = first.AddWatcher("cat_ears");
            Assert.Equal(DanbooruWatcherCheckKind.BaselineEstablished, (await first.RunNextAsync())!.Kind);
            BooruPostSnapshot read = Assert.IsType<BooruPostSnapshot>(await first.ReadPostAsync("8201"));
            BooruOriginalAdmission admission = await first.AdmitAsync(read.Post, BooruFindingKind.OneShot);
            await B9Wait.UntilAsync(() => world.Cdn.CountFor(post.OriginalUrl) == 1, "transfer started");
            Assert.True(await first.Originals.CancelAsync("8201"));
            Assert.Equal(BooruOriginalDownloadState.Cancelled, (await admission.Completion.WaitAsync(B9Wait.Timeout)).State);
            Assert.Null(first.Record("8201").LocalAssetId);
        }
        world.Cdn.Gate = null;

        await using B9Session second = new(world);
        Assert.Empty(await second.StartAsync());
        Assert.Equal(BooruOriginalIntent.Cancelled, second.Record("8201").OriginalIntent);

        // The watcher later finds the cancelled post: provenance is recorded, the transfer is not restarted.
        world.Provider.Publish("cat_ears", 8201);
        world.Clock.Advance(B9World.Interval);
        DanbooruWatcherCheckSummary check = (await second.RunNextAsync())!;
        Assert.Equal(1, check.KeptCancelled);
        Assert.Equal(0, check.Queued);
        Assert.Contains(second.Record("8201").Findings, item => item.Kind == BooruFindingKind.Watcher && item.WatcherId == watcher);
        await Task.Delay(100);
        Assert.Equal(1, world.Cdn.CountFor(post.OriginalUrl));

        // An explicit retry is the only way back.
        BooruOriginalAdmission retry = await second.Originals.RetryAsync("8201").WaitAsync(B9Wait.Timeout);
        Assert.Equal(BooruOriginalDownloadState.Linked, (await retry.Completion.WaitAsync(B9Wait.Timeout)).State);
        Assert.Equal(2, world.Cdn.CountFor(post.OriginalUrl));
    }

    [Fact]
    public async Task S6_DamagedBytesNeverBecomeASuccessAndStayPendingUntilAGoodTransferAfterRestart()
    {
        using B9World world = new();
        B9Post post = world.Post(8301);
        world.Cdn.Corrupt[post.OriginalUrl] = true;
        string target = Path.Combine(world.OutputRoot, "danbooru", "8301", "8301.png");
        await using (B9Session first = new(world))
        {
            await first.StartAsync();
            BooruPostSnapshot read = Assert.IsType<BooruPostSnapshot>(await first.ReadPostAsync("8301"));
            BooruOriginalAdmission admission = await first.AdmitAsync(read.Post, BooruFindingKind.OneShot);
            BooruOriginalDownloadOutcome failed = await admission.Completion.WaitAsync(B9Wait.Timeout);
            Assert.Equal(BooruOriginalDownloadState.DownloadFailed, failed.State);
            Assert.Null(failed.AssetId);
            BooruPostRecord record = first.Record("8301");
            Assert.Null(record.LocalAssetId);
            Assert.True(record.HasPendingOriginal);
            Assert.False(File.Exists(target));
        }

        world.Cdn.Corrupt.Clear();
        await using B9Session second = new(world);
        BooruOriginalAdmission recovered = Assert.Single(await second.StartAsync());
        BooruOriginalDownloadOutcome linked = await recovered.Completion.WaitAsync(B9Wait.Timeout);
        Assert.Equal(BooruOriginalDownloadState.Linked, linked.State);
        Assert.Equal(post.Sha256Hex, linked.Sha256);
        Assert.Equal(post.Bytes, await B9Wait.ReadVerifiedAssetAsync(world.OutputRoot, "8301", linked.AssetId!));
    }

    // ---- Scenario 7: restart without network ---------------------------------------------------------------

    [Fact]
    public async Task S7_OfflineRestartKeepsBooruMediaMetadataAndWatchersReadableAndFailsOnlyVisibly()
    {
        using B9World world = new();
        B9Post searched = world.Post(9001, rating: "q");
        B9Post watched = world.Post(9002);
        B9Post pending = world.Post(9003);
        world.Provider.Publish("cat_ears", 9000);
        world.Post(9000);
        world.Provider.Publish("solo", 9001);
        string watcher;
        string searchedAsset, watchedAsset;
        await using (B9Session online = new(world))
        {
            await online.StartAsync();
            BooruSearchSnapshot search = Assert.IsType<BooruSearchSnapshot>(await online.SearchAsync("solo"));
            BooruOriginalAdmission a = await online.AdmitAsync(search.Page.Posts.Single(), BooruFindingKind.Search, "solo");
            searchedAsset = (await a.Completion.WaitAsync(B9Wait.Timeout)).AssetId!;
            watcher = online.AddWatcher("cat_ears");
            await online.RunNextAsync();
            world.Provider.Publish("cat_ears", 9002);
            world.Clock.Advance(B9World.Interval);
            Assert.Equal(1, (await online.RunNextAsync())!.Queued);
            await B9Wait.UntilAsync(() => online.Record("9002").LocalAssetId != null, "watched original linked");
            watchedAsset = online.Record("9002").LocalAssetId!;

            await online.Queue.SetPausedAsync(true);
            BooruPostSnapshot pendingPost = Assert.IsType<BooruPostSnapshot>(await online.ReadPostAsync("9003"));
            Assert.Equal(BooruOriginalAdmissionStatus.Queued, (await online.AdmitAsync(pendingPost.Post, BooruFindingKind.OneShot)).Status);
        }
        int metadataBefore = world.Provider.RequestCount;

        world.Provider.Offline = true;
        world.Cdn.Offline = true;
        await using (B9Session offline = new(world))
        {
            // The real host start: recover watchers and pending originals, then start the loop.
            await offline.StartHostAsync();

            // Local data is fully readable without any provider.
            Assert.Equal(searched.Bytes, await B9Wait.ReadVerifiedAssetAsync(world.OutputRoot, "9001", searchedAsset));
            Assert.Equal(watched.Bytes, await B9Wait.ReadVerifiedAssetAsync(world.OutputRoot, "9002", watchedAsset));
            BooruPostRecord searchedRecord = offline.Record("9001");
            Assert.Equal(BooruPostClaimRating.Questionable, searchedRecord.Post.Rating);
            Assert.Contains(searchedRecord.Post.Tags, tag => tag.Name == "cat_ears");
            Assert.Equal(BooruFindingKind.Search, Assert.Single(searchedRecord.Findings).Kind);
            Assert.Equal(watcher, Assert.Single(offline.Record("9002").Findings).WatcherId);
            DanbooruWatcherStatus status = offline.Scheduler.GetStatus(watcher)!;
            Assert.True(status.BaselineEstablished);
            Assert.Equal(DanbooruWatcherState.Active, status.State);

            // The pending original was retried from local data; offline it fails visibly and stays pending.
            await B9Wait.UntilAsync(() => offline.Originals.Snapshot().TryGetValue("9003", out BooruOriginalDownloadOutcome? outcome)
                && outcome.State != BooruOriginalDownloadState.Queued, "offline pending original reached a visible result");
            Assert.Equal(BooruOriginalDownloadState.DownloadFailed, offline.Originals.Snapshot()["9003"].State);
            Assert.True(offline.Record("9003").HasPendingOriginal);

            // Starting offline sent no metadata request: the watcher was not due.
            Assert.Equal(0, world.Provider.OfflineAttempts);
            Assert.Equal(metadataBefore, world.Provider.RequestCount);

            // An explicit search offline is a visible network failure (one host retry), not an empty result.
            BooruSearchFailure failure = Assert.IsType<BooruSearchFailure>(await offline.SearchAsync("cat_ears"));
            Assert.Equal("http-network", failure.Failure.Code);
            Assert.Equal(2, world.Provider.OfflineAttempts);

            // A manual check offline defers the watcher; it is neither removed nor put in error.
            DanbooruWatcherCheckSummary manual = await offline.PumpAsync(offline.Scheduler.CheckNowAsync(watcher));
            Assert.Equal(DanbooruWatcherCheckKind.Deferred, manual.Kind);
            Assert.Equal(DanbooruWatcherState.Cooldown, offline.Scheduler.GetStatus(watcher)!.State);
            Assert.Single(offline.Catalog.Read().Watchers, item => item.WatcherId == watcher);
        }

        world.Provider.Offline = false;
        world.Cdn.Offline = false;
        await using B9Session back = new(world);
        BooruOriginalAdmission recovered = Assert.Single(await back.StartAsync());
        Assert.Equal(BooruOriginalDownloadState.Linked, (await recovered.Completion.WaitAsync(B9Wait.Timeout)).State);
        Assert.Equal(pending.Bytes, await B9Wait.ReadVerifiedAssetAsync(world.OutputRoot, "9003", back.Record("9003").LocalAssetId!));
        world.AssertCleanWire();
    }

    // ---- Scenario 8: upgrade of a profile without booru data -----------------------------------------------

    [Fact]
    public async Task S8_ProfileWithoutBooruDataStartsOfflineWithoutRewritesAndGainsOnlyTheAdditiveCatalog()
    {
        using B9World world = new();
        // A profile written by the non-booru stores only (as before 1.1.0), plus unrelated user files.
        Directory.CreateDirectory(world.ProfileDirectory);
        using (MeowChan.Persistence.ProfileLease lease = MeowChan.Persistence.ProfileLease.Acquire(world.ProfileDirectory))
        {
            _ = new MeowChan.Persistence.WorkStateStore(lease.ProfileDirectory, lease).LoadResources();
            lease.Admission.CompleteStartup();
        }
        File.WriteAllText(Path.Combine(world.ProfileDirectory, "saved_urls.json"), "[\"https://boards.example.invalid/a/thread/1\"]");
        File.WriteAllText(Path.Combine(world.ProfileDirectory, "settings.json"), "{\"legacy\":true}");
        Directory.CreateDirectory(Path.Combine(world.OutputRoot, "a", "1"));
        File.WriteAllBytes(Path.Combine(world.OutputRoot, "a", "1", "legacy.png"), B9Post.SyntheticBytes(1));
        Dictionary<string, string> legacy = Inventory(world.Root);
        Assert.False(File.Exists(Path.Combine(world.ProfileDirectory, BooruCatalogStore.FileName)));

        world.Provider.Offline = true;
        world.Cdn.Offline = true;
        await using (B9Session upgraded = new(world))
        {
            await upgraded.StartHostAsync();
            Assert.Empty(upgraded.Scheduler.GetSnapshot().Watchers);
            Assert.Null(upgraded.Scheduler.GetSnapshot().FaultCode);
            Assert.Empty(upgraded.Originals.GetPendingOriginals());
            Assert.Empty(upgraded.Catalog.Read().Posts);
        }
        Assert.Equal(0, world.Provider.OfflineAttempts);
        Assert.Empty(world.Cdn.Requests);
        // Opening the profile with the booru stack rewrote nothing and created nothing booru-specific yet.
        AssertUnchanged(legacy, Inventory(world.Root), allowedNew: []);

        // First booru use (online) adds exactly the additive catalog and the downloaded original.
        world.Provider.Offline = false;
        world.Cdn.Offline = false;
        B9Post post = world.Post(9501);
        string asset;
        await using (B9Session first = new(world))
        {
            await first.StartAsync();
            BooruPostSnapshot read = Assert.IsType<BooruPostSnapshot>(await first.ReadPostAsync("9501"));
            BooruOriginalAdmission admission = await first.AdmitAsync(read.Post, BooruFindingKind.OneShot);
            asset = (await admission.Completion.WaitAsync(B9Wait.Timeout)).AssetId!;
        }
        Dictionary<string, string> after = Inventory(world.Root);
        AssertUnchanged(legacy, after, allowedNew: [Path.Combine("profile", BooruCatalogStore.FileName)],
            allowedNewPrefix: Path.Combine("media", "danbooru", "9501") + Path.DirectorySeparatorChar);
        Assert.DoesNotContain(after.Keys, key => key.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase));

        // And the upgraded profile restarts offline with the booru data readable.
        world.Provider.Offline = true;
        world.Cdn.Offline = true;
        await using B9Session reopened = new(world);
        await reopened.StartAsync();
        Assert.Equal(post.Bytes, await B9Wait.ReadVerifiedAssetAsync(world.OutputRoot, "9501", asset));
        Assert.Equal(asset, reopened.Record("9501").LocalAssetId);
    }

    // ---- Scenario 9: failure matrix through plugin, host and scheduler --------------------------------------

    public static TheoryData<string, int, string, string, string, string?> FailureMatrix() => new()
    {
        // case, status, body, media type, expected watcher check kind, expected stable code
        { "redirect-301", 301, "", "text/plain", nameof(DanbooruWatcherCheckKind.Failed), "http-redirect" },
        { "redirect-302", 302, "", "text/plain", nameof(DanbooruWatcherCheckKind.Failed), "http-redirect" },
        { "unauthorized-401", 401, "{}", "application/json", nameof(DanbooruWatcherCheckKind.Failed), "access-denied" },
        { "forbidden-403", 403, "<html>denied</html>", "text/html", nameof(DanbooruWatcherCheckKind.Failed), "access-denied" },
        { "not-found-404", 404, "{}", "application/json", nameof(DanbooruWatcherCheckKind.Failed), "not-found" },
        { "gone-410", 410, "{}", "application/json", nameof(DanbooruWatcherCheckKind.Failed), "page-limit" },
        { "html-challenge", 200, "<html><body>challenge</body></html>", "text/html", nameof(DanbooruWatcherCheckKind.Failed), "not-json" },
        { "wrong-shape", 200, "{\"posts\":[]}", "application/json", nameof(DanbooruWatcherCheckKind.Failed), "malformed" },
        { "truncated", 200, "[{\"id\": 1", "application/json", nameof(DanbooruWatcherCheckKind.Failed), "malformed" },
        { "too-large", 200, "[" + new string(' ', BooruHostHttp.MaximumWireBytes + 16) + "]", "application/json", nameof(DanbooruWatcherCheckKind.Failed), "metadata-too-large" },
        { "bad-gateway-502", 502, "busy", "text/plain", nameof(DanbooruWatcherCheckKind.Deferred), "provider-unavailable" },
        { "unavailable-503", 503, "busy", "text/plain", nameof(DanbooruWatcherCheckKind.Deferred), "provider-unavailable" },
        // The host intercepts 429 itself and starts the provider-wide cooldown (no Retry-After: host minimum).
        { "too-many-429", 429, "busy", "text/plain", nameof(DanbooruWatcherCheckKind.Deferred), "booru-cooldown" },
    };

    [Theory]
    [MemberData(nameof(FailureMatrix))]
    public async Task S9_ProviderFailuresKeepTheWatcherAdmitNothingAndNeverRetryAggressively(string caseName, int status,
        string body, string mediaType, string expectedKind, string? expectedCode)
    {
        using B9World world = new();
        world.Provider.Respond = (_, _) => B9Provider.Status((HttpStatusCode)status, body, mediaType);
        await using B9Session session = new(world);
        await session.StartAsync();
        string watcher = session.AddWatcher("cat_ears");

        DanbooruWatcherCheckSummary check = (await session.RunNextAsync())!;
        Assert.True(expectedKind == check.Kind.ToString(), $"{caseName}: expected {expectedKind}, got {check.Kind} ({check.Code}).");
        if (expectedCode != null) Assert.Equal(expectedCode, check.Code);
        Assert.False(string.IsNullOrEmpty(check.Code), $"{caseName}: a failure must carry a stable code.");
        Assert.Equal(0, check.Queued);
        Assert.Empty(world.Cdn.Requests);
        Assert.Empty(session.Catalog.Read().Posts);
        // The watcher survives every provider failure.
        BooruWatcherRecord stored = Assert.Single(session.Catalog.Read().Watchers);
        Assert.NotEqual(nameof(DanbooruWatcherState.Removed), stored.State);
        DanbooruWatcherStatus watcherStatus = session.Scheduler.GetStatus(watcher)!;
        Assert.Equal(check.Kind == DanbooruWatcherCheckKind.Deferred ? DanbooruWatcherState.Cooldown : DanbooruWatcherState.Error,
            watcherStatus.State);

        // At most one host retry (502/503), never a burst; nothing more for a whole minute.
        int sent = world.Provider.RequestCount;
        Assert.InRange(sent, 1, status is 502 or 503 ? 2 : 1);
        world.Clock.Advance(TimeSpan.FromSeconds(59));
        Assert.Null(await session.RunNextAsync());
        Assert.Equal(sent, world.Provider.RequestCount);

        // The same failure on a one-shot read is a visible failure result too, never a success or a download.
        BooruPostResult post = await session.ReadPostAsync("1");
        Assert.IsNotType<BooruPostSnapshot>(post);
        Assert.Empty(world.Cdn.Requests);
        world.AssertCleanWire();
    }

    [Fact]
    public async Task S9_EmptyResultIsABaselineNotAFailureAndDeletedOrBannedPostsKeepTheirProviderFlags()
    {
        using B9World world = new();
        world.Post(9701, deleted: true, banned: true);
        await using B9Session session = new(world);
        await session.StartAsync();
        string watcher = session.AddWatcher("nothing_here");
        Assert.Equal(DanbooruWatcherCheckKind.BaselineEstablished, (await session.RunNextAsync())!.Kind);
        Assert.Equal(DanbooruWatcherState.Active, session.Scheduler.GetStatus(watcher)!.State);
        Assert.IsType<BooruSearchSnapshot>(await session.SearchAsync("nothing_here"));

        BooruPostSnapshot flagged = Assert.IsType<BooruPostSnapshot>(await session.ReadPostAsync("9701"));
        Assert.True(flagged.Post.IsDeleted);
        Assert.True(flagged.Post.IsBanned);
        BooruOriginalAdmission admission = await session.AdmitAsync(flagged.Post, BooruFindingKind.OneShot);
        await admission.Completion.WaitAsync(B9Wait.Timeout);
        BooruPostRecord record = session.Record("9701");
        Assert.True(record.Post.IsDeleted);
        Assert.True(record.Post.IsBanned);
        world.AssertCleanWire();
    }

    [Fact]
    public async Task S9_SlowMetadataBodyTimesOutAndDefersTheWatcherInsteadOfHanging()
    {
        using B9World world = new();
        world.Provider.Respond = (_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new NeverEndingStream())
        };
        await using B9Session session = new(world);
        await session.StartAsync();
        string watcher = session.AddWatcher("cat_ears");

        Task<DanbooruWatcherCheckSummary?> check = session.Scheduler.RunNextAsync();
        DateTime deadline = DateTime.UtcNow + B9Wait.Timeout;
        while (!check.IsCompleted)
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("The slow body was not bounded by the response deadline.");
            await Task.Delay(10);
            // Jump over provider spacing/retry and, explicitly, over the per-attempt response deadline.
            if (!world.Clock.AdvanceToNextDelay(TimeSpan.FromSeconds(5))) world.Clock.Advance(TimeSpan.FromSeconds(1));
        }
        DanbooruWatcherCheckSummary summary = (await check)!;
        Assert.Equal(DanbooruWatcherCheckKind.Deferred, summary.Kind);
        Assert.Equal("http-timeout", summary.Code);
        Assert.Equal(DanbooruWatcherState.Cooldown, session.Scheduler.GetStatus(watcher)!.State);
        Assert.InRange(world.Provider.RequestCount, 1, 2);
    }

    // ---- Scenario 10: no credentials or private tags in provider requests -----------------------------------

    [Fact]
    public async Task S10_ProviderRequestsCarryOnlyTheUserQueryAndNoCredentialsEvenWithPrivateTagsInTheProfile()
    {
        using B9World world = new();
        world.Post(9901);
        world.Provider.Publish("cat_ears", 9901);
        await using B9Session session = new(world);
        await session.StartAsync();
        // A private tag exists locally; it must never leak into a provider request.
        File.WriteAllText(Path.Combine(session.ProfileDirectory, "private-note.txt"), "secret_private_tag");

        Assert.IsType<BooruSearchSnapshot>(await session.SearchAsync("cat_ears"));
        string watcher = session.AddWatcher("cat_ears");
        await session.RunNextAsync();
        Assert.IsType<BooruPostSnapshot>(await session.ReadPostAsync("9901"));

        // Invalid queries never reach the plugin or the wire.
        int before = world.Provider.RequestCount;
        Assert.IsType<BooruSearchFailure>(await session.SearchAsync("a b c"));
        Assert.IsType<BooruSearchFailure>(await session.SearchAsync("order:random"));
        Assert.False(session.Scheduler.AddWatcher("a b c").Accepted);
        Assert.Equal(before, world.Provider.RequestCount);

        Assert.All(world.Provider.Requests, request =>
        {
            Assert.DoesNotContain("secret", request.Tags ?? string.Empty, StringComparison.Ordinal);
            Assert.DoesNotContain("rating:", request.Tags ?? string.Empty, StringComparison.Ordinal);
        });
        Assert.Equal(["cat_ears", "cat_ears"], world.Provider.Requests.Where(item => item.Path == "/posts.json").Select(item => item.Tags!).ToArray());
        Assert.NotNull(watcher);
        world.AssertCleanWire();
    }

    // ---- helpers -----------------------------------------------------------------------------------------

    private static Dictionary<string, string> Inventory(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(path => !Path.GetFileName(path).StartsWith(".meowchan-profile", StringComparison.Ordinal)
                && !Path.GetFileName(path).EndsWith(".lock", StringComparison.Ordinal))
            .ToDictionary(path => Path.GetRelativePath(root, path),
                path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))), StringComparer.Ordinal);

    private static void AssertUnchanged(Dictionary<string, string> before, Dictionary<string, string> after,
        string[] allowedNew, string? allowedNewPrefix = null)
    {
        foreach ((string path, string hash) in before)
        {
            Assert.True(after.TryGetValue(path, out string? now), $"Pre-existing file '{path}' disappeared.");
            Assert.True(hash == now, $"Pre-existing file '{path}' was rewritten.");
        }
        string[] added = after.Keys.Except(before.Keys, StringComparer.Ordinal)
            .Where(path => !allowedNew.Contains(path, StringComparer.Ordinal)
                && (allowedNewPrefix is null || !path.StartsWith(allowedNewPrefix, StringComparison.Ordinal)))
            .ToArray();
        Assert.True(added.Length == 0, "Unexpected new files: " + string.Join(", ", added));
    }

    /// <summary>A response body that never yields a byte until cancelled.</summary>
    private sealed class NeverEndingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
