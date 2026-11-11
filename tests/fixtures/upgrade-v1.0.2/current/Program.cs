using System.Collections.Concurrent;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using MeowChan;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Updates;
using MeowChan.Favorites;
using MeowChan.Ratings;
using MeowChan.Persistence;
using MeowChan.Storage;

// One current-side process per open. Never seeds, repairs, rebinds or rewrites old inputs.
if (args.Length != 2) throw new ArgumentException("Expected existing released profile and new result file.");
string profile = Path.GetFullPath(args[0]);
string result = Path.GetFullPath(args[1]);
if (!Directory.Exists(profile) || File.Exists(result) || File.Exists(result + ".pending"))
    throw new InvalidOperationException("Requires an existing profile and unused result paths.");
using Timer watchdog = new(_ =>
{
    Console.Error.WriteLine("CURRENT CONSUMER TIMEOUT; keep the partial attempt, generate a fresh profile.");
    Environment.Exit(124);
}, null, TimeSpan.FromSeconds(210), Timeout.InfiniteTimeSpan);
TimeSpan deadline = TimeSpan.FromSeconds(90);
using JsonDocument receipt = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(profile, "v102-receipt.json")));
using Offline offline = new();
object observed;
Exception? primaryFailure = null;
DesktopSessionHost host = new(profile,
    updateSessionFactory: (path, config) => new UpdateSession(path, config, offline.Client(), disableEnvironment: () => "1"),
    pluginTransportFactory: offline.Client);
try
{
    await host.StartAsync().WaitAsync(deadline);
    if (host.State != DesktopSessionState.Ready)
        throw new InvalidOperationException("Current startup failed: " + host.StartupError);
    List<object> threads = new();
    await using (ArchiveReader reader = await host.OpenArchiveReaderAsync().WaitAsync(deadline))
    {
        ArchiveThreadPage all = await reader.ListThreadsAsync().WaitAsync(deadline);
        foreach (JsonElement old in receipt.RootElement.GetProperty("Threads").EnumerateArray())
        {
            ResourceId id = old.GetProperty("Id").Deserialize<ResourceId>()!;
            ArchivedThreadView view = await reader.ReadThreadAsync(id).WaitAsync(deadline)
                ?? throw new InvalidDataException("Missing archived thread: " + id);
            MediaAssetSnapshot manifest = await MediaAssetReader.OpenAsync(view.Summary.MediaDirectory).WaitAsync(deadline);
            if (manifest.Status != MediaManifestStatus.Available) throw new InvalidDataException("Unavailable media manifest: " + id);
            List<object> assets = new();
            foreach (MediaAssetReference asset in manifest.GetPage())
            {
                await using Stream bytes = await manifest.OpenVerifiedReadAsync(asset.AssetId).WaitAsync(deadline);
                assets.Add(new { Reference = asset, bytes.Length, Sha256 = Convert.ToHexString(await SHA256.HashDataAsync(bytes)) });
            }
            var exports = await host.ExportHistory.ListVerifiedAsync(id).WaitAsync(deadline);
            threads.Add(new { Id = id, view.Summary.Url, view.Summary.MediaDirectory, Posts = view.GetPosts(), Assets = assets,
                Exports = exports.Select(export => new { export.Entry, export.Inspection }).ToArray() });
        }
        observed = new
        {
            IsPaused = host.IsPaused,
            Settings = Settings(host.Config),
            MediaFavorites = host.GetFavorites(),
            ThreadFavorites = host.GetThreadFavoriteSnapshot().Snapshot
                ?? throw new InvalidDataException("Thread favorites unavailable."),
            Ratings = host.GetMediaRatings(),
            Bookmarks = await host.ReadPostBookmarksAsync().WaitAsync(deadline),
            HostThreads = host.GetView().Threads.Select(thread => new { thread.Snapshot.Id, thread.Snapshot.Url }).ToArray(),
            HostBoardUrls = host.GetView().Boards.Select(board => board.Snapshot.Url).ToArray(),
            Work = host.GetThreadWorkSnapshots(),
            ArchiveIds = all.Items.Select(thread => thread.Id).ToArray(),
            ArchiveTotal = all.TotalCount,
            Threads = threads
        };
    }
}
catch (Exception error)
{
    primaryFailure = error;
    throw;
}
finally
{
    // Do not publish a successful snapshot until every producer, plugin worker and lease has drained.
    try { await host.DisposeAsync().AsTask().WaitAsync(deadline); }
    catch (Exception cleanup) when (primaryFailure != null)
    {
        Console.Error.WriteLine("Additional disposal failure (primary failure retained): " + cleanup);
    }
}
if (host.State != DesktopSessionState.Stopped) throw new InvalidOperationException("Host did not stop.");
object persisted;
using (ProfileLease lease = ProfileLease.Acquire(profile))
{
    SessionLoadResult watch = await new SessionStore(profile, lease).LoadAsync().WaitAsync(deadline);
    persisted = new
    {
        Watch = watch.Snapshot, watch.Recovered, watch.Warning, Settings = Settings(Config.LoadConfig(profile, lease)),
        MediaFavorites = new FavoriteStore(profile, lease).List(),
        Ratings = new MediaRatingStore(profile, lease).List(),
        ThreadFavorites = new ThreadFavoriteStore(profile, lease).ReadSnapshot(),
        Bookmarks = PostBookmarkStore.ReadOnly(profile, lease)
    };
}
if (!offline.Requests.IsEmpty)
    throw new InvalidOperationException("Current consumer attempted network: " + string.Join(", ", offline.Requests));
byte[] output = JsonSerializer.SerializeToUtf8Bytes(new
{
    Schema = "meowchan.released-102-current-cycle.v1",
    ProcessId = Environment.ProcessId,
    CurrentAssemblySha256 = AssemblyHash(typeof(Config).Assembly),
    CurrentCoreAssemblySha256 = AssemblyHash(typeof(ArchiveReader).Assembly),
    CurrentVersion = typeof(Config).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
    Profile = profile, HostStopped = true, NetworkRequests = offline.Requests.ToArray(), Observed = observed, Persisted = persisted
}, new JsonSerializerOptions { WriteIndented = true });
using (FileStream stream = new(result + ".pending", FileMode.CreateNew, FileAccess.Write, FileShare.None))
{
    stream.Write(output);
    stream.Flush(flushToDisk: true);
}
File.Move(result + ".pending", result);
Console.WriteLine("CURRENT OFFLINE CYCLE OBSERVED; receipt assertions belong to Released102RestPackageUpgradeTests.");

static string AssemblyHash(Assembly assembly)
{
    using FileStream input = File.OpenRead(assembly.Location);
    return Convert.ToHexString(SHA256.HashData(input));
}

static object Settings(Config settings) => new
{
    settings.DownloadsPaused, settings.VideoVolume, settings.VideoStartMuted,
    settings.GalleryContextOpen, settings.AutomaticUpdateChecks, settings.DownloadPath,
    settings.LastExportDirectory, settings.ArchiveThreadLayout, settings.ArchiveSplitWidth, settings.WindowWidth
};

sealed class Offline : HttpMessageHandler
{
    internal ConcurrentQueue<string> Requests { get; } = new();
    internal HttpClient Client() => new(this, disposeHandler: false);
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        Requests.Enqueue(request.Method + " " + request.RequestUri);
        throw new InvalidOperationException("Released upgrade consumer forbids all HTTP: " + request.RequestUri);
    }
}
