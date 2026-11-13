using System.Reflection;
using System.Text.Json;
using MeowChan;
using MeowChan.Archive;
using MeowChan.Favorites;
using MeowChan.Persistence;
using MeowChan.Ratings;

// Downgrade probe: what the released 1.0.2 code does when it is pointed at a profile that
// 1.1.0 has already upgraded. Overlay for the pinned 1.0.2 tree, built like the fixture
// generator; it reads and reports, it never repairs. Run it on a COPY of the profile: the
// released stores open their files read/write and this probe cannot prevent that.
const string revision = "eec6c0169f8d52f7481ea0bf7febfd20981e97c3";
string productVersion = typeof(Config).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
if (productVersion != "1.0.2+" + revision) throw new Exception("Requires pinned 1.0.2 informational version: " + productVersion);
if (args.Length != 2) throw new ArgumentException("Expected an upgraded profile COPY and a new result file.");
string profile = Path.GetFullPath(args[0]);
string result = Path.GetFullPath(args[1]);
if (!Directory.Exists(profile) || File.Exists(result)) throw new InvalidOperationException("Requires an existing profile copy and an unused result path.");
using Timer watchdog = new(_ => { Console.Error.WriteLine("DOWNGRADE PROBE TIMEOUT (120s)."); Environment.Exit(124); },
    null, TimeSpan.FromSeconds(120), Timeout.InfiniteTimeSpan);

SortedDictionary<string, object> outcomes = new(StringComparer.Ordinal);
SortedDictionary<string, string> summary = new(StringComparer.Ordinal);
Record("settings", () =>
{
    Config settings = Config.LoadConfig(profile);
    return new { settings.VideoVolume, settings.MaxParallelDownloads, settings.DownloadsPaused, settings.ArchiveThreadLayout };
});
using (ProfileLease lease = ProfileLease.Acquire(profile))
{
    Record("archive", () =>
    {
        ArchiveCatalog catalog = ArchiveCatalog.OpenAsync(profile, lease).GetAwaiter().GetResult();
        try { return new { Opened = true, catalog.DatabasePath }; }
        finally { catalog.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    });
    Record("favorites", () => new { Count = new FavoriteStore(profile, lease).List().Count });
    Record("ratings", () => new { Count = new MediaRatingStore(profile, lease).List().Count });
    Record("thread-favorites", () => new { Count = new ThreadFavoriteStore(profile, lease).ReadSnapshot().Threads.Count });
    Record("bookmarks", () => new { Count = new PostBookmarkStore(profile, lease).Read().Bookmarks.Count });
    Record("saved-searches", () =>
    {
        SavedArchiveSearchSnapshot snapshot = new SavedArchiveSearchStore(profile, lease).Load();
        return new { State = snapshot.State.ToString(), Count = snapshot.Searches.Count, snapshot.Error };
    });
    Record("reader-groups", () =>
    {
        ReaderGroupsLoadResult loaded = new ReaderGroupStore(profile, lease).LoadAsync().GetAwaiter().GetResult();
        return new { State = loaded.State.ToString(), Count = loaded.Snapshot?.Groups.Count, loaded.Error };
    });
    Record("watch", () =>
    {
        SessionLoadResult watch = new SessionStore(profile, lease).LoadAsync().GetAwaiter().GetResult();
        return new { Count = watch.Snapshot.ThreadUrls.Length, watch.Recovered, watch.Warning };
    });
}
File.WriteAllText(result, JsonSerializer.Serialize(new
{
    Schema = "meowchan.released-102-downgrade-probe.v1", ProductVersion = productVersion, Profile = profile,
    Outcomes = outcomes
}, new JsonSerializerOptions { WriteIndented = true }) + "\n");
Console.WriteLine("DOWNGRADE PROBE DONE: " + string.Join(", ", summary.Select(entry => entry.Key + "=" + entry.Value)));

void Record(string name, Func<object> probe)
{
    try
    {
        outcomes[name] = new { Failure = (string?)null, Value = probe() };
        summary[name] = "ok";
    }
    catch (Exception error)
    {
        outcomes[name] = new { Failure = error.GetType().Name, error.Message };
        summary[name] = "refused:" + error.GetType().Name;
    }
}
