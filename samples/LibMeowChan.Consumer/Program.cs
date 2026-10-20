using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MeowChan;
using MeowChan.Boards.Base;
using MeowChan.Archive;
using MeowChan.Persistence;
using MeowChan.Downloader;
using MeowChan.Interfaces;
using MeowChan.Storage;

namespace LibMeowChan.Consumer;

internal static class Program
{
    private static async Task Main()
    {
        string directory = Directory.CreateTempSubdirectory("libmeowchan-consumer-").FullName;
        using DemoHandler handler = new DemoHandler();
        using HttpClient transport = new HttpClient(handler);

        try
        {
            // New providers, queues, stores and sessions share only the destination.
            // The second session must recover the committed medium without HTTP.
            await RunSessionAsync(transport, handler, directory, run: 1);
            await RunSessionAsync(transport, handler, directory, run: 2);
            if (handler.RequestCount != 1)
                throw new InvalidOperationException("Restoring verified media issued another request.");

            await CheckIndependentArchiveAsync(directory);
            Console.WriteLine("Package consumer passed: verified download, manifest restore, independent SQLite archive, and clean shutdown.");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task CheckIndependentArchiveAsync(string directory)
    {
        string profile = Path.Combine(directory, "profile");
        ResourceId id = new ResourceId("demo", "demo", "1");
        const string capturedBody = "Package-only offline post<a href=\"#1\">&gt;&gt;1</a>";
        string mediaDirectory = StorageLayout.GetThreadDirectory(directory, id.Source, id.Board, id.Thread!);
        using (ProfileLease lease = ProfileLease.Acquire(profile))
        {
            await using ThreadContentRuntime content = new ThreadContentRuntime(profile, lease);
            await content.InitializeAsync();
            await content.Catalog.RegisterThreadAsync(new ArchiveThreadRegistration(id,
                "https://demo.invalid/demo/thread/1", "Offline package archive", mediaDirectory, true));
            await content.Catalog.CaptureAsync(new CapturedThreadSnapshot(id,
                "https://demo.invalid/demo/thread/1", "Offline package archive", ThreadSourceState.Archived,
                DateTimeOffset.UtcNow, mediaDirectory, new[]
                {
                    new CapturedPost("1", 0, PostFieldPresence.Html | PostFieldPresence.Attachments, Html: capturedBody,
                        Attachments: new[] { new CapturedAttachment(0, "1", ExpectedSize: DemoPayload.Bytes.Length, ExpectedMd5: DemoPayload.Md5) })
                }));
        }
        // The reader outlives the writer and profile lease, with no provider or UI dependency.
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(profile);
        ArchivedThreadView saved = await reader.ReadThreadAsync(id)
            ?? throw new InvalidOperationException("The packaged catalog lost its thread.");
        if (saved.PostCount != 1 || saved.GetPosts(0, 1).Single().Html != capturedBody
            || saved.Summary.SourceState != ThreadSourceState.Archived)
            throw new InvalidOperationException("The packaged independent reader did not recover the catalog.");
        ArchiveThreadPage matches = await reader.SearchThreadsAsync(new ArchiveThreadQuery("PACKAGE-ONLY", Source: "demo", Board: "demo", HasPosts: true));
        ArchivePostId postId = new ArchivePostId(id, "1");
        ArchiveQuote quote = (await reader.GetQuotesAsync(postId)).Items.Single();
        ArchiveQuoteResolution resolution = await reader.ResolveQuoteAsync(quote);
        if (matches.TotalCount != 1 || matches.Items.Single().Id != id || resolution.State != ArchiveQuoteState.Captured
            || resolution.PostIndex != 0 || (await reader.GetBacklinksAsync(postId)).TotalCount != 1)
            throw new InvalidOperationException("The packaged indexed search/quote APIs did not resolve retained local content.");
        ArchiveHtmlExportResult exported = await ArchiveHtmlExporter.ExportAsync(reader, id, Path.Combine(directory, "html-export"));
        if (exported.PostCount != 1 || exported.FileCount != 1 || exported.Omissions.Count != 0
            || !File.Exists(exported.IndexPath) || !File.Exists(Path.Combine(exported.Directory, "style.css")))
            throw new InvalidOperationException("The packaged portable HTML export did not publish a complete local folder.");
    }

    private static async Task RunSessionAsync(HttpClient transport, DemoHandler handler, string directory, int run)
    {
        DemoProvider provider = new DemoProvider(transport);
        MediaStore store = new MediaStore();
        DownloadQueue queue = new DownloadQueue(2, 1024 * 1024, store);
        await using Registry session = new Registry(
            new IImageBoard[] { provider }, queue, directory,
            TimeSpan.FromHours(1), boardIntervalSeconds: 3600, persistSession: false);

        AdmissionResult admission = await session.AddAsync("https://demo.invalid/demo/thread/1");
        if (admission.Status != AdmissionStatus.Added || admission.Thread is not DemoThread thread)
            throw new InvalidOperationException("The custom provider's thread was not admitted.");

        // AddAsync starts monitoring. The queue may still be idle until its first poll.
        await thread.FirstPollEnqueued.WaitAsync(TimeSpan.FromSeconds(10));
        await queue.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));
        string destination = Path.Combine(thread.DownloadDirectory, "1.bin");
        byte[] saved = await File.ReadAllBytesAsync(destination);
        if (!saved.AsSpan().SequenceEqual(DemoPayload.Bytes) || !thread.IsMediaDownloaded("1"))
            throw new InvalidOperationException("The download did not produce verified media.");
        ThreadSnapshot snapshot = session.GetSnapshot().Threads.Single();
        if (snapshot.KnownMedia != 1 || snapshot.CompletedMedia != 1 || snapshot.MediaFailures.Count != 0
            || snapshot.Downloads.Queued != 0 || snapshot.Downloads.Active != 0)
            throw new InvalidOperationException("The exposed progress does not match the saved medium.");

        ConsumerProgress progress = new ConsumerProgress(run, snapshot, saved.Length, handler.RequestCount);
        JsonSerializerOptions jsonOptions = new JsonSerializerOptions();
        jsonOptions.Converters.Add(new JsonStringEnumConverter());
        Console.WriteLine(JsonSerializer.Serialize(progress, jsonOptions));

        await session.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
        if (session.Threads.Count != 0 || session.Boards.Count != 0)
            throw new InvalidOperationException("Stopped watches remained active.");
    }

    private sealed record ConsumerProgress(int Run, ThreadSnapshot Thread, int SavedBytes, int MediaRequests);
}

internal static class DemoPayload
{
    public static readonly byte[] Bytes = Encoding.UTF8.GetBytes("libmeowchan standalone consumer\n");
    public static readonly string Md5 = Convert.ToBase64String(MD5.HashData(Bytes));
}

internal sealed class DemoHandler : HttpMessageHandler
{
    private int _requests;
    public int RequestCount => Volatile.Read(ref _requests);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Method != HttpMethod.Get || request.RequestUri?.AbsoluteUri != "https://demo.invalid/media/1.bin")
            throw new InvalidOperationException("The offline example received an unexpected HTTP request.");

        Interlocked.Increment(ref _requests);
        HttpResponseMessage response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(DemoPayload.Bytes)
        };
        return Task.FromResult(response);
    }
}

internal sealed class DemoProvider : BaseImageBoard
{
    public DemoProvider(HttpClient transport) : base(transport) { }
    public override string Name => "demo";
    public override string NiceName => "Offline example";
    public override Uri BaseUri => new Uri("https://demo.invalid/");
    public override bool CanHandle(Uri uri) => uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps
        && uri.IsDefaultPort && uri.Host == "demo.invalid" && string.IsNullOrEmpty(uri.UserInfo);
    public override bool IsThreadUri(Uri uri) => CanHandle(uri) && uri.AbsolutePath == "/demo/thread/1";
    public override bool IsBoardUri(Uri uri) => CanHandle(uri) && uri.AbsolutePath == "/demo/";
    public override IBoard GetBoard(Uri uri) => new DemoBoard(this);
    public override IThread GetThread(Uri uri) => new DemoThread(new DemoBoard(this));
}

internal sealed class DemoBoard : BaseBoard
{
    public DemoBoard(IImageBoard provider) => ImageBoard = provider;
    public override IImageBoard ImageBoard { get; }
    public override string BoardCode => "demo";
    public override string Name { get; protected set; } = "Demo board";
    public override string NiceName => Name;
    public override Uri BoardUri => new Uri(ImageBoard.BaseUri, "/demo/");

    public override Task<IEnumerable<IThread>> GetThreadsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IEnumerable<IThread> threads = new IThread[] { new DemoThread(this) };
        return Task.FromResult(threads);
    }
}

internal sealed class DemoThread : BaseThread
{
    private readonly TaskCompletionSource _firstPoll = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    public DemoThread(IBoard board) => Board = board;
    public Task FirstPollEnqueued => _firstPoll.Task;
    public override IBoard Board { get; }
    public override string ThreadId => "1";
    public override string NiceName => "Demo thread";
    public override Uri ThreadUri => new Uri(Board.ImageBoard.BaseUri, "/demo/thread/1");

    public override Task checkThreadAsync(DownloadQueue queue, CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Title = "Package-only custom provider";
            TotalMediaCount = 1;
            Activity = ThreadActivity.Downloading;
            string path = StorageLayout.GetMediaPath(OutputDirectory, Board.ImageBoard.Name,
                Board.BoardCode, ThreadId, "1", ".bin");
            DownloadItem item = new DownloadItem(new Uri("https://demo.invalid/media/1.bin"), path,
                Board.ImageBoard, this, "1", expectedSize: DemoPayload.Bytes.Length,
                expectedMd5: DemoPayload.Md5, originalFileName: "sample.bin");
            if (!queue.TryEnqueue(item))
                throw new InvalidOperationException("The demo media was not admitted to the queue.");

            _firstPoll.TrySetResult();
            return Task.CompletedTask;
        }
        catch (Exception exception)
        {
            _firstPoll.TrySetException(exception);
            throw;
        }
    }
}
