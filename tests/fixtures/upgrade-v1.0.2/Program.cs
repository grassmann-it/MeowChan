using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MeowChan;
using MeowChan.Archive;
using MeowChan.Boards.FourChan;
using MeowChan.Boards.EightKun;
using MeowChan.Desktop;
using MeowChan.Favorites;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Storage;
using MeowChan.Ratings;
using System.Reflection;

const string revision = "eec6c0169f8d52f7481ea0bf7febfd20981e97c3";
string productVersion = typeof(Config).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
if (productVersion != "1.0.2+" + revision) throw new Exception("Requires pinned 1.0.2 informational version: " + productVersion);
if (args.Length != 2) throw new ArgumentException("Expected new profile path and exported UPGRADE-102-SOURCE.json path.");
byte[] provenance = File.ReadAllBytes(args[1]);
using JsonDocument provenanceDocument = JsonDocument.Parse(provenance);
if (provenanceDocument.RootElement.GetProperty("source_commit").GetString() != revision ||
    !provenanceDocument.RootElement.GetProperty("synthetic").GetBoolean() ||
    provenanceDocument.RootElement.GetProperty("schema").GetString() != "meowchan.synthetic-upgrade-102-source.v2")
    throw new Exception("Wrong synthetic source provenance.");
// Covers admission, archive/export and disposal as well as the download poll.
// Timeout is a failed attempt; retain the partial directory, never publish a receipt.
using Timer watchdog = new(_ => { Console.Error.WriteLine("SYNTHETIC GENERATION TIMEOUT (120s); retain partial output."); Environment.Exit(124); },
    null, TimeSpan.FromSeconds(120), Timeout.InfiniteTimeSpan);
string profile = Path.GetFullPath(args[0]);
if (Directory.Exists(profile) || File.Exists(profile) || new FileInfo(profile).LinkTarget != null) throw new InvalidOperationException("Generator requires a new profile path.");
Directory.CreateDirectory(profile);
// Released Config admits this first ordinary save only while the new profile is empty.
// Do not create exports/media or acquire a host/lease before settings exist.
new Config { DownloadPath = Path.Combine(profile, "media"), DownloadsPaused = true,
    AutomaticUpdateChecks = false, VideoVolume = 31, VideoStartMuted = true, GalleryContextOpen = true,
    SaveUrlsOnExit = true, MaxParallelDownloads = 2,
    RecheckTimer = 3600, CompactDensity = true,
    ArchiveThreadLayout = "Table", ArchiveSplitWidth = 431, WindowWidth = 1123,
    LastExportDirectory = Path.Combine(profile, "exports") }.SaveConfig(profile);
Directory.CreateDirectory(Path.Combine(profile, "exports"));
using Fixture transport = new();
using HttpClient client = new(transport);
List<object> threads = new();
List<FavoriteEntry> favorites = new();
await using (DesktopSessionHost host = new(profile, new IImageBoard[] { new FourChanImageBoard(client), new EightKunImageBoard(client) }))
{
    await host.StartAsync().WaitAsync(TimeSpan.FromSeconds(30));
    if (host.State != DesktopSessionState.Ready) throw new Exception("Old host not ready: " + host.StartupError);
    foreach (string url in Fixture.Urls)
        if ((await host.AddAsync(url)).Status != AdmissionStatus.Added) throw new Exception("Old source admission failed: " + url);
    await host.SetPausedAsync(false);
    using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(30));
    while (host.GetThreadWorkSnapshots().Count != 2 || host.GetThreadWorkSnapshots().Any(thread => thread.Media.Count != 1 || thread.Media.Any(media => media.Status != MeowChan.Persistence.MediaWorkStatus.Succeeded)))
        await Task.Delay(25, deadline.Token);
    await host.SetPausedAsync(true);
    await using ArchiveReader reader = await host.OpenArchiveReaderAsync();
    foreach (string source in new[] { "4chan", "8kun" })
    {
        ResourceId id = new(source, "g", "123");
        ArchivedThreadView view = (await reader.ReadThreadAsync(id))!;
        MediaAssetReference asset = (await MediaAssetReader.OpenAsync(view.Summary.MediaDirectory)).GetPage().Single();
        if (asset.Length != Fixture.Bytes.Length ||
            !string.Equals(asset.Sha256, Convert.ToHexString(SHA256.HashData(Fixture.Bytes)), StringComparison.OrdinalIgnoreCase))
            throw new Exception("Fixture media bytes mismatch: " + source);
        if (!view.GetPosts().Select(post => post.PostId).OrderBy(id => id, StringComparer.Ordinal).SequenceEqual(new[] { "123", "124" }))
            throw new Exception("Fixture post identities mismatch: " + source);
        FavoriteEntry favorite = new(FavoriteAssetKey.From(id, asset), asset.Length, asset.OriginalFileName);
        favorites.Add(favorite);
        ArchiveHtmlExportResult export = await ArchiveHtmlExporter.ExportAsync(reader, id, Path.Combine(profile, "exports", source));
        await host.ExportHistory.RecordPublishedAsync(export);
        threads.Add(new { Id = id, view.Summary.Url, view.Summary.MediaDirectory, Posts = view.GetPosts(), Asset = asset,
            Favorite = favorite, Export = export.Metadata });
    }
}
object inventory;
using (ProfileLease lease = ProfileLease.Acquire(profile))
{
    FavoriteStore store = new(profile, lease);
    MediaRatingStore ratings = new(profile, lease);
    ThreadFavoriteStore threadFavorites = new(profile, lease);
    PostBookmarkStore bookmarks = new(profile, lease);
    foreach (FavoriteEntry favorite in favorites)
    {
        store.SetFavorite(favorite, true);
        ratings.Set(favorite.Key, favorite.Key.Thread.Source == "4chan" ? 5 : 2);
        threadFavorites.Set(favorite.Key.Thread, true);
        bookmarks.Upsert(new(favorite.Key.Thread, "124"), "SYNTHETIC 1.0.2 retained reply\nNo personal content.",
            bookmarks.Read().Revision, expectedExists: false);
    }
    // Released saved searches and reader groups, written through the pinned 1.0.2 stores only.
    SavedArchiveSearchStore searchStore = new(profile, lease);
    SavedArchiveSearchSnapshot searchState = searchStore.Load();
    if (!searchState.CanWrite || searchState.Searches.Count != 0) throw new Exception("Saved searches not empty/writable.");
    searchState = searchStore.Create(Guid.Parse("1a0251a0-0000-4000-8000-000000000001"), "SYNTHETIC 1.0.2 retained 4chan search",
        new ArchiveSearchSpec("retained", new[] { "4chan" }), searchState.Revision);
    searchState = searchStore.Create(Guid.Parse("1a0251a0-0000-4000-8000-000000000002"), "SYNTHETIC 1.0.2 retained 8kun search",
        new ArchiveSearchSpec("reply", boards: new[] { new ArchiveBoardConstraint("8kun", "g") }), searchState.Revision);
    ReaderGroupStore groupStore = new(profile, lease);
    ReaderGroupsLoadResult groupsLoaded = await groupStore.LoadAsync();
    if (!groupsLoaded.CanWrite || groupsLoaded.Snapshot!.Groups.Count != 0) throw new Exception("Reader groups not empty/writable.");
    ReaderGroupsSnapshot savedGroups = await groupStore.SaveAsync(new ReaderGroupsSnapshot(1, groupsLoaded.Snapshot.Revision, new[]
    {
        new ReaderGroup(Guid.Parse("1a0251a0-0000-4000-8000-000000000011"), "SYNTHETIC 1.0.2 both sources",
            new[] { new ResourceId("4chan", "g", "123"), new ResourceId("8kun", "g", "123") }),
        new ReaderGroup(Guid.Parse("1a0251a0-0000-4000-8000-000000000012"), "SYNTHETIC 1.0.2 single source",
            new[] { new ResourceId("8kun", "g", "123") })
    }), groupsLoaded.Snapshot.Revision);
    SavedArchiveSearchSnapshot searchReadback = new SavedArchiveSearchStore(profile, lease).Load();
    ReaderGroupsLoadResult groupReadback = await new ReaderGroupStore(profile, lease).LoadAsync();
    if (searchReadback.Searches.Count != 2 || !searchReadback.Searches.All(entry => entry.CanApply) ||
        searchReadback.Revision != searchState.Revision ||
        groupReadback.Snapshot!.Groups.Count != 2 || groupReadback.Snapshot.Revision != savedGroups.Revision ||
        groupReadback.Snapshot.Groups.Sum(group => group.Members.Count) != 3)
        throw new Exception("Saved search/reader group persistence mismatch.");
    SessionLoadResult watch = await new SessionStore(profile, lease).LoadAsync();
    if (watch.Recovered || watch.Snapshot.ThreadUrls.Length != 2 ||
        !Fixture.Urls.All(url => watch.Snapshot.ThreadUrls.Contains(url))) throw new Exception("Watch persistence mismatch.");
    Config settings = Config.LoadConfig(profile);
    if (!settings.DownloadsPaused || settings.VideoVolume != 31 || !settings.VideoStartMuted ||
        !settings.GalleryContextOpen || settings.AutomaticUpdateChecks) throw new Exception("Settings persistence mismatch.");
    if (store.List().Count != 2 || ratings.List().Count != 2 || threadFavorites.ReadSnapshot().Threads.Count != 2 ||
        bookmarks.Read().Bookmarks.Count != 2) throw new Exception("Inventory persistence mismatch.");
    foreach (FavoriteEntry expected in favorites)
    {
        if (!store.List().Contains(expected) ||
            !ratings.List().Any(entry => entry.Key == expected.Key && entry.Value == (expected.Key.Thread.Source == "4chan" ? 5 : 2)) ||
            !threadFavorites.ReadSnapshot().Threads.Contains(expected.Key.Thread) ||
            !bookmarks.Read().Bookmarks.Any(entry => entry.Key.Thread == expected.Key.Thread && entry.Key.Post == "124" &&
                entry.Note == "SYNTHETIC 1.0.2 retained reply\nNo personal content."))
            throw new Exception("Exact persisted inventory mismatch.");
    }
    inventory = new { MediaFavorites = store.List(), ThreadFavorites = threadFavorites.ReadSnapshot(),
        Ratings = ratings.List(), Bookmarks = bookmarks.Read(), Watch = watch.Snapshot,
        SavedSearches = searchReadback, ReaderGroups = groupReadback.Snapshot,
        Settings = new { settings.DownloadsPaused, settings.VideoVolume, settings.VideoStartMuted,
            settings.GalleryContextOpen, settings.AutomaticUpdateChecks, settings.DownloadPath,
            settings.LastExportDirectory, settings.ArchiveThreadLayout, settings.ArchiveSplitWidth, settings.WindowWidth } };

}
// Receipt is evidence from old public/internal contracts, never an input state document.
var owned = new SortedDictionary<string, string>(StringComparer.Ordinal);
long totalBytes = 0;
foreach (string path in Fixture.EnumerateFiles(profile))
{
    FileInfo file = new(path);
    if (file.LinkTarget != null || file.Length > 32 * 1024 * 1024 || owned.Count >= 4096 ||
        (totalBytes += file.Length) > 128 * 1024 * 1024) throw new Exception("Fixture file budget/link violation.");
    using FileStream input = File.OpenRead(path);
    owned.Add(Path.GetRelativePath(profile, path).Replace(Path.DirectorySeparatorChar, '/'), Convert.ToHexString(SHA256.HashData(input)));
}
string receiptJson = JsonSerializer.Serialize(new {
    Schema = "meowchan.synthetic-upgrade-102.v1", Synthetic = true, SourceRevision = revision, ProductVersion = productVersion,
    SourceManifestSha256 = Convert.ToHexString(SHA256.HashData(provenance)),
    Inventory = inventory, Threads = threads, Files = owned,
    Requests = transport.Requests.OrderBy(uri => uri, StringComparer.Ordinal).ToArray(), LegacyProfileLock = File.Exists(Path.Combine(profile, ".ochan-profile.lock")),
    Protocol = File.ReadAllText(Path.Combine(profile, ".meowchan-profile.protocol")) }, new JsonSerializerOptions { WriteIndented = true });
string pendingReceipt = Path.Combine(profile, "v102-receipt.json.pending");
using (FileStream output = new(pendingReceipt, FileMode.CreateNew, FileAccess.Write, FileShare.None))
{
    byte[] bytes = Encoding.UTF8.GetBytes(receiptJson + "\n");
    output.Write(bytes);
    output.Flush(flushToDisk: true);
}
File.Move(pendingReceipt, Path.Combine(profile, "v102-receipt.json"));
Console.WriteLine("SYNTHETIC PROFILE CREATED; validate receipt before use: " + profile);

sealed class Fixture : HttpMessageHandler
{
    internal static readonly string[] Urls = { "https://boards.4chan.org/g/thread/123", "https://8kun.top/g/res/123.html" };
    internal static readonly byte[] Bytes = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Wl2nAAAAABJRU5ErkJggg==");
    internal System.Collections.Concurrent.ConcurrentQueue<string> Requests = new();
    private int requestCount;
    internal static IEnumerable<string> EnumerateFiles(string root)
    {
        Stack<string> pending = new();
        pending.Push(root);
        int count = 0;
        while (pending.Count != 0)
            foreach (string path in Directory.EnumerateFileSystemEntries(pending.Pop()))
            {
                FileAttributes attributes = File.GetAttributes(path);
                if (++count > 8192 || (attributes & FileAttributes.ReparsePoint) != 0)
                    throw new Exception("Fixture traversal budget/link violation.");
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(path);
                else yield return path;
            }
    }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (request.Method != HttpMethod.Get || Interlocked.Increment(ref requestCount) > 256)
            throw new InvalidOperationException("Fixture only permits up to 256 GET requests.");
        string uri = request.RequestUri!.AbsoluteUri;
        Requests.Enqueue(uri);
        string? json = uri switch {
            "https://a.4cdn.org/boards.json" => "{\"boards\":[{\"board\":\"g\",\"title\":\"Synthetic\"}]}",
            "https://8kun.top/boards.json" => "[{\"uri\":\"g\",\"title\":\"Synthetic\"}]",
            "https://a.4cdn.org/g/thread/123.json" or "https://8kun.top/g/res/123.json" => JsonSerializer.Serialize(new { posts = new object[] {
                new { no = 123, resto = 0, sub = "SYNTHETIC 1.0.2 upgrade", com = "<p>Retained opening</p>", time = 1700000000,
                    tim = 456, ext = ".png", filename = "fixture", fsize = Bytes.Length, md5 = Convert.ToBase64String(MD5.HashData(Bytes)) },
                new { no = 124, resto = 123, com = "<p>Retained reply &gt;&gt;123</p>", time = 1700000001 } } }),
            _ => null
        };
        byte[] content = json != null ? Encoding.UTF8.GetBytes(json) : uri is "https://i.4cdn.org/g/456.png" or "https://nerv.8kun.top/file_store/456.png"
            ? Bytes : throw new InvalidOperationException("Blocked nonfixture network: " + uri);
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new ByteArrayContent(content) });
    }
}
