using System.Net;
using System.Text.Json;
using MeowChan;
using MeowChan.Archive;
using MeowChan.Boards.FourChan;
using MeowChan.Boards.EightKun;
using MeowChan.Downloader;
using MeowChan.Http;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Storage;

string root = Path.GetFullPath(args[0]);
List<object> results = new();
foreach (string source in new[] { "4chan", "8kun" })
{
    ProbeHandler handler = new();
    using HttpClient transport = new(handler) { Timeout = Timeout.InfiniteTimeSpan };
    using HttpClient client = ProviderHttpClientFactory.CreatePolicyClient(transport, source, new RequestPolicyState());
    string profile = Path.Combine(root, source, "profile");
    using ProfileLease lease = ProfileLease.Acquire(profile);
    await using ThreadContentRuntime content = new(profile, lease);
    await content.InitializeAsync();
    IImageBoard provider = source == "4chan" ? new FourChanImageBoard(client) : new EightKunImageBoard(client);
    DownloadQueue queue = new(2, 1_048_576, new MediaStore());
    // Restored Watching intents normally resume polling immediately. This probe
    // verifies offline reopening, so pause before any restored monitor can run.
    queue.SetPaused(true);
    await using Registry session = new(new[] { provider }, queue, Path.Combine(root, source, "downloads"), TimeSpan.FromHours(1), 3600,
        new SessionStore(profile, lease), true, contentRuntime: content, workStore: new WorkStateStore(profile, lease));
    await session.RestoreAsync();
    if (!session.IsPaused) throw new InvalidOperationException("Offline restore must remain paused.");
    DateTime deadline = DateTime.UtcNow.AddSeconds(90);
    bool passed = false;
    while (DateTime.UtcNow < deadline)
    {
        ThreadSnapshot? state = session.GetSnapshot().Threads.SingleOrDefault();
        if (state?.LastCheckFailure != null || state?.MediaFailures.Count > 0) throw new InvalidOperationException("Live restore failure.");
        if (state != null && state.KnownMedia > 0 && state.CompletedMedia == state.SelectedMedia
            && state.Downloads.Active == 0 && state.Downloads.Queued == 0 && state.RequestProgress == null && state.Activity != ThreadActivity.Checking)
        {
            if (state.Work is not { Intent: WorkIntent.Watching, SavedMedia: 1 })
                throw new InvalidOperationException("Offline restore lost its active intent or saved media state.");
            if (handler.Requests.Count != 0 || handler.MediaRequests != 0 || state.SelectedMedia != 1 || state.CompletedMedia != 1) throw new InvalidOperationException("A fresh process fetched saved media again.");
            await using ArchiveReader reader = await content.OpenReaderAsync();
            ArchivedThreadView view = (await reader.ReadThreadAsync(state.Id))!;
            if (view.PostCount == 0) throw new InvalidOperationException("Restored archive has no posts.");
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
            if (files != 1 || bytes == 0) throw new InvalidOperationException("Restored media verification failed.");
            object result = new { Source = source, Passed = true, Process = Environment.ProcessId, FreshProcess = true, Files = files, Bytes = bytes,
                state.CompletedMedia, Posts = view.PostCount, Paused = session.IsPaused, Intent = state.Work.Intent.ToString(),
                SavedMedia = state.Work.SavedMedia, MediaHTTPRequests = handler.MediaRequests, Requests = handler.Requests };
            results.Add(result);
            Console.WriteLine(JsonSerializer.Serialize(result));
            passed = true;
            break;
        }
        await Task.Delay(200);
    }
    if (!passed) throw new TimeoutException("Fresh-process live restore timed out.");
    await session.StopAsync().WaitAsync(TimeSpan.FromSeconds(30));
    if (handler.Requests.Count != 0) throw new InvalidOperationException("Fresh restore attempted HTTP before shutdown.");
}
await File.WriteAllTextAsync(Path.Combine(root, "fresh-process-restore.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));

sealed class ProbeHandler : HttpMessageHandler
{
    public int MediaRequests;
    public List<object> Requests { get; } = new();
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        lock (Requests) Requests.Add(new { Url = request.RequestUri!.AbsoluteUri, Blocked = true });
        if (request.RequestUri!.Host is "i.4cdn.org" or "nerv.8kun.top") Interlocked.Increment(ref MediaRequests);
        throw new InvalidOperationException("Fresh restore attempted HTTP; all transport is blocked.");
    }
}
