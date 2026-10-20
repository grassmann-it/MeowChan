using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using MeowChan.Archive;
using MeowChan.Boards.FourChan;
using MeowChan.Boards.EightKun;
using MeowChan.Downloader;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Rules;
using MeowChan.Storage;

namespace MeowChan.Tests;

public sealed class ContentRuntimeTests
{
    private const string Url = "https://boards.4chan.org/g/thread/123";
    private static readonly ResourceId Id = new ResourceId("4chan", "g", "123");

    [Fact]
    public async Task ExplicitTerminalRetrySavesMissingSelectedMediaWithoutRefetchingOrReevaluatingExclusions()
    {
        using TempDirectory profile = new TempDirectory();
        ContentTransport transport = new ContentTransport
        {
            MediaStatus = HttpStatusCode.Forbidden,
            Json = ThreadJson(new { no = 123, archived = 1, com = "terminal", tim = 777, ext = ".png", filename = "image",
                fsize = ContentTransport.Payload.Length, md5 = Convert.ToBase64String(MD5.HashData(ContentTransport.Payload)) },
                new { no = 124, tim = 778, ext = ".webm", filename = "excluded", fsize = 4 })
        };
        using HttpClient client = new HttpClient(transport);
        using ProfileLease lease = ProfileLease.Acquire(profile.Path);
        await using ThreadContentRuntime runtime = new ThreadContentRuntime(profile.Path, lease);
        await runtime.InitializeAsync();
        await runtime.Rules.SaveAsync(new[] { new MediaRule(MediaRuleScope.Global,
            types: new MediaTypeSelection(MediaTypeSelectionMode.Selected, new[] { ".png" })) });
        DownloadQueue queue = new DownloadQueue(1, 1L << 30);
        await using Registry registry = Registry(client, queue, Path.Combine(profile.Path, "media"), runtime);
        IThread thread = (await registry.AddAsync(Url)).Thread!;
        await TestWait.UntilAsync(() => thread.MediaFailures.Count == 1);
        Assert.Equal(ThreadSourceState.Archived, thread.SourceState);
        Assert.Equal(1, thread.GetSnapshot(new DownloadCounts()).Content!.ExcludedMedia);
        int metadataRequests = transport.ThreadRequests;
        await runtime.Rules.SaveAsync(Array.Empty<MediaRule>());
        transport.MediaStatus = HttpStatusCode.OK;
        Assert.Equal(CheckRequestStatus.Scheduled, registry.RequestCheck(thread));
        await TestWait.UntilAsync(() => thread.DownloadedMediaCount == 1 && thread.MediaFailures.Count == 0);
        await queue.WaitForIdleAsync().WaitAsync(TestWait.Timeout);
        Assert.Equal(metadataRequests, transport.ThreadRequests);
        Assert.Equal(1, thread.GetSnapshot(new DownloadCounts()).Content!.ExcludedMedia);
        Assert.Equal(2, transport.MediaRequests);
    }

    [Fact]
    public async Task ExclusionSurvivesRuleChangesRemoveRestartAnd404WhileNewMediaUsesNewRules()
    {
        using TempDirectory profile = new TempDirectory();
        ContentTransport transport = new ContentTransport { Json = ThreadJson(Post(123, "first", 777)) };
        using HttpClient client = new HttpClient(transport);
        string output = Path.Combine(profile.Path, "media");
        using (ProfileLease lease = ProfileLease.Acquire(profile.Path))
        {
            await using ThreadContentRuntime runtime = new ThreadContentRuntime(profile.Path, lease);
            await runtime.InitializeAsync();
            await runtime.Rules.SaveAsync(new[] { new MediaRule(MediaRuleScope.Global, types: new MediaTypeSelection(MediaTypeSelectionMode.Selected)) });
            DownloadQueue queue = new DownloadQueue(2, 1L << 30);
            await using Registry registry = Registry(client, queue, output, runtime);
            IThread thread = (await registry.AddAsync(Url)).Thread!;
            await TestWait.UntilAsync(() => thread.GetSnapshot(new DownloadCounts()).Content?.ExcludedMedia == 1);
            await thread.StopMonitoringAsync();
            Assert.Equal(ThreadActivity.Finished, thread.Activity);
            Assert.Equal(0, thread.DownloadedMediaCount);
            Assert.Equal(0, transport.MediaRequests);
            await runtime.Rules.SaveAsync(Array.Empty<MediaRule>());
            transport.Json = ThreadJson(Post(123, "changed", 777), Post(124, "new", 778));
            await thread.checkThreadAsync(queue);
            await queue.WaitForIdleAsync().WaitAsync(TestWait.Timeout);
            Assert.Equal(1, transport.MediaRequests);
            Assert.Equal(1, thread.GetSnapshot(new DownloadCounts()).Content!.ExcludedMedia);
            Assert.Equal(1, thread.DownloadedMediaCount);
            Assert.Equal(ThreadActivity.Finished, thread.Activity);
            await registry.RemoveThreadAsync(thread);
        }
        transport.Status = HttpStatusCode.NotFound;
        using (ProfileLease lease = ProfileLease.Acquire(profile.Path))
        {
            await using ThreadContentRuntime runtime = new ThreadContentRuntime(profile.Path, lease);
            await runtime.InitializeAsync();
            DownloadQueue queue = new DownloadQueue(2, 1L << 30);
            await using Registry registry = Registry(client, queue, output, runtime);
            IThread restored = (await registry.AddAsync(Url)).Thread!;
            await TestWait.UntilAsync(() => restored.SourceState == ThreadSourceState.NotFound);
            await queue.WaitForIdleAsync().WaitAsync(TestWait.Timeout);
            Assert.Equal(1, transport.MediaRequests);
            Assert.Equal(1, restored.DownloadedMediaCount);
            Assert.Equal(1, restored.GetSnapshot(new DownloadCounts()).Content!.ExcludedMedia);
        }
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(profile.Path);
        ArchivedThreadView archived = (await reader.ReadThreadAsync(Id))!;
        Assert.Equal(new[] { "changed", "new" }, archived.GetPosts().Select(post => post.Html));
        MediaAssetSnapshot assets = await MediaAssetReader.OpenAsync(archived.Summary.MediaDirectory);
        MediaAssetReference asset = Assert.Single(assets.GetPage());
        await using Stream stream = await assets.OpenVerifiedReadAsync(asset.AssetId);
        Assert.Equal(ContentTransport.Payload.Length, stream.Length);
    }

    [Fact]
    public async Task CaptureFailureDoesNotBlockDownloadsAndRetriedTerminalSnapshotKeepsMissingPosts()
    {
        using TempDirectory profile = new TempDirectory();
        using ProfileLease lease = ProfileLease.Acquire(profile.Path);
        await using ThreadContentRuntime runtime = new ThreadContentRuntime(profile.Path, lease);
        await runtime.InitializeAsync();
        Sql(profile.Path, "CREATE TRIGGER fail_capture BEFORE INSERT ON archive_posts BEGIN SELECT RAISE(ABORT, 'Fixture capture failure'); END;");
        ContentTransport transport = new ContentTransport { Json = ThreadJson(Post(123, "retained", 777)) };
        using HttpClient client = new HttpClient(transport);
        DownloadQueue queue = new DownloadQueue(1, 1L << 30);
        await using Registry registry = Registry(client, queue, Path.Combine(profile.Path, "media"), runtime);
        IThread thread = (await registry.AddAsync(Url)).Thread!;
        await TestWait.UntilAsync(() => thread.GetSnapshot(new DownloadCounts()).Content?.CaptureFailure != null);
        // Capture failure is published before durable selection/admission. Stopping at that point
        // legitimately cancels work not yet admitted, so await the intended successful transfer.
        await TestWait.UntilAsync(() => thread.DownloadedMediaCount == 1);
        Assert.NotNull(thread.GetSnapshot(new DownloadCounts()).Content?.CaptureFailure);
        await thread.StopMonitoringAsync();
        await queue.WaitForIdleAsync().WaitAsync(TestWait.Timeout);
        Assert.Equal(1, thread.DownloadedMediaCount);
        transport.Json = ThreadJson(new { no = 124, com = "later", archived = 1 });
        await thread.checkThreadAsync(queue);
        Assert.Equal(ThreadSourceState.Archived, thread.SourceState);
        Assert.NotNull(thread.GetSnapshot(new DownloadCounts()).Content!.CaptureFailure);
        Sql(profile.Path, "DROP TRIGGER fail_capture;");
        await thread.checkThreadAsync(queue); // Terminal paths retry pending content without fetching another snapshot.
        Assert.Null(thread.GetSnapshot(new DownloadCounts()).Content!.CaptureFailure);
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(profile.Path);
        ArchivedThreadView archive = (await reader.ReadThreadAsync(Id))!;
        Assert.Equal(new[] { "retained", "later" }, archive.GetPosts().OrderBy(post => post.PostId).Select(post => post.Html));
    }

    [Fact]
    public async Task RestoredLegacyWatchNeedsExplicitCaptureOptInAndNoPostIsInventedFromMedia()
    {
        using TempDirectory profile = new TempDirectory();
        using ProfileLease lease = ProfileLease.Acquire(profile.Path);
        SessionStore sessions = new SessionStore(profile.Path, lease);
        await sessions.SaveAsync(new SessionSnapshot { ThreadUrls = new[] { Url } });
        await using ThreadContentRuntime runtime = new ThreadContentRuntime(profile.Path, lease);
        await runtime.InitializeAsync();
        ContentTransport transport = new ContentTransport { Json = ThreadJson(Post(123, "previous watch", 777)) };
        using HttpClient client = new HttpClient(transport);
        DownloadQueue queue = new DownloadQueue(1, 1L << 30);
        await using Registry registry = new Registry(new IImageBoard[] { new FourChanImageBoard(client) }, queue,
            Path.Combine(profile.Path, "media"), TimeSpan.FromHours(1), 60, sessions, contentRuntime: runtime);
        await registry.RestoreAsync();
        IThread thread = registry.Threads.Single();
        await TestWait.UntilAsync(() => thread.TotalMediaCount == 1);
        await thread.StopMonitoringAsync();
        await using ArchiveReader reader = await runtime.OpenReaderAsync();
        ArchivedThreadView before = (await reader.ReadThreadAsync(Id))!;
        Assert.False(before.Summary.CaptureEnabled);
        Assert.Empty(before.GetPosts());
        await ((MeowChan.Boards.Base.BaseThread)thread).SetCaptureEnabledAsync(true);
        ArchivedThreadView after = (await reader.ReadThreadAsync(Id))!;
        Assert.True(after.Summary.CaptureEnabled);
        Assert.Equal("previous watch", Assert.Single(after.GetPosts()).Html);
    }

    [Fact]
    public async Task EightKunTextOnlyAndMultipleAttachmentsUseSameOfflineContract()
    {
        using TempDirectory profile = new TempDirectory();
        using ProfileLease lease = ProfileLease.Acquire(profile.Path);
        await using ThreadContentRuntime runtime = new ThreadContentRuntime(profile.Path, lease);
        await runtime.InitializeAsync();
        await runtime.Rules.SaveAsync(new[] { new MediaRule(MediaRuleScope.Global, types: new MediaTypeSelection(MediaTypeSelectionMode.Selected)) });
        ContentTransport transport = new ContentTransport
        {
            Json = ThreadJson(new { no = 123, com = "eight", tim = "777", ext = ".png", filename = "first", extra_files = new[] { new { tim = "778", ext = ".png", filename = "second" } } },
                new { no = 124, com = "text only" })
        };
        using HttpClient client = new HttpClient(transport);
        DownloadQueue queue = new DownloadQueue(1, 1L << 30);
        await using Registry registry = new Registry(new IImageBoard[] { new EightKunImageBoard(client) }, queue,
            Path.Combine(profile.Path, "media"), TimeSpan.FromHours(1), 60, persistSession: false, contentRuntime: runtime);
        IThread thread = (await registry.AddAsync("https://8kun.top/test/res/123.html")).Thread!;
        await TestWait.UntilAsync(() => thread.GetSnapshot(new DownloadCounts()).Content?.ExcludedMedia == 2
            || thread.LastCheckFailure != null || thread.GetSnapshot(new DownloadCounts()).Content?.SelectionFailure != null);
        Assert.True(thread.GetSnapshot(new DownloadCounts()).Content?.ExcludedMedia == 2,
            JsonSerializer.Serialize(thread.GetSnapshot(new DownloadCounts())));
        await using ArchiveReader reader = await runtime.OpenReaderAsync();
        ArchivedThreadView view = (await reader.ReadThreadAsync(new ResourceId("8kun", "test", "123")))!;
        Assert.Equal(2, view.PostCount);
        Assert.Equal(new[] { "777", "778" }, view.GetPosts()[0].Attachments!.Select(attachment => attachment.MediaId));
        Assert.Empty(view.GetPosts()[1].Attachments!);
        Assert.Equal(0, transport.MediaRequests);
    }

    private static Registry Registry(HttpClient client, DownloadQueue queue, string output, ThreadContentRuntime runtime) =>
        new Registry(new IImageBoard[] { new FourChanImageBoard(client) }, queue, output, TimeSpan.FromHours(1), 60,
            persistSession: false, contentRuntime: runtime);
    private static object Post(int id, string text, int media) => new { no = id, com = text, tim = media, ext = ".png", filename = "fixture", fsize = ContentTransport.Payload.Length, md5 = Convert.ToBase64String(MD5.HashData(ContentTransport.Payload)) };
    private static string ThreadJson(params object[] posts) => JsonSerializer.Serialize(new { posts });
    private static void Sql(string profile, string sql)
    {
        using SqliteConnection connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(profile, "archive.db"), Pooling = false }.ToString());
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
    private sealed class ContentTransport : HttpMessageHandler
    {
        internal static readonly byte[] Payload = new byte[] { 1, 2, 3, 4 };
        internal string Json = "{\"posts\":[]}";
        internal HttpStatusCode Status = HttpStatusCode.OK;
        internal int MediaRequests;
        internal int ThreadRequests;
        internal HttpStatusCode MediaStatus = HttpStatusCode.OK;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("boards.json", StringComparison.Ordinal))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"boards\":[{\"board\":\"g\",\"title\":\"Technology\"},{\"uri\":\"test\",\"title\":\"Test\"}]}") });
            if (path.EndsWith(".json", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref ThreadRequests);
                return Task.FromResult(new HttpResponseMessage(Status) { Content = new StringContent(Json) });
            }
            Interlocked.Increment(ref MediaRequests);
            return Task.FromResult(new HttpResponseMessage(MediaStatus) { Content = new ByteArrayContent(Payload) });
        }
    }
}
