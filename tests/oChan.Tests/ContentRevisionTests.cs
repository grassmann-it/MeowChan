using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using oChan.Archive;
using oChan.Boards.Base;
using oChan.Boards.FourChan;
using oChan.Boards.EightKun;
using oChan.Downloader;
using oChan.Interfaces;
using oChan.Persistence;
using oChan.Storage;

namespace oChan.Tests;

public sealed class ContentRevisionTests
{
    private static readonly byte[][] Payloads = [[1, 2, 3, 4], [4, 3, 2, 1], [3, 4, 1, 2]];
    private const string ThreadUrl = "https://boards.4chan.org/g/thread/123";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FinalSourceSnapshotDuringActiveOldDownloadRetainsBothBytesAndDoesNotSignalIdleEarly(bool eightKun)
    {
        using TempDirectory profile = new TempDirectory();
        using ProfileLease lease = ProfileLease.Acquire(profile.Path);
        await using ThreadContentRuntime runtime = new ThreadContentRuntime(profile.Path, lease);
        await runtime.InitializeAsync();
        RevisionTransport transport = new RevisionTransport();
        using HttpClient client = new HttpClient(transport);
        DownloadQueue queue = new DownloadQueue(1, 1L << 30);
        IImageBoard provider = eightKun ? new ControlledEightKunProvider(client) : new ControlledProvider(client);
        await using Registry registry = new Registry(new[] { provider }, queue,
            Path.Combine(profile.Path, "media"), TimeSpan.FromHours(1), 60, persistSession: false, contentRuntime: runtime);
        BaseThread thread = Assert.IsAssignableFrom<BaseThread>((await registry.AddAsync(eightKun
            ? "https://8kun.top/g/res/123.html" : ThreadUrl)).Thread);
        IControlledThread controlled = Assert.IsAssignableFrom<IControlledThread>(thread);
        await transport.WaitForMediaAsync(0);
        await TestWait.UntilAsync(() => controlled.Clock.Waits == 1);

        transport.SetRevision(1, terminal: true);
        await thread.checkThreadAsync(queue);
        if (eightKun)
        {
            transport.SetThreadStatus(HttpStatusCode.NotFound);
            await thread.checkThreadAsync(queue);
        }
        await thread.StopMonitoringAsync();

        Assert.Equal(eightKun ? ThreadSourceState.NotFound : ThreadSourceState.Archived, thread.SourceState);
        Assert.Equal(new DownloadCounts(1, 1), Counts(queue, thread));
        Task idle = queue.WaitForIdleAsync();
        Assert.False(idle.IsCompleted);
        transport.Release(0);
        Task secondEntered = transport.WaitForMediaAsync(1);
        Task next = await Task.WhenAny(secondEntered, idle).WaitAsync(TestWait.Timeout);
        Assert.Same(secondEntered, next);
        await secondEntered;
        Assert.False(idle.IsCompleted);
        Assert.Equal(new DownloadCounts(0, 1), Counts(queue, thread));
        transport.Release(1);
        await idle.WaitAsync(TestWait.Timeout);

        Assert.Equal(new DownloadCounts(0, 0), Counts(queue, thread));
        Assert.Equal(2, transport.MediaRequests);
        Assert.Equal(new[] { 0, 1 }, transport.RequestedRevisions.ToArray());
        Assert.Equal(eightKun ? 3 : 2, transport.ThreadRequests);
        Assert.Equal(1, thread.DownloadedMediaCount);
        Assert.Equal(ThreadActivity.Finished, thread.Activity);
        MediaAssetSnapshot assets = await MediaAssetReader.OpenAsync(thread.DownloadDirectory);
        Assert.Equal(2, assets.Count);
        MediaAssetReference current = Assert.Single(assets.GetPage(), asset => asset.IsCurrent);
        Assert.Equal(Hash(Payloads[1]), current.Sha256);
        Assert.Contains(assets.GetPage(), asset => !asset.IsCurrent && asset.Sha256 == Hash(Payloads[0]));
        await using ArchiveReader reader = await runtime.OpenReaderAsync();
        ArchivedThreadView archived = (await reader.ReadThreadAsync(new ResourceId(eightKun ? "8kun" : "4chan", "g", "123")))!;
        Assert.Equal(eightKun ? ThreadSourceState.NotFound : ThreadSourceState.Archived, archived.Summary.SourceState);
        CapturedPost post = Assert.Single(archived.GetPosts());
        Assert.Equal(Convert.ToBase64String(MD5.HashData(Payloads[1])), Assert.Single(post.Attachments!).ExpectedMd5);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OnlyLatestSuccessorRemainsAndRevertingToActiveBytesRemovesTheSuccessor(bool revert)
    {
        using TempDirectory directory = new TempDirectory();
        RevisionTransport transport = new RevisionTransport();
        using StubImageBoard provider = new StubImageBoard(transport);
        ProbeThread thread = new ProbeThread(provider.Board, new Uri("https://stub.test/g/thread/123")) { TotalMediaCount = 1 };
        DownloadQueue queue = new DownloadQueue(1, 1L << 30, maxPendingDownloads: 1);
        try
        {
            DownloadItem first = Item(provider, thread, directory.Path, 0);
            Assert.True(queue.TryEnqueue(first));
            await transport.WaitForMediaAsync(0);
            Assert.True(queue.TryEnqueue(Item(provider, thread, directory.Path, 1)));
            Assert.False(queue.TryEnqueue(Item(provider, thread, directory.Path, 1)));
            Assert.Equal(new DownloadCounts(1, 1), Counts(queue, thread));
            Assert.Equal(1, queue.GetCapacitySnapshot().Pending);
            Assert.False(queue.TryEnqueue(new DownloadItem(new Uri("https://stub.test/media/other.bin"),
                Path.Combine(directory.Path, "other.bin"), provider, thread, "other")));
            Assert.True(queue.TryEnqueue(Item(provider, thread, directory.Path, 2)));
            Assert.Equal(new DownloadCounts(1, 1), Counts(queue, thread));
            if (revert)
            {
                Assert.True(queue.TryEnqueue(first));
                Assert.Equal(new DownloadCounts(0, 1), Counts(queue, thread));
            }
            transport.SetRevision(revert ? 0 : 2);
            Task idle = queue.WaitForIdleAsync();
            transport.Release(0);
            if (!revert)
            {
                await transport.WaitForMediaAsync(1);
                Assert.False(idle.IsCompleted);
                Assert.Equal(new DownloadCounts(0, 1), Counts(queue, thread));
                transport.Release(1);
            }
            await idle.WaitAsync(TestWait.Timeout);
            Assert.Equal(revert ? new[] { 0 } : new[] { 0, 2 }, transport.RequestedRevisions.ToArray());
            Assert.Equal(new DownloadCounts(), Counts(queue, thread));
            MediaAssetSnapshot assets = await MediaAssetReader.OpenAsync(Path.GetDirectoryName(first.DestinationPath)!);
            Assert.Equal(revert ? 1 : 2, assets.Count);
            Assert.Equal(Hash(Payloads[revert ? 0 : 2]), Assert.Single(assets.GetPage(), asset => asset.IsCurrent).Sha256);
        }
        finally { await queue.StopAllAsync().WaitAsync(TestWait.Timeout); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationDropsSuccessorAndDrainsCountsWithoutPublishingPartialFiles(bool stopAll)
    {
        using TempDirectory directory = new TempDirectory();
        RevisionTransport transport = new RevisionTransport();
        using StubImageBoard provider = new StubImageBoard(transport);
        ProbeThread thread = new ProbeThread(provider.Board, new Uri("https://stub.test/g/thread/123"));
        DownloadQueue queue = new DownloadQueue(1, 1L << 30);
        try
        {
            Assert.True(queue.TryEnqueue(Item(provider, thread, directory.Path, 0)));
            await transport.WaitForMediaAsync(0);
            Assert.True(queue.TryEnqueue(Item(provider, thread, directory.Path, 1)));
            Assert.Equal(new DownloadCounts(1, 1), Counts(queue, thread));
            Task idle = queue.WaitForIdleAsync();

            await (stopAll ? queue.StopAllAsync() : queue.CancelDownloadsForThreadAsync(thread)).WaitAsync(TestWait.Timeout);
            await idle.WaitAsync(TestWait.Timeout);

            Assert.Equal(new DownloadCounts(), Counts(queue, thread));
            Assert.Equal(1, transport.MediaRequests);
            Assert.False(queue.TryEnqueue(Item(provider, thread, directory.Path, 1)));
            Assert.Empty(Directory.GetFiles(directory.Path, "*", SearchOption.AllDirectories));
            ProbeThread replacement = new ProbeThread(provider.Board, thread.ThreadUri);
            transport.SetRevision(1);
            transport.Release(1);
            bool admitted = queue.TryEnqueue(Item(provider, replacement, directory.Path, 1));
            Assert.Equal(!stopAll, admitted);
            if (admitted)
            {
                await queue.WaitForIdleAsync().WaitAsync(TestWait.Timeout);
                Assert.Equal(2, transport.MediaRequests);
                Assert.Equal(new DownloadCounts(), Counts(queue, replacement));
                Assert.Equal(1, replacement.DownloadedMediaCount);
            }
        }
        finally { await queue.StopAllAsync().WaitAsync(TestWait.Timeout); }
    }

    [Fact]
    public async Task FailedOldRetrievalDoesNotDropAQueuedRevisionOrLeaveItsFailureAfterSuccess()
    {
        using TempDirectory directory = new TempDirectory();
        RevisionTransport transport = new RevisionTransport { FailFirstMedia = true };
        using StubImageBoard provider = new StubImageBoard(transport);
        ProbeThread thread = new ProbeThread(provider.Board, new Uri("https://stub.test/g/thread/123")) { TotalMediaCount = 1 };
        DownloadQueue queue = new DownloadQueue(1, 1L << 30);
        try
        {
            Assert.True(queue.TryEnqueue(Item(provider, thread, directory.Path, 0)));
            await transport.WaitForMediaAsync(0);
            Assert.True(queue.TryEnqueue(Item(provider, thread, directory.Path, 1)));
            transport.SetRevision(1);
            transport.Release(0);
            await transport.WaitForMediaAsync(1);
            Assert.NotEmpty(thread.MediaFailures);
            transport.Release(1);
            await queue.WaitForIdleAsync().WaitAsync(TestWait.Timeout);

            Assert.Empty(thread.MediaFailures);
            Assert.Equal(ThreadActivity.Finished, thread.Activity);
            Assert.Equal(1, thread.DownloadedMediaCount);
            Assert.Equal(new DownloadCounts(), Counts(queue, thread));
            MediaAssetSnapshot assets = await MediaAssetReader.OpenAsync(Path.Combine(directory.Path, "stub", "g", "123"));
            Assert.Equal(Hash(Payloads[1]), Assert.Single(assets.GetPage()).Sha256);
        }
        finally { await queue.StopAllAsync().WaitAsync(TestWait.Timeout); }
    }

    [Fact]
    public async Task ReenablingCaptureAfterTerminalObservationCommitsLatestSeenPostsEvenWithEarlierCapture()
    {
        using TempDirectory profile = new TempDirectory();
        using ProfileLease lease = ProfileLease.Acquire(profile.Path);
        await using ThreadContentRuntime runtime = new ThreadContentRuntime(profile.Path, lease);
        await runtime.InitializeAsync();
        RevisionTransport transport = new RevisionTransport { TextOnly = true };
        using HttpClient client = new HttpClient(transport);
        DownloadQueue queue = new DownloadQueue(1, 1L << 30);
        await using Registry registry = new Registry(new IImageBoard[] { new ControlledProvider(client) }, queue,
            Path.Combine(profile.Path, "media"), TimeSpan.FromHours(1), 60, persistSession: false, contentRuntime: runtime);
        ControlledThread thread = Assert.IsType<ControlledThread>((await registry.AddAsync(ThreadUrl)).Thread);
        await TestWait.UntilAsync(() => thread.Clock.Waits == 1);
        await thread.SetCaptureEnabledAsync(false);
        transport.SetRevision(1, terminal: true);
        await thread.checkThreadAsync(queue);
        await thread.StopMonitoringAsync();
        await using ArchiveReader reader = await runtime.OpenReaderAsync();
        ResourceId id = new ResourceId("4chan", "g", "123");
        Assert.Equal("revision 0", Assert.Single((await reader.ReadThreadAsync(id))!.GetPosts()).Html);

        await thread.SetCaptureEnabledAsync(true);

        Assert.Equal("revision 1", Assert.Single((await reader.ReadThreadAsync(id))!.GetPosts()).Html);
        Assert.Equal(2, transport.ThreadRequests);
        Assert.Equal(0, transport.MediaRequests);
    }

    [Fact]
    public async Task CaptureFailureRetriesRetainedContentBefore304AndClearsOnlyAfterCommit()
    {
        using TempDirectory profile = new TempDirectory();
        using ProfileLease lease = ProfileLease.Acquire(profile.Path);
        await using ThreadContentRuntime runtime = new ThreadContentRuntime(profile.Path, lease);
        await runtime.InitializeAsync();
        Sql(runtime.Catalog.DatabasePath, "CREATE TRIGGER fail_capture BEFORE INSERT ON archive_posts BEGIN SELECT RAISE(ABORT,'fixture'); END;");
        RevisionTransport transport = new RevisionTransport { TextOnly = true };
        using HttpClient client = new HttpClient(transport);
        DownloadQueue queue = new DownloadQueue(1, 1L << 30);
        await using Registry registry = new Registry(new IImageBoard[] { new ControlledProvider(client) }, queue,
            Path.Combine(profile.Path, "media"), TimeSpan.FromHours(1), 60, persistSession: false, contentRuntime: runtime);
        ControlledThread thread = Assert.IsType<ControlledThread>((await registry.AddAsync(ThreadUrl)).Thread);
        await TestWait.UntilAsync(() => thread.Clock.Waits == 1);
        Assert.NotNull(thread.GetSnapshot(new DownloadCounts()).Content!.CaptureFailure);
        Assert.Null(thread.LastCheckFailure);
        await using ArchiveReader reader = await runtime.OpenReaderAsync();
        ResourceId id = new ResourceId("4chan", "g", "123");
        Assert.Empty((await reader.ReadThreadAsync(id))!.GetPosts());
        transport.SetThreadStatus(HttpStatusCode.NotModified);
        await thread.checkThreadAsync(queue);
        Assert.NotNull(thread.GetSnapshot(new DownloadCounts()).Content!.CaptureFailure);
        Sql(runtime.Catalog.DatabasePath, "DROP TRIGGER fail_capture;");

        await thread.checkThreadAsync(queue);

        Assert.Null(thread.GetSnapshot(new DownloadCounts()).Content!.CaptureFailure);
        Assert.NotNull(thread.GetSnapshot(new DownloadCounts()).Content!.LastCapturedAt);
        Assert.Equal("revision 0", Assert.Single((await reader.ReadThreadAsync(id))!.GetPosts()).Html);
        Assert.Equal(3, transport.ThreadRequests);
        Assert.Equal(0, transport.MediaRequests);
    }

    private static DownloadCounts Counts(DownloadQueue queue, IThread thread) => queue.GetThreadCounts(new[] { thread })[thread];
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static DownloadItem Item(StubImageBoard provider, IThread thread, string root, int revision)
        => new DownloadItem(new Uri("https://stub.test/media/456.bin"), Path.Combine(root, "stub", "g", "123", "456.bin"),
            provider, thread, "456", Payloads[revision].Length, Convert.ToBase64String(MD5.HashData(Payloads[revision])));
    private static void Sql(string path, string sql)
    {
        using SqliteConnection connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private sealed class ControlledProvider(HttpClient client) : FourChanImageBoard(client)
    {
        public override IThread GetThread(Uri uri)
        {
            IThread original = base.GetThread(uri);
            return new ControlledThread(original.Board, original.ThreadUri);
        }
    }
    private interface IControlledThread { AsyncStepGate Clock { get; } }
    private sealed class ControlledThread(IBoard board, Uri uri) : FourChanThread(board, uri), IControlledThread
    {
        public AsyncStepGate Clock { get; } = new AsyncStepGate();
        protected override Task WaitForNextCheckAsync(TimeSpan interval, CancellationToken cancellationToken) => Clock.WaitAsync(cancellationToken);
    }
    private sealed class ControlledEightKunProvider(HttpClient client) : EightKunImageBoard(client)
    {
        public override IThread GetThread(Uri uri)
        {
            IThread original = base.GetThread(uri);
            return new ControlledEightKunThread(original.Board, original.ThreadUri);
        }
    }
    private sealed class ControlledEightKunThread(IBoard board, Uri uri) : EightKunThread(board, uri), IControlledThread
    {
        public AsyncStepGate Clock { get; } = new AsyncStepGate();
        protected override Task WaitForNextCheckAsync(TimeSpan interval, CancellationToken cancellationToken) => Clock.WaitAsync(cancellationToken);
    }

    private sealed class RevisionTransport : HttpMessageHandler
    {
        private readonly TaskCompletionSource[] _entered = Enumerable.Range(0, 4)
            .Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        private readonly TaskCompletionSource[] _released = Enumerable.Range(0, 4)
            .Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        private int _revision;
        private int _terminal;
        private int _mediaRequests;
        private int _threadRequests;
        private int _threadStatus = (int)HttpStatusCode.OK;
        public bool FailFirstMedia { get; init; }
        public bool TextOnly { get; init; }
        public int MediaRequests => Volatile.Read(ref _mediaRequests);
        public int ThreadRequests => Volatile.Read(ref _threadRequests);
        public ConcurrentQueue<int> RequestedRevisions { get; } = new ConcurrentQueue<int>();
        public Task WaitForMediaAsync(int index) => _entered[index].Task.WaitAsync(TestWait.Timeout);
        public void Release(int index) => _released[index].TrySetResult();
        public void SetThreadStatus(HttpStatusCode status) => Volatile.Write(ref _threadStatus, (int)status);
        public void SetRevision(int revision, bool terminal = false)
        {
            Volatile.Write(ref _revision, revision);
            Volatile.Write(ref _terminal, terminal ? 1 : 0);
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("boards.json", StringComparison.Ordinal))
                return Json(request.RequestUri.Host == "8kun.top"
                    ? "[{\"uri\":\"g\",\"title\":\"Technology\"}]"
                    : "{\"boards\":[{\"board\":\"g\",\"title\":\"Technology\"}]}");
            if (path.EndsWith(".json", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _threadRequests);
                HttpStatusCode status = (HttpStatusCode)Volatile.Read(ref _threadStatus);
                if (status != HttpStatusCode.OK) return new HttpResponseMessage(status);
                int revision = Volatile.Read(ref _revision);
                Dictionary<string, object> post = new Dictionary<string, object>
                {
                    ["no"] = 123L, ["com"] = "revision " + revision, ["archived"] = Volatile.Read(ref _terminal)
                };
                if (!TextOnly)
                {
                    post["tim"] = 456L;
                    post["ext"] = ".bin";
                    post["filename"] = "fixture";
                    post["fsize"] = Payloads[revision].Length;
                    post["md5"] = Convert.ToBase64String(MD5.HashData(Payloads[revision]));
                }
                return Json(JsonSerializer.Serialize(new { posts = new[] { post } }));
            }
            int index = Interlocked.Increment(ref _mediaRequests) - 1;
            int bytesRevision = Volatile.Read(ref _revision);
            RequestedRevisions.Enqueue(bytesRevision);
            _entered[index].TrySetResult();
            await _released[index].Task.WaitAsync(cancellationToken);
            return index == 0 && FailFirstMedia ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Payloads[bytesRevision]) };
        }

        private static HttpResponseMessage Json(string json) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) };
    }
}
