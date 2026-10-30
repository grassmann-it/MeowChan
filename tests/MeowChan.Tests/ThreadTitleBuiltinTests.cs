using System.Net;
using System.Text.Json;
using MeowChan.Archive;
using MeowChan.Boards.Base;
using MeowChan.Boards.EightKun;
using MeowChan.Boards.FourChan;
using MeowChan.Downloader;
using MeowChan.Interfaces;
using MeowChan.Persistence;

namespace MeowChan.Tests;

public sealed class ThreadTitleBuiltinTests
{
    public static IEnumerable<object[]> RealTitles() =>
        from eightKun in new[] { false, true }
        from capture in new[] { false, true }
        from title in new[] { "Known source title", "Unknown", "Thread 123" }
        select new object[] { eightKun, capture, title };

    [Theory]
    [MemberData(nameof(RealTitles))]
    public async Task BuiltinRawTitleSurvivesMissingSubjectAndOfflineRebind(bool eightKun, bool capture, string title)
    {
        using TempDirectory profile = new();
        using Transport transport = new() { Posts = [new { no = 123, sub = title, com = "OP" }] };
        using HttpClient client = new(transport);
        ResourceId id = new(eightKun ? "8kun" : "4chan", "g", "123");
        using (ProfileLease lease = ProfileLease.Acquire(profile.Path))
        {
            await using ThreadContentRuntime runtime = new(profile.Path, lease);
            await runtime.InitializeAsync();
            BaseThread thread = Thread(eightKun, client);
            Assert.Equal("", thread.SourceTitle);
            await BindAsync(runtime, thread, profile.Path, capture);
            DownloadQueue queue = new(1, 1048576);
            try
            {
                await thread.checkThreadAsync(queue);
                Assert.Equal(title, thread.SourceTitle);
                transport.Posts = [new { no = 123, sub = " \t ", com = "New OP, not a title" }];
                await thread.checkThreadAsync(queue);
                Assert.Equal(title, thread.Title);
                Assert.Equal(title, thread.GetSnapshot(new()).Title);
            }
            finally { await queue.StopAllAsync(); }
            await using ArchiveReader reader = await runtime.OpenReaderAsync();
            ArchivedThreadView stored = (await reader.ReadThreadAsync(id))!;
            Assert.Equal(title, stored.Summary.Title);
            Assert.Equal(capture, stored.Summary.CaptureEnabled);
            Assert.Equal(capture, stored.Summary.LastCapturedAt.HasValue);
            Assert.Equal(capture ? 1 : 0, stored.PostCount);
        }
        transport.Offline = true;
        int requestsBeforeBind = transport.Requests;
        using (ProfileLease lease = ProfileLease.Acquire(profile.Path))
        {
            await using ThreadContentRuntime runtime = new(profile.Path, lease);
            await runtime.InitializeAsync();
            BaseThread restored = Thread(eightKun, client);
            Assert.Equal("", restored.SourceTitle); // Constructor did not invent a provider title.
            await BindAsync(runtime, restored, profile.Path, capture: false);
            Assert.Equal(requestsBeforeBind, transport.Requests);
            Assert.Equal(title, restored.Title);
            Assert.Equal(title, restored.SourceTitle);
            Assert.Equal(capture, restored.GetSnapshot(new()).Content!.CaptureEnabled);
            DownloadQueue queue = new(1, 1048576);
            try { await Assert.ThrowsAsync<HttpRequestException>(() => restored.checkThreadAsync(queue)); }
            finally { await queue.StopAllAsync(); }
            Assert.Equal(title, restored.Title);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task BuiltinMissingSubjectUsesOpeningWithoutSerializingDisplayAsTitle(bool eightKun, bool capture)
    {
        using TempDirectory profile = new();
        using Transport transport = new() { Posts = [new { no = 123, com = "<p>Opening &amp; 😀</p><br>second line" }] };
        using HttpClient client = new(transport);
        using ProfileLease lease = ProfileLease.Acquire(profile.Path);
        await using ThreadContentRuntime runtime = new(profile.Path, lease);
        await runtime.InitializeAsync();
        BaseThread thread = Thread(eightKun, client);
        await BindAsync(runtime, thread, profile.Path, capture);
        DownloadQueue queue = new(1, 1048576);
        try
        {
            // A second observation must not promote the previous display getter into source metadata.
            await thread.checkThreadAsync(queue);
            await thread.checkThreadAsync(queue);
        }
        finally { await queue.StopAllAsync(); }
        Assert.Equal("Opening & 😀 second line", thread.Title);
        Assert.Equal("", thread.SourceTitle);
        await using ArchiveReader reader = await runtime.OpenReaderAsync();
        ResourceId id = new(eightKun ? "8kun" : "4chan", "g", "123");
        ArchivedThreadView stored = (await reader.ReadThreadAsync(id))!;
        Assert.Equal("", stored.Summary.Title);
        Assert.Equal(capture ? 1 : 0, stored.PostCount);
        Assert.Equal(capture, stored.Summary.CaptureEnabled);
        transport.Offline = true;
        int requestsBeforeBind = transport.Requests;
        BaseThread rebound = Thread(eightKun, client);
        await BindAsync(runtime, rebound, profile.Path, capture: false);
        Assert.Equal(requestsBeforeBind, transport.Requests);
        Assert.Equal(capture ? "Opening & 😀 second line" : $"{id.Source} /g/ 123", rebound.Title);
        Assert.Equal("", rebound.SourceTitle);
    }

    [Theory]
    [InlineData(false, null)]
    [InlineData(true, null)]
    [InlineData(false, "Unknown")]
    [InlineData(true, "Unknown")]
    [InlineData(false, "Thread 123")]
    [InlineData(true, "Thread 123")]
    public async Task BuiltinCatalogSeparatesMissingSubjectFromExplicitLiteralTitle(bool eightKun, string? title)
    {
        using Transport transport = new() { Posts = [new { no = 123, sub = title, com = "<b>Catalog &amp; opening</b>" }] };
        using HttpClient client = new(transport);
        IBoard board = Provider(eightKun, client).GetBoard(new Uri(eightKun ? "https://8kun.top/g/" : "https://boards.4chan.org/g/"));
        BaseThread thread = Assert.IsAssignableFrom<BaseThread>(Assert.Single(await board.GetThreadsAsync()));
        Assert.Equal(title ?? "Catalog & opening", thread.Title);
        Assert.Equal(title ?? "", thread.SourceTitle);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BuiltinReplySubjectCannotBecomeThreadTitle(bool eightKun)
    {
        using Transport transport = new() { Posts = [new { no = 124, sub = "Reply subject", com = "Reply" }] };
        using HttpClient client = new(transport);
        BaseThread thread = Thread(eightKun, client);
        DownloadQueue queue = new(1, 1048576);
        try
        {
            await thread.checkThreadAsync(queue);
            Assert.Equal("", thread.SourceTitle);
            Assert.Equal($"{thread.Board.ImageBoard.Name} /g/ 123", thread.Title);
            transport.Posts = [new { no = 124, sub = "Reply subject", com = "Reply" }, new { no = 123, sub = "Actual OP subject", com = "OP" }];
            await thread.checkThreadAsync(queue);
            Assert.Equal("Actual OP subject", thread.SourceTitle);
        }
        finally { await queue.StopAllAsync(); }
    }

    private static IImageBoard Provider(bool eightKun, HttpClient client) => eightKun ? new EightKunImageBoard(client) : new FourChanImageBoard(client);
    private static BaseThread Thread(bool eightKun, HttpClient client) => (BaseThread)Provider(eightKun, client).GetThread(new Uri(
        eightKun ? "https://8kun.top/g/res/123.html" : "https://boards.4chan.org/g/thread/123"));
    private static Task BindAsync(ThreadContentRuntime runtime, BaseThread thread, string output, bool capture)
    {
        thread.RestoreDurableState(new WorkResource { Url = thread.Url, OutputRoot = output, Generation = 1, IsThread = true, Intent = WorkIntent.Watching });
        return runtime.BindAsync(thread, output, restored: !capture);
    }

    private sealed class Transport : HttpMessageHandler
    {
        internal object[] Posts = [];
        internal bool Offline;
        internal int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            if (Offline) throw new HttpRequestException("Offline fixture");
            Uri uri = request.RequestUri!;
            string json = uri.AbsolutePath == "/boards.json"
                ? uri.Host == "8kun.top" ? "[{\"uri\":\"g\",\"title\":\"Test\"}]" : "{\"boards\":[{\"board\":\"g\",\"title\":\"Test\"}]}"
                : uri.AbsolutePath.EndsWith("/catalog.json", StringComparison.Ordinal)
                    ? JsonSerializer.Serialize(new[] { new { page = 1, threads = Posts } })
                    : JsonSerializer.Serialize(new { posts = Posts });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        }
    }
}
