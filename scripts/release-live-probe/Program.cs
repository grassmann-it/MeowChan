using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using MeowChan;
using MeowChan.Archive;
using MeowChan.Boards.FourChan;
using MeowChan.Boards.EightKun;
using MeowChan.Downloader;
using MeowChan.Http;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Rules;
using MeowChan.Storage;

if (args.Length != 3) throw new ArgumentException("Expected fresh output directory and two thread URLs.");
string root = Path.GetFullPath(args[0]);
string fourChanUrl = args[1];
string eightKunUrl = args[2];
Directory.CreateDirectory(root);
JsonSerializerOptions json = new() { WriteIndented = true };
json.Converters.Add(new JsonStringEnumConverter());
List<object> results = new();
bool allPassed = true;
foreach ((string source, string url) in new[] {
    ("4chan", fourChanUrl),
    ("8kun", eightKunUrl) })
{
    AuditHandler audit = new(source, url);
    using HttpClient transport = new(audit) { Timeout = Timeout.InfiniteTimeSpan };
    using HttpClient client = ProviderHttpClientFactory.CreatePolicyClient(transport, source, new RequestPolicyState());
    string profile = Path.Combine(root, source, "profile");
    string mediaRoot = Path.Combine(root, source, "downloads");
    if (Directory.Exists(profile)) throw new InvalidOperationException("Initial live check requires a new isolated profile.");
    bool passed = false;
    object? detail = null;
    try
    {
        {
            const int run = 1;
            using ProfileLease lease = ProfileLease.Acquire(profile);
            await using ThreadContentRuntime content = new(profile, lease);
            await content.InitializeAsync();
            if (run == 1) await content.Rules.SaveAsync(new[] { new MediaRule(MediaRuleScope.Global,
                types: new MediaTypeSelection(MediaTypeSelectionMode.Selected, new[] { ".jpg", ".png" }),
                sizeLimit: new MediaSizeLimit(MediaSizeLimitMode.Limited, 200_000)) });
            DownloadQueue queue = new(2, 1_048_576, maxPendingDownloads: 8, mediaStore: new MediaStore());
            IImageBoard provider = source == "4chan" ? new FourChanImageBoard(client) : new EightKunImageBoard(client);
            await using Registry session = new(new[] { provider }, queue, mediaRoot, TimeSpan.FromHours(1), 3600,
                new SessionStore(profile, lease), true, contentRuntime: content, workStore: new WorkStateStore(profile, lease));
            await session.RestoreAsync();
            if (run == 1)
            {
                AdmissionResult admission = await session.AddAsync(url);
                if (admission.Status != AdmissionStatus.Added) throw new InvalidOperationException("Admission: " + admission.Status);
            }
            await ReadyAsync(session, 90);
            ThreadSnapshot state = session.GetSnapshot().Threads.Single();
            await using ArchiveReader reader = await content.OpenReaderAsync();
            ArchivedThreadView view = await reader.ReadThreadAsync(state.Id) ?? throw new InvalidOperationException("No captured archive.");
            if (view.PostCount == 0) throw new InvalidOperationException("No captured posts.");
            if ((await reader.SearchThreadsAsync(new ArchiveThreadQuery(state.Id.Thread!, Source: source))).TotalCount != 1)
                throw new InvalidOperationException("Live archive search failed.");
            int files = 0;
            long bytes = 0;
            foreach (string directory in view.MediaDirectories)
            {
                MediaAssetSnapshot manifest = await MediaAssetReader.OpenAsync(directory);
                foreach (MediaAssetReference asset in manifest.GetPage(0, 500))
                {
                    await using Stream stream = await manifest.OpenVerifiedReadAsync(asset.AssetId);
                    bytes += stream.Length;
                    files++;
                }
            }
            if (files != 1 || state.SelectedMedia != 1 || audit.MediaCount != 1) throw new InvalidOperationException("No verified saved files.");
            ArchiveHtmlExportResult export = await ArchiveHtmlExporter.ExportAsync(reader, state.Id, Path.Combine(root, source, "html-run-" + run));
            if (export.FileCount < 1 || export.Omissions.Count != 0) throw new InvalidOperationException("Live export has missing attachments.");
            detail = new { Url = url, Run = run, Posts = view.PostCount, Files = files, Bytes = bytes,
                state.CompletedMedia, state.SelectedMedia, HTTPMediaRequests = audit.MediaCount, HTTPThreadRequests = audit.ThreadCount,
                HTTPBoardMetadataRequests = audit.BoardMetadataCount,
                Search = true, Export = export.IndexPath };
            Console.WriteLine(JsonSerializer.Serialize(new { Source = source, Detail = detail }, json));
            await session.StopAsync().WaitAsync(TimeSpan.FromSeconds(30));
            if (audit.ThreadCount != 1 || audit.MediaCount != 1 || audit.BoardMetadataCount > 1)
                throw new InvalidOperationException("Live request counts exceeded the provider budget.");
        }
        passed = true;
    }
    catch (Exception exception) { detail = new { Error = exception.ToString() }; Console.WriteLine(source + " FAILED " + exception.Message); }
    finally
    {
        object report = new { Source = source, Passed = passed, Detail = detail, Requests = audit.Entries.ToArray() };
        results.Add(report);
        allPassed &= passed;
        await File.WriteAllTextAsync(Path.Combine(root, source + "-result.json"), JsonSerializer.Serialize(report, json));
    }

}
await File.WriteAllTextAsync(Path.Combine(root, "results.json"), JsonSerializer.Serialize(results, json));

if (!allPassed) Environment.ExitCode = 1;

static async Task ReadyAsync(Registry session, int seconds)
{
    DateTime deadline = DateTime.UtcNow.AddSeconds(seconds);
    while (DateTime.UtcNow < deadline)
    {
        ThreadSnapshot? state = session.GetSnapshot().Threads.SingleOrDefault();
        if (state?.LastCheckFailure != null) throw new InvalidOperationException("Provider check failed: " + state.LastCheckFailure);
        if (state?.MediaFailures.Count > 0) throw new InvalidOperationException("Media failed: " + string.Join("; ", state.MediaFailures.Values));
        if (state != null && state.KnownMedia > 0 && state.CompletedMedia >= state.SelectedMedia
            && state.Downloads.Queued == 0 && state.Downloads.Active == 0 && state.RequestProgress == null
            && state.Activity != ThreadActivity.Checking) return;
        await Task.Delay(200);
    }
    throw new TimeoutException("Live session did not reach a completed download state.");
}

sealed record HttpEntry(DateTimeOffset At, string Url, int? Status, bool Conditional, string? Error = null);
sealed class AuditHandler : DelegatingHandler
{
    public ConcurrentQueue<HttpEntry> Entries { get; } = new();
    private int _media;
    private int _threads;
    private int _boardMetadata;
    private readonly string _apiHost;
    private readonly string _mediaHost;
    private readonly string _threadPath;
    public int MediaCount => Volatile.Read(ref _media);
    public int ThreadCount => Volatile.Read(ref _threads);
    public int BoardMetadataCount => Volatile.Read(ref _boardMetadata);
    public AuditHandler(string source, string threadUrl) : base(new SocketsHttpHandler { AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli })
    {
        _apiHost = source == "4chan" ? "a.4cdn.org" : "8kun.top";
        _mediaHost = source == "4chan" ? "i.4cdn.org" : "nerv.8kun.top";
        string path = new Uri(threadUrl).AbsolutePath;
        _threadPath = source == "4chan" ? path + ".json" : path.Replace(".html", ".json", StringComparison.Ordinal);
    }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        Uri uri = request.RequestUri!;
        bool media = uri.Host == _mediaHost;
        bool thread = uri.Host == _apiHost && uri.AbsolutePath == _threadPath;
        bool metadata = uri.Host == _apiHost && uri.AbsolutePath == "/boards.json";
        try
        {
            if (request.Method != HttpMethod.Get || uri.Scheme != "https" || !uri.IsDefaultPort
                || uri.Query.Length != 0 || !(media || thread || metadata))
                throw new InvalidOperationException("Outside authorized live test endpoints.");
            if ((media && Interlocked.Increment(ref _media) > 1)
                || (thread && Interlocked.Increment(ref _threads) > 1)
                || (metadata && Interlocked.Increment(ref _boardMetadata) > 1))
                throw new InvalidOperationException("Live per-endpoint request budget exceeded.");
            using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(30));
            HttpResponseMessage response = await base.SendAsync(request, deadline.Token);
            try
            {
                // Bound actual decompressed response bytes, including a dishonest Content-Length.
                await response.Content.LoadIntoBufferAsync(media ? 200_000 : 2_097_152, deadline.Token);
            }
            catch { response.Dispose(); throw; }
            Entries.Enqueue(new(DateTimeOffset.UtcNow, uri.AbsoluteUri, (int)response.StatusCode, request.Headers.IfModifiedSince.HasValue));
            return response;
        }
        catch (Exception exception) { Entries.Enqueue(new(DateTimeOffset.UtcNow, uri.AbsoluteUri, null, request.Headers.IfModifiedSince.HasValue, exception.Message)); throw; }
    }
}
