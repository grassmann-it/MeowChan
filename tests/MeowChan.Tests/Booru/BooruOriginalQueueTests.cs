using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using MeowChan.Archive;
using MeowChan.Booru;
using MeowChan.Downloader;
using MeowChan.Persistence;
using TempDirectory = MeowChan.Tests.TempDirectory;

namespace MeowChan.Tests.Booru;

/// <summary>
/// B4: explicit Danbooru originals use the existing DownloadQueue in its durable (work-journal) mode,
/// without journal rows, chan registration or private tags. All HTTP is an in-memory fake CDN.
/// </summary>
public sealed class BooruOriginalQueueTests
{
    [Fact]
    public async Task NormalPostDownloadsOnlyTheOriginalLinksItsAssetOnceAndStaysOutOfChanState()
    {
        using TempDirectory directory = new();
        byte[] bytes = Bytes(1);
        FakeCdn cdn = new();
        BooruPostClaim post = Post("42", bytes, cdn);
        BooruOriginalDownloadOutcome outcome;
        int links = 0, catalogWrites = 0;
        await using (Session session = new(directory.Path, cdn))
        {
            session.Booru.LinkingForTests = (_, _) => Interlocked.Increment(ref links);
            session.Catalog.AfterAdmittedMutationForTests = _ => Interlocked.Increment(ref catalogWrites);
            BooruOriginalAdmission admission = await session.Booru.AdmitAsync(post, BooruFindingKind.OneShot);
            Assert.Equal(BooruOriginalAdmissionStatus.Queued, admission.Status);
            outcome = await admission.Completion.WaitAsync(TestWait.Timeout);

            Assert.Equal(BooruOriginalDownloadState.Linked, outcome.State);
            Assert.Equal(1, links);
            // Claim, one-shot finding, the durable request intent and exactly one asset link.
            Assert.Equal(4, catalogWrites);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), outcome.Sha256);
            Assert.Equal(bytes.Length, outcome.Length);
            Assert.Equal(bytes, File.ReadAllBytes(outcome.LocalPath!));
            Assert.Equal(Path.Combine(session.OutputRoot, "danbooru", "42", "42.png"), outcome.LocalPath);
            Assert.Equal([OriginalUrl("42")], cdn.Requests.ToArray());

            BooruPostRecord record = Assert.Single(session.Catalog.Read().Posts);
            Assert.Equal(outcome.AssetId, record.LocalAssetId);
            Assert.Equal(outcome.LocalPath, record.LocalPath);
            Assert.Equal(BooruFindingKind.OneShot, Assert.Single(record.Findings).Kind);
            // A one-shot creates no watcher, no chan journal resource and no private tag data.
            Assert.Empty(session.Catalog.Read().Watchers);
            Assert.Empty(session.Work.LoadResources());
            Assert.False(File.Exists(Path.Combine(session.ProfileDirectory, MeowChan.Tags.PrivateTagStore.FileName)));
            Assert.Empty(session.Booru.GetPendingOriginals());

            // A repeated request is answered from the catalog link, not from a second transfer.
            BooruOriginalAdmission again = await session.Booru.AdmitAsync(post, BooruFindingKind.OneShot);
            Assert.Equal(BooruOriginalAdmissionStatus.AlreadyLinked, again.Status);
            Assert.Single(cdn.Requests);
            Assert.Equal(1, links);
        }

        // Offline restart: the link and provenance survive without any provider request.
        using ProfileLease reopened = ProfileLease.Acquire(Path.Combine(directory.Path, "profile"));
        BooruPostRecord saved = Assert.Single(new BooruCatalogStore(reopened.ProfileDirectory, reopened).Read().Posts);
        Assert.Equal(outcome.AssetId, saved.LocalAssetId);
        Assert.Equal(BooruPostClaimRating.General, saved.Post.Rating);
        Assert.Single(cdn.Requests);
    }

    [Fact]
    public async Task SearchOneShotAndTwoWatchersShareExactlyOneQueueJobForTheSamePostOriginal()
    {
        using TempDirectory directory = new();
        byte[] bytes = Bytes(2);
        FakeCdn cdn = new() { Gate = new SemaphoreSlim(0) };
        BooruPostClaim post = Post("77", bytes, cdn);
        await using Session session = new(directory.Path, cdn);
        int links = 0;
        session.Booru.LinkingForTests = (_, _) => Interlocked.Increment(ref links);

        BooruOriginalAdmission first = await session.Booru.AdmitAsync(post, BooruFindingKind.Search, query: "cat");
        await TestWait.UntilAsync(() => cdn.Requests.Count == 1);
        BooruOriginalAdmission[] others =
        [
            await session.Booru.AdmitAsync(post, BooruFindingKind.OneShot),
            await session.Booru.AdmitAsync(post, BooruFindingKind.Watcher, "w1", "cat"),
            await session.Booru.AdmitAsync(post, BooruFindingKind.Watcher, "w2", "cat")
        ];
        Assert.Equal(BooruOriginalAdmissionStatus.Queued, first.Status);
        Assert.All(others, admission =>
        {
            Assert.Equal(BooruOriginalAdmissionStatus.AlreadyQueued, admission.Status);
            Assert.Same(first.Completion, admission.Completion);
        });
        (int active, int pending, _) = session.Queue.GetCapacitySnapshot();
        Assert.Equal(1, active + pending);

        cdn.Gate.Release();
        BooruOriginalDownloadOutcome outcome = await first.Completion.WaitAsync(TestWait.Timeout);
        Assert.Equal(BooruOriginalDownloadState.Linked, outcome.State);
        Assert.Single(cdn.Requests);
        Assert.Equal(1, links);
        BooruPostRecord record = Assert.Single(session.Catalog.Read().Posts);
        Assert.Equal(4, record.Findings.Count);
        // Watcher findings mark this single post only; no watcher record is created or changed here.
        Assert.Empty(session.Catalog.Read().Watchers);
    }

    [Fact]
    public async Task MissingRestrictedOrUnsafeOriginalsAreVisibleWithoutAnyTransferOrSampleFallback()
    {
        using TempDirectory directory = new();
        FakeCdn cdn = new();
        await using Session session = new(directory.Path, cdn);
        BooruPostClaim sampleOnly = new("danbooru", "5", BooruPostClaimPresence.Media, Media:
        [
            new BooruPostClaimMedia(BooruPostClaimMediaRole.Sample, "https://cdn.donmai.us/sample/5.jpg", ".jpg", 3, ExpectedMd5: Md5([1, 2, 3])),
            new BooruPostClaimMedia(BooruPostClaimMediaRole.Preview, "https://cdn.donmai.us/preview/5.jpg")
        ]);
        BooruPostClaim restricted = new("danbooru", "6", BooruPostClaimPresence.Media,
            Media: [new BooruPostClaimMedia(BooruPostClaimMediaRole.Original, null)]);
        BooruPostClaim unverifiable = new("danbooru", "7", BooruPostClaimPresence.Media,
            Media: [new BooruPostClaimMedia(BooruPostClaimMediaRole.Original, "https://cdn.donmai.us/original/7.png", ".png")]);
        BooruPostClaim foreignHost = new("danbooru", "8", BooruPostClaimPresence.Media, Media:
            [new BooruPostClaimMedia(BooruPostClaimMediaRole.Original, "https://source.example/8.png", ".png", 3, ExpectedMd5: Md5([1, 2, 3]))]);

        (BooruPostClaim Post, BooruOriginalSelectionError Error)[] cases =
        [
            (sampleOnly, BooruOriginalSelectionError.MissingOriginal),
            (restricted, BooruOriginalSelectionError.OriginalUrlMissing),
            (unverifiable, BooruOriginalSelectionError.MissingIntegrityClaims),
            (foreignHost, BooruOriginalSelectionError.MediaHostNotAllowed)
        ];
        foreach ((BooruPostClaim post, BooruOriginalSelectionError error) in cases)
        {
            BooruOriginalAdmission admission = await session.Booru.AdmitAsync(post, BooruFindingKind.OneShot);
            Assert.Equal(BooruOriginalAdmissionStatus.Unavailable, admission.Status);
            Assert.Equal(error, admission.SelectionError);
            BooruOriginalDownloadOutcome outcome = await admission.Completion;
            Assert.Equal(BooruOriginalDownloadState.Unavailable, outcome.State);
            Assert.Equal(outcome, session.Booru.Snapshot()[post.PostId]);
        }

        Assert.Empty(cdn.Requests);
        Assert.All(session.Catalog.Read().Posts, record => Assert.Null(record.LocalAssetId));
        Assert.Equal(0, session.Queue.GetCapacitySnapshot().Pending);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CorruptOrTruncatedBytesFailClosedStayPendingAndCanBeRetried(bool truncate)
    {
        using TempDirectory directory = new();
        byte[] bytes = Bytes(3);
        FakeCdn cdn = new();
        BooruPostClaim post = Post("90", bytes, cdn);
        cdn.Responses[OriginalUrl("90")] = truncate
            ? new FakeResponse(bytes[..(bytes.Length / 2)], DeclaredLength: bytes.Length)
            : new FakeResponse(bytes.Select(value => (byte)(value ^ 0x5a)).ToArray());
        await using Session session = new(directory.Path, cdn);

        BooruOriginalAdmission admission = await session.Booru.AdmitAsync(post, BooruFindingKind.OneShot);
        BooruOriginalDownloadOutcome failed = await admission.Completion.WaitAsync(TestWait.Timeout);

        Assert.Equal(BooruOriginalDownloadState.DownloadFailed, failed.State);
        Assert.False(string.IsNullOrWhiteSpace(failed.Failure));
        Assert.Null(failed.AssetId);
        string mediaDirectory = Path.Combine(session.OutputRoot, "danbooru", "90");
        Assert.False(File.Exists(Path.Combine(mediaDirectory, "90.png")));
        Assert.True(!Directory.Exists(mediaDirectory) || Directory.GetFiles(mediaDirectory).All(path => Path.GetFileName(path).StartsWith('.')));
        Assert.Null(Assert.Single(session.Catalog.Read().Posts).LocalAssetId);
        Assert.Equal("90", Assert.Single(session.Booru.GetPendingOriginals()).PostId);

        cdn.Responses[OriginalUrl("90")] = new FakeResponse(bytes);
        BooruOriginalAdmission retry = await session.Booru.RetryAsync("90");
        BooruOriginalDownloadOutcome linked = await retry.Completion.WaitAsync(TestWait.Timeout);
        Assert.Equal(BooruOriginalDownloadState.Linked, linked.State);
        Assert.Equal(bytes, File.ReadAllBytes(linked.LocalPath!));
        Assert.Empty(session.Booru.GetPendingOriginals());
    }

    [Fact]
    public async Task PausedQueueHoldsTheOriginalAndResumeCompletesIt()
    {
        using TempDirectory directory = new();
        byte[] bytes = Bytes(4);
        FakeCdn cdn = new();
        BooruPostClaim post = Post("11", bytes, cdn);
        await using Session session = new(directory.Path, cdn);
        await session.Queue.SetPausedAsync(true);

        BooruOriginalAdmission admission = await session.Booru.AdmitAsync(post, BooruFindingKind.OneShot);
        Assert.Equal(BooruOriginalAdmissionStatus.Queued, admission.Status);
        await Task.Delay(100);
        Assert.Empty(cdn.Requests);
        Assert.False(admission.Completion.IsCompleted);
        Assert.Equal(BooruOriginalDownloadState.Queued, session.Booru.Snapshot()["11"].State);

        await session.Queue.SetPausedAsync(false);
        Assert.Equal(BooruOriginalDownloadState.Linked, (await admission.Completion.WaitAsync(TestWait.Timeout)).State);
    }

    [Fact]
    public async Task PauseDuringTransferInterruptsAndResumesTheSameJob()
    {
        using TempDirectory directory = new();
        byte[] bytes = Bytes(5);
        FakeCdn cdn = new() { Gate = new SemaphoreSlim(0) };
        BooruPostClaim post = Post("12", bytes, cdn);
        await using Session session = new(directory.Path, cdn);

        BooruOriginalAdmission admission = await session.Booru.AdmitAsync(post, BooruFindingKind.OneShot);
        await TestWait.UntilAsync(() => cdn.Requests.Count == 1);
        await session.Queue.SetPausedAsync(true);
        Assert.False(admission.Completion.IsCompleted);
        Assert.Null(Assert.Single(session.Catalog.Read().Posts).LocalAssetId);

        cdn.Gate.Release();
        await session.Queue.SetPausedAsync(false);
        BooruOriginalDownloadOutcome outcome = await admission.Completion.WaitAsync(TestWait.Timeout);
        Assert.Equal(BooruOriginalDownloadState.Linked, outcome.State);
        Assert.Equal(2, cdn.Requests.Count);
    }

    [Fact]
    public async Task CancelIsAVisibleNonSuccessAndTheOriginalCanBeRequestedAgain()
    {
        using TempDirectory directory = new();
        byte[] bytes = Bytes(6);
        FakeCdn cdn = new() { Gate = new SemaphoreSlim(0) };
        BooruPostClaim post = Post("13", bytes, cdn);
        await using Session session = new(directory.Path, cdn);

        BooruOriginalAdmission admission = await session.Booru.AdmitAsync(post, BooruFindingKind.OneShot);
        await TestWait.UntilAsync(() => cdn.Requests.Count == 1);
        Assert.True(await session.Booru.CancelAsync("13"));
        BooruOriginalDownloadOutcome cancelled = await admission.Completion.WaitAsync(TestWait.Timeout);
        Assert.Equal(BooruOriginalDownloadState.Cancelled, cancelled.State);
        Assert.Null(Assert.Single(session.Catalog.Read().Posts).LocalAssetId);
        Assert.False(File.Exists(Path.Combine(session.OutputRoot, "danbooru", "13", "13.png")));
        Assert.False(await session.Booru.CancelAsync("13"));

        cdn.Gate.Release();
        BooruOriginalAdmission retry = await session.Booru.RetryAsync("13");
        Assert.Equal(BooruOriginalAdmissionStatus.Queued, retry.Status);
        Assert.Equal(BooruOriginalDownloadState.Linked, (await retry.Completion.WaitAsync(TestWait.Timeout)).State);
    }

    [Fact]
    public async Task RestartRecoversAPendingOriginalFromTheCatalogWithoutAJournalRow()
    {
        using TempDirectory directory = new();
        byte[] bytes = Bytes(7);
        FakeCdn cdn = new();
        BooruPostClaim post = Post("21", bytes, cdn);
        await using (Session first = new(directory.Path, cdn))
        {
            await first.Queue.SetPausedAsync(true);
            Assert.Equal(BooruOriginalAdmissionStatus.Queued,
                (await first.Booru.AdmitAsync(post, BooruFindingKind.Watcher, "w1", "cat")).Status);
            Assert.Empty(first.Work.LoadResources());
        }
        Assert.Empty(cdn.Requests);

        await using Session second = new(directory.Path, cdn);
        Assert.Equal("21", Assert.Single(second.Booru.GetPendingOriginals()).PostId);
        BooruOriginalAdmission recovered = Assert.Single(await second.Booru.RecoverPendingAsync());
        Assert.Equal(BooruOriginalAdmissionStatus.Queued, recovered.Status);
        Assert.Equal(BooruOriginalDownloadState.Linked, (await recovered.Completion.WaitAsync(TestWait.Timeout)).State);
        Assert.Single(cdn.Requests);
        Assert.Single(Assert.Single(second.Catalog.Read().Posts).Findings);
        Assert.Empty(second.Work.LoadResources());
    }

    [Fact]
    public async Task CancelledOriginalIsNotReAdmittedAfterARestart()
    {
        using TempDirectory directory = new();
        byte[] bytes = Bytes(9);
        FakeCdn cdn = new() { Gate = new SemaphoreSlim(0) };
        BooruPostClaim post = Post("41", bytes, cdn);
        await using (Session session = new(directory.Path, cdn))
        {
            BooruOriginalAdmission admission = await session.Booru.AdmitAsync(post, BooruFindingKind.OneShot);
            await TestWait.UntilAsync(() => cdn.Requests.Count == 1);
            Assert.True(await session.Booru.CancelAsync("41"));
            Assert.Equal(BooruOriginalDownloadState.Cancelled, (await admission.Completion.WaitAsync(TestWait.Timeout)).State);
            // The cancellation is durable and the post is no longer a pending original.
            Assert.Equal(BooruOriginalIntent.Cancelled,
                Assert.Single(session.Catalog.Read().Posts).OriginalIntent);
            Assert.Empty(session.Booru.GetPendingOriginals());
        }

        cdn.Gate.Release();
        await using Session reopened = new(directory.Path, cdn);
        Assert.Empty(reopened.Booru.GetPendingOriginals());
        Assert.Empty(await reopened.Booru.RecoverPendingAsync());
        // The one request seen above is the cancelled in-flight attempt; nothing new was sent.
        Assert.Single(cdn.Requests);
    }

    [Fact]
    public async Task UnavailableOriginalIsNotAPendingOriginalAfterARestart()
    {
        using TempDirectory directory = new();
        byte[] bytes = Bytes(10);
        FakeCdn cdn = new();
        // A sample-only post has no acceptable original; the provider data stays stored.
        BooruPostClaim post = Post("51", bytes, cdn) with
        {
            Media =
            [
                new BooruPostClaimMedia(BooruPostClaimMediaRole.Preview, $"https://cdn.donmai.us/preview/51.jpg"),
                new BooruPostClaimMedia(BooruPostClaimMediaRole.Sample, $"https://cdn.donmai.us/sample/51.jpg")
            ]
        };
        await using (Session session = new(directory.Path, cdn))
        {
            BooruOriginalAdmission admission = await session.Booru.AdmitAsync(post, BooruFindingKind.OneShot);
            Assert.Equal(BooruOriginalAdmissionStatus.Unavailable, admission.Status);
            BooruOriginalDownloadOutcome outcome = await admission.Completion.WaitAsync(TestWait.Timeout);
            Assert.Equal(BooruOriginalDownloadState.Unavailable, outcome.State);
            Assert.Equal(BooruOriginalSelectionError.MissingOriginal, outcome.SelectionError);
            Assert.Empty(cdn.Requests);
            Assert.Equal(BooruOriginalIntent.Unavailable,
                Assert.Single(session.Catalog.Read().Posts).OriginalIntent);
            Assert.Empty(session.Booru.GetPendingOriginals());
        }

        await using Session reopened = new(directory.Path, cdn);
        Assert.Empty(reopened.Booru.GetPendingOriginals());
        Assert.Empty(await reopened.Booru.RecoverPendingAsync());
        Assert.Empty(cdn.Requests);
    }

    [Fact]
    public async Task QueueRefusalIsTerminalAndAnExplicitRetryRestartsTheRequest()
    {
        using TempDirectory directory = new();
        byte[] bytes = Bytes(11);
        FakeCdn cdn = new() { Gate = new SemaphoreSlim(0) };
        BooruPostClaim post = Post("61", bytes, cdn);
        await using Session session = new(directory.Path, cdn);
        await session.Queue.StopAllAsync();

        BooruOriginalAdmission refused = await session.Booru.AdmitAsync(post, BooruFindingKind.OneShot);
        Assert.Equal(BooruOriginalAdmissionStatus.NotAdmitted, refused.Status);
        Assert.Equal(BooruOriginalDownloadState.NotAdmitted, (await refused.Completion.WaitAsync(TestWait.Timeout)).State);
        Assert.Equal(BooruOriginalIntent.NotAdmitted,
            Assert.Single(session.Catalog.Read().Posts).OriginalIntent);
        Assert.Empty(session.Booru.GetPendingOriginals());
        Assert.Empty(cdn.Requests);
    }

    [Fact]
    public async Task StoppingTheQueueCompletesTheExternalOwnerInsteadOfHanging()
    {
        using TempDirectory directory = new();
        byte[] bytes = Bytes(12);
        FakeCdn cdn = new() { Gate = new SemaphoreSlim(0) };
        BooruPostClaim post = Post("71", bytes, cdn);
        await using Session session = new(directory.Path, cdn);

        BooruOriginalAdmission admission = await session.Booru.AdmitAsync(post, BooruFindingKind.OneShot);
        await TestWait.UntilAsync(() => cdn.Requests.Count == 1);
        await session.Queue.StopAllAsync();

        // The completion task reaches a terminal, non-success state instead of hanging forever.
        BooruOriginalDownloadOutcome stopped = await admission.Completion.WaitAsync(TestWait.Timeout);
        Assert.Equal(BooruOriginalDownloadState.Cancelled, stopped.State);
        Assert.Null(stopped.AssetId);
    }

    [Fact]
    public async Task FailedCatalogLinkIsVisibleAndRestartRelinksTheCommittedMediaWithoutASecondTransfer()
    {
        using TempDirectory directory = new();
        byte[] bytes = Bytes(8);
        FakeCdn cdn = new();
        BooruPostClaim post = Post("31", bytes, cdn);
        BooruOriginalDownloadOutcome failed;
        await using (Session first = new(directory.Path, cdn))
        {
            await first.Queue.SetPausedAsync(true);
            BooruOriginalAdmission admission = await first.Booru.AdmitAsync(post, BooruFindingKind.OneShot);
            first.Catalog.BeforeAdmittedMutationForTests = _ => throw new IOException("synthetic catalog failure");
            await first.Queue.SetPausedAsync(false);
            failed = await admission.Completion.WaitAsync(TestWait.Timeout);
            first.Catalog.BeforeAdmittedMutationForTests = null;

            Assert.Equal(BooruOriginalDownloadState.LinkFailed, failed.State);
            Assert.Contains("synthetic catalog failure", failed.Failure);
            Assert.NotNull(failed.AssetId);
            Assert.True(File.Exists(failed.LocalPath));
            Assert.Null(Assert.Single(first.Catalog.Read().Posts).LocalAssetId);
            Assert.Equal(BooruOriginalDownloadState.LinkFailed, first.Booru.Snapshot()["31"].State);
        }
        Assert.Single(cdn.Requests);

        await using Session second = new(directory.Path, cdn);
        int links = 0;
        second.Booru.LinkingForTests = (_, _) => Interlocked.Increment(ref links);
        BooruOriginalAdmission recovered = Assert.Single(await second.Booru.RecoverPendingAsync());
        BooruOriginalDownloadOutcome linked = await recovered.Completion.WaitAsync(TestWait.Timeout);
        Assert.Equal(BooruOriginalDownloadState.Linked, linked.State);
        Assert.Equal(failed.AssetId, linked.AssetId);
        Assert.Equal(failed.LocalPath, linked.LocalPath);
        Assert.Equal(1, links);
        Assert.Single(cdn.Requests);
        Assert.Equal(failed.AssetId, Assert.Single(second.Catalog.Read().Posts).LocalAssetId);
    }

    [Fact]
    public async Task QueueOfAnotherProfileCannotCommitOrLinkABooruOriginal()
    {
        using TempDirectory directory = new();
        byte[] bytes = Bytes(9);
        FakeCdn cdn = new();
        BooruPostClaim post = Post("41", bytes, cdn);
        Directory.CreateDirectory(Path.Combine(directory.Path, "catalog-profile"));
        Directory.CreateDirectory(Path.Combine(directory.Path, "queue-profile"));
        using ProfileLease catalogLease = ProfileLease.Acquire(Path.Combine(directory.Path, "catalog-profile"));
        using ProfileLease queueLease = ProfileLease.Acquire(Path.Combine(directory.Path, "queue-profile"));
        DownloadQueue queue = new(1, 1L << 30);
        queue.ConfigureWorkState(new WorkStateStore(queueLease.ProfileDirectory, queueLease));
        BooruCatalogStore catalog = new(catalogLease.ProfileDirectory, catalogLease);
        catalogLease.Admission.CompleteStartup();
        queueLease.Admission.CompleteStartup();
        BooruOriginalQueue booru = new(queue, catalog, Path.Combine(directory.Path, "media"), new HttpClient(cdn));
        try
        {
            BooruOriginalAdmission admission = await booru.AdmitAsync(post, BooruFindingKind.OneShot);
            BooruOriginalDownloadOutcome outcome = await admission.Completion.WaitAsync(TestWait.Timeout);
            Assert.Equal(BooruOriginalDownloadState.DownloadFailed, outcome.State);
            Assert.Null(Assert.Single(catalog.Read().Posts).LocalAssetId);
            Assert.Empty(cdn.Requests);
        }
        finally { await queue.StopAllAsync().WaitAsync(TestWait.Timeout); }
    }

    private static string OriginalUrl(string postId) => $"https://cdn.donmai.us/original/{postId}.png";

    private static byte[] Bytes(int seed) => Enumerable.Range(0, 4096).Select(index => (byte)(index * 31 + seed)).ToArray();

    private static string Md5(byte[] bytes) => Convert.ToBase64String(MD5.HashData(bytes));

    private static BooruPostClaim Post(string postId, byte[] bytes, FakeCdn cdn)
    {
        cdn.Responses[OriginalUrl(postId)] = new FakeResponse(bytes);
        cdn.Responses[$"https://cdn.donmai.us/sample/{postId}.jpg"] = new FakeResponse([9, 9, 9]);
        cdn.Responses[$"https://cdn.donmai.us/preview/{postId}.jpg"] = new FakeResponse([8, 8]);
        return new BooruPostClaim("danbooru", postId,
            BooruPostClaimPresence.Media | BooruPostClaimPresence.Rating | BooruPostClaimPresence.Tags,
            PostUrl: $"https://danbooru.donmai.us/posts/{postId}",
            Rating: BooruPostClaimRating.General, RawRating: "g",
            Tags: [new BooruPostClaimTag("cat", BooruPostClaimTagCategory.General)],
            Media:
            [
                new BooruPostClaimMedia(BooruPostClaimMediaRole.Preview, $"https://cdn.donmai.us/preview/{postId}.jpg"),
                new BooruPostClaimMedia(BooruPostClaimMediaRole.Sample, $"https://cdn.donmai.us/sample/{postId}.jpg"),
                new BooruPostClaimMedia(BooruPostClaimMediaRole.Original, OriginalUrl(postId), ".png", bytes.Length, 64, 64, Md5(bytes))
            ]);
    }

    private sealed record FakeResponse(byte[] Body, long? DeclaredLength = null);

    /// <summary>In-memory CDN; it never opens a socket. An optional gate holds each response body.</summary>
    private sealed class FakeCdn : HttpMessageHandler
    {
        internal ConcurrentDictionary<string, FakeResponse> Responses { get; } = new(StringComparer.Ordinal);
        internal ConcurrentQueue<string> Requests { get; } = new();
        internal SemaphoreSlim? Gate { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string url = request.RequestUri!.AbsoluteUri;
            Requests.Enqueue(url);
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Null(request.Headers.Authorization);
            if (Gate != null)
            {
                await Gate.WaitAsync(cancellationToken);
                Gate.Release();
            }
            if (!Responses.TryGetValue(url, out FakeResponse? response))
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            ByteArrayContent content = new(response.Body);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            if (response.DeclaredLength is long declared) content.Headers.ContentLength = declared;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }
    }

    /// <summary>One profile: the queue in durable mode, the B3 catalog and the B4 adapter on one lease.</summary>
    private sealed class Session : IAsyncDisposable
    {
        private readonly ProfileLease _lease;

        internal Session(string root, FakeCdn cdn)
        {
            ProfileDirectory = Path.Combine(root, "profile");
            OutputRoot = Path.Combine(root, "media");
            Directory.CreateDirectory(ProfileDirectory);
            _lease = ProfileLease.Acquire(ProfileDirectory);
            ProfileDirectory = _lease.ProfileDirectory;
            Work = new WorkStateStore(ProfileDirectory, _lease);
            Queue = new DownloadQueue(1, 1L << 30);
            Queue.ConfigureWorkState(Work);
            Catalog = new BooruCatalogStore(ProfileDirectory, _lease);
            // As in Registry startup: the additive catalog is read while startup owns the profile.
            Catalog.Read();
            _lease.Admission.CompleteStartup();
            Booru = new BooruOriginalQueue(Queue, Catalog, OutputRoot, new HttpClient(cdn, disposeHandler: false));
        }

        internal string ProfileDirectory { get; }
        internal string OutputRoot { get; }
        internal WorkStateStore Work { get; }
        internal DownloadQueue Queue { get; }
        internal BooruCatalogStore Catalog { get; }
        internal BooruOriginalQueue Booru { get; }

        public async ValueTask DisposeAsync()
        {
            try { await Queue.StopAllAsync().WaitAsync(TestWait.Timeout); }
            finally { _lease.Dispose(); }
        }
    }
}
