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
using MeowChan.Tags;

// One current-side process per open. Never seeds, repairs, rebinds or rewrites old inputs.
// Modes (optional third argument, default "observe"; the xunit upgrade test only uses "observe"):
//   observe          read-only open of the upgraded profile (cycles 0, 1, 2).
//   write-host       after the observation: change settings (also queue options), re-assert the pause state and
//                    create a thread tag through the host (first tag write creates private-tags.json, version 1).
//   write-local-tag  tag one real manifest asset as a loose file at STORE level (version 1 -> 2, v1 backup). The
//                    synthetic profile has no loose file, so host revalidation does not apply; reported as such.
//   readback         read-only open that also reports the tag state (an offline restart after the writes).
//   media-index      builds the media page index the way MediaPage does and saves the cache file
//                    (profile/cache/media-index-v1.json), then reads it back. A 1.0.2 profile has no cache file,
//                    so this is the cold first build, not a v3 -> v4 migration.
if (args.Length is not (2 or 3)) throw new ArgumentException("Expected existing released profile, new result file and optional mode.");
string profile = Path.GetFullPath(args[0]);
string result = Path.GetFullPath(args[1]);
string mode = args.Length == 3 ? args[2] : "observe";
if (mode is not ("observe" or "write-host" or "write-local-tag" or "readback" or "media-index"))
    throw new ArgumentException("Unknown mode: " + mode);
const string ThreadTagName = "ap04-thread-tag";
const string LocalTagName = "ap04-local-tag";
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
object? plugins = null;
object? writes = null;
object? hostTags = null;
LocalMediaObservation? localObservation = null;
List<ResourceId> threadIds = new();
Exception? primaryFailure = null;
await using DesktopSessionHost host = new(profile,
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
            threadIds.Add(id);
            ArchivedThreadView view = await reader.ReadThreadAsync(id).WaitAsync(deadline)
                ?? throw new InvalidDataException("Missing archived thread: " + id);
            MediaAssetSnapshot manifest = await MediaAssetReader.OpenAsync(view.Summary.MediaDirectory).WaitAsync(deadline);
            if (manifest.Status != MediaManifestStatus.Available) throw new InvalidDataException("Unavailable media manifest: " + id);
            List<object> assets = new();
            foreach (MediaAssetReference asset in manifest.GetPage())
            {
                await using Stream bytes = await manifest.OpenVerifiedReadAsync(asset.AssetId).WaitAsync(deadline);
                string sha256 = Convert.ToHexString(await SHA256.HashDataAsync(bytes));
                bytes.Position = 0;
                string sha1 = Convert.ToBase64String(await SHA1.HashDataAsync(bytes));
                assets.Add(new { Reference = asset, bytes.Length, Sha256 = sha256, Sha1 = sha1 });
                if (localObservation is null)
                    LocalMediaObservation.TryCreate(id, manifest.Directory, asset.RequestedFileName, asset.AssetId, asset.Length, asset.Sha256,
                        out localObservation);
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
    plugins = await PluginsAsync(host, deadline);
    if (mode == "write-host") writes = await WriteHostAsync(host, threadIds, deadline);
    if (mode == "media-index") writes = await MediaIndexAsync(host, profile, deadline);
    if (mode != "observe") hostTags = HostTags(host, threadIds);
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
object? persistedTags = null;
using (ProfileLease lease = ProfileLease.Acquire(profile))
{
    SessionLoadResult watch = await new SessionStore(profile, lease).LoadAsync().WaitAsync(deadline);
    persisted = new
    {
        Watch = watch.Snapshot, watch.Recovered, watch.Warning, Settings = Settings(Config.LoadConfig(profile, lease)),
        MediaFavorites = new FavoriteStore(profile, lease).List(),
        Ratings = new MediaRatingStore(profile, lease).List(),
        ThreadFavorites = new ThreadFavoriteStore(profile, lease).ReadSnapshot(),
        Bookmarks = PostBookmarkStore.ReadOnly(profile, lease),
        // Stores the released 1.0.2 profile also carries; the upgrade must keep them readable, not only present.
        SavedSearches = SavedSearches(profile, lease),
        ReaderGroups = await ReaderGroupsAsync(profile, lease, deadline),
        // Every setting as the upgraded profile reads it, next to the documented defaults of this build, so a new
        // setting that silently became 0 instead of its default is visible.
        AllSettings = AllSettings(Config.LoadConfig(profile, lease)),
        DefaultSettings = AllSettings(new Config())
    };
    if (mode == "write-local-tag")
    {
        if (localObservation is null) throw new InvalidOperationException("No manifest asset to tag in the upgraded profile.");
        writes = WriteLocalTag(profile, lease, localObservation);
    }
    // The tag store is read only in the later modes: "observe" must leave no tag state behind or read.
    if (mode != "observe") persistedTags = StoreTags(profile, lease, threadIds);
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
    Profile = profile, HostStopped = true, NetworkRequests = offline.Requests.ToArray(), Observed = observed, Persisted = persisted,
    Mode = mode, Plugins = plugins, Writes = writes, HostTags = hostTags, PersistedTags = persistedTags
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

static async Task<object> PluginsAsync(DesktopSessionHost host, TimeSpan deadline)
{
    // The same plugin session the application uses; the consumer directory carries the delivered plugins/ tree.
    var session = host.Plugins ?? throw new InvalidOperationException("The plugin session is not available.");
    var snapshot = await session.ProfileStore.ReadAsync().WaitAsync(deadline);
    return new
    {
        snapshot.Revision,
        Warnings = session.Warnings.ToArray(),
        Sources = session.Sources.Select(source => source.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray(),
        Plugins = snapshot.Plugins.OrderBy(plugin => plugin.PluginId, StringComparer.Ordinal).Select(plugin => new
        {
            plugin.PluginId, plugin.Enabled, Active = plugin.Active != null, ActiveExecutionRevision = plugin.Active?.ExecutionRevision,
            plugin.Problem,
            SdkTransition = plugin.AutomaticSdkTransition is { } transition ? transition.FromSdkVersion + "->" + transition.ToSdkVersion : null
        }).ToArray()
    };
}

static async Task<object> WriteHostAsync(DesktopSessionHost host, IReadOnlyList<ResourceId> threadIds, TimeSpan deadline)
{
    if (threadIds.Count == 0) throw new InvalidOperationException("The profile has no archived thread to tag.");
    Config before = host.Config;
    int volumeBefore = before.VideoVolume, parallelBefore = before.MaxParallelDownloads;
    // Settings write that also reconfigures the download queue (options + saved configuration, one admitted command).
    Config settings = host.Config.Clone();
    settings.VideoVolume = volumeBefore == 47 ? 46 : 47;
    settings.MaxParallelDownloads = parallelBefore + 1;
    await host.ApplySettingsAsync(settings).WaitAsync(deadline);
    // Pause state re-asserted: unpausing is deliberately not done, it would start the watch producers (network).
    await host.SetPausedAsync(true).WaitAsync(deadline);
    var defined = await host.DefinePrivateTagAsync(ThreadTagName).WaitAsync(deadline);
    var assigned = await host.SetThreadTagAsync(defined.Tag, threadIds[0], true).WaitAsync(deadline);
    return new
    {
        Settings = new { VolumeBefore = volumeBefore, VolumeAfter = host.Config.VideoVolume, ParallelBefore = parallelBefore,
            ParallelAfter = host.Config.MaxParallelDownloads },
        IsPaused = host.IsPaused,
        Tag = new { Name = ThreadTagName, Id = defined.Tag.Value, Thread = threadIds[0], DefinedChanged = defined.Changed,
            AssignedChanged = assigned.Changed, Revision = assigned.Snapshot.Revision, assigned.Snapshot.FormatVersion }
    };
}

static object WriteLocalTag(string profile, ProfileLease lease, LocalMediaObservation observation)
{
    // Store-level on purpose: the synthetic profile has no loose file. The host revalidation (loose-file check) is not run.
    PrivateTagStore store = new(profile, lease);
    PrivateTagCommit commit = store.DefineAndSetMediaSubjects(LocalTagName, new MediaTagSubject[] { new MediaTagSubject.Local(observation, null) });
    return new
    {
        Level = "store", HostRevalidation = false, Name = LocalTagName, commit.Changed, Tag = commit.Tag.Value,
        Revision = commit.Snapshot.Revision, commit.Snapshot.FormatVersion, LocalAssignments = commit.Snapshot.LocalAssignments.Count,
        Locator = new { Thread = observation.Locator.Thread, observation.Locator.Directory, observation.Locator.RequestedFileName },
        observation.AssetId, observation.Length, observation.Sha256
    };
}

static object HostTags(DesktopSessionHost host, IReadOnlyList<ResourceId> threadIds)
{
    var loaded = host.GetPrivateTagSnapshot();
    var snapshot = loaded.Snapshot;
    return new
    {
        State = loaded.State.ToString(), loaded.Error, Revision = snapshot?.Revision, FormatVersion = snapshot?.FormatVersion,
        Tags = snapshot?.Tags.Select(tag => tag.Name.Display).OrderBy(name => name, StringComparer.Ordinal).ToArray(),
        ThreadTags = threadIds.Select(id => new { Thread = id, Tags = snapshot?.TagsOf(id).Select(tag => tag.Name.Display).ToArray() }).ToArray(),
        ThreadAssignments = snapshot?.ThreadAssignments.Count, MediaAssignments = snapshot?.MediaAssignments.Count,
        LocalAssignments = snapshot?.LocalAssignments.Count
    };
}

static object StoreTags(string profile, ProfileLease lease, IReadOnlyList<ResourceId> threadIds)
{
    var snapshot = new PrivateTagStore(profile, lease).ReadSnapshot();
    return new
    {
        snapshot.Revision, snapshot.FormatVersion, Tags = snapshot.Tags.Select(tag => tag.Name.Display).OrderBy(name => name, StringComparer.Ordinal).ToArray(),
        ThreadAssignments = snapshot.ThreadAssignments.Count, MediaAssignments = snapshot.MediaAssignments.Count,
        LocalAssignments = snapshot.LocalAssignments.Count,
        ThreadTags = threadIds.Select(id => new { Thread = id, Tags = snapshot.TagsOf(id).Select(tag => tag.Name.Display).ToArray() }).ToArray()
    };
}

static object SavedSearches(string profile, ProfileLease lease)
{
    SavedArchiveSearchSnapshot snapshot = new SavedArchiveSearchStore(profile, lease).Load();
    return new
    {
        State = snapshot.State.ToString(), snapshot.Error, snapshot.Revision, Count = snapshot.Searches.Count,
        Searches = snapshot.Searches.Select(search => new { search.Id, search.Name, search.CanApply, search.UnsupportedReason,
            Text = search.Definition?.Text, Sources = search.Definition?.Sources }).ToArray()
    };
}

static async Task<object> ReaderGroupsAsync(string profile, ProfileLease lease, TimeSpan deadline)
{
    ReaderGroupsLoadResult loaded = await new ReaderGroupStore(profile, lease).LoadAsync().WaitAsync(deadline);
    return new
    {
        State = loaded.State.ToString(), loaded.Error, Revision = loaded.Snapshot?.Revision,
        Count = loaded.Snapshot?.Groups.Count,
        Groups = loaded.Snapshot?.Groups.Select(group => new { group.Id, group.Name, Members = group.Members.ToArray() }).ToArray()
    };
}

// Every simple public setting by name. Complex members (dictionaries, layout profiles) are left out on purpose:
// the file comparison already covers them byte for byte.
static SortedDictionary<string, object?> AllSettings(Config settings)
{
    SortedDictionary<string, object?> values = new(StringComparer.Ordinal);
    foreach (PropertyInfo property in typeof(Config).GetProperties(BindingFlags.Public | BindingFlags.Instance))
    {
        if (property.GetIndexParameters().Length != 0 || !property.CanRead) continue;
        Type type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
        if (!type.IsPrimitive && !type.IsEnum && type != typeof(string) && type != typeof(decimal)) continue;
        object? value = property.GetValue(settings);
        values[property.Name] = value is Enum ? value.ToString() : value;
    }
    return values;
}

static async Task<object> MediaIndexAsync(DesktopSessionHost host, string profile, TimeSpan deadline)
{
    // The same build and save the media page performs on its first open.
    string path = Path.Combine(profile, "cache", MeowChan.Gallery.MediaLibraryIndexCache.FileName);
    bool existedBefore = File.Exists(path);
    MeowChan.Gallery.MediaLibrarySnapshot built = await MeowChan.Gallery.MediaLibraryIndexBuilder
        .BuildAsync(host.OpenArchiveReaderAsync, MediaAssetReader.OpenAsync).WaitAsync(deadline);
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    MeowChan.Gallery.MediaLibraryIndexCacheWrite write = await MeowChan.Gallery.MediaLibraryIndexCache
        .SaveAsync(path, built, host.Config.MediaIndexCacheLimitMiB * 1024L * 1024).WaitAsync(deadline);
    MeowChan.Gallery.MediaLibraryIndexCacheRead? read = await MeowChan.Gallery.MediaLibraryIndexCache.LoadAsync(path).WaitAsync(deadline);
    using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(path));
    return new
    {
        CacheFile = Path.GetRelativePath(profile, path).Replace(Path.DirectorySeparatorChar, '/'),
        ExistedBefore = existedBefore, WrittenVersion = document.RootElement.GetProperty("Version").GetInt32(),
        Status = write.Status.ToString(), write.Bytes, write.LimitBytes,
        BuiltEntries = built.Entries.Count, BuiltThreads = built.Threads.Count,
        ReadBackEntries = read?.Snapshot.Entries.Count, ReadBackThreads = read?.Snapshot.Threads.Count,
        Files = built.Entries.Select(entry => entry.Name + "|" + entry.Sha256 + "|" + entry.Length)
            .OrderBy(name => name, StringComparer.Ordinal).ToArray()
    };
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
