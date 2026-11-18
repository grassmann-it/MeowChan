using System.Reflection;
using MeowChan;
using MeowChan.Archive;
using MeowChan.Interfaces;
using MeowChan.Persistence;

const string revision = "eec6c0169f8d52f7481ea0bf7febfd20981e97c3";
string version = typeof(ProfileLease).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
Console.WriteLine("libmeowchan " + version);
if (args.Length != 1) throw new ArgumentException("Expected an empty output directory.");
string output = Path.GetFullPath(args[0]);
if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any()) throw new InvalidOperationException("The output directory must be empty.");

// Reader groups: two generations (the backup holds the first), with an empty group, members of two sources and escaped names.
string groupsProfile = Path.Combine(output, "p-groups"); Directory.CreateDirectory(groupsProfile);
using (ProfileLease lease = ProfileLease.Acquire(groupsProfile))
{
    ReaderGroupStore store = new(groupsProfile, lease);
    ReaderGroupsLoadResult loaded = await store.LoadAsync();
    ReaderGroupsSnapshot first = await store.SaveAsync(new ReaderGroupsSnapshot(1, loaded.Snapshot!.Revision, new[]
    {
        new ReaderGroup(Guid.Parse("1a0251a0-0000-4000-8000-000000000021"), "first generation", new[] { new ResourceId("4chan", "g", "123") })
    }), loaded.Snapshot.Revision);
    await store.SaveAsync(new ReaderGroupsSnapshot(1, first.Revision, new[]
    {
        new ReaderGroup(Guid.Parse("1a0251a0-0000-4000-8000-000000000011"), "both sources", new[] { new ResourceId("4chan", "g", "123"), new ResourceId("8kun", "g", "123"), new ResourceId("4chan", "a", "9") }),
        new ReaderGroup(Guid.Parse("1a0251a0-0000-4000-8000-000000000012"), "empty group", Array.Empty<ResourceId>()),
        new ReaderGroup(Guid.Parse("1a0251a0-0000-4000-8000-000000000013"), "café \"quoted\" \\ 😀", new[] { new ResourceId("8kun", "g", "123") })
    }), first.Revision);
}
// Reader groups written empty after a group existed (revision 2, "groups": []).
string emptiedProfile = Path.Combine(output, "p-emptied"); Directory.CreateDirectory(emptiedProfile);
using (ProfileLease lease = ProfileLease.Acquire(emptiedProfile))
{
    ReaderGroupStore store = new(emptiedProfile, lease);
    ReaderGroupsLoadResult loaded = await store.LoadAsync();
    ReaderGroupsSnapshot one = await store.SaveAsync(new ReaderGroupsSnapshot(1, loaded.Snapshot!.Revision, new[]
    {
        new ReaderGroup(Guid.Parse("1a0251a0-0000-4000-8000-000000000031"), "to be removed", new[] { new ResourceId("4chan", "g", "1") })
    }), loaded.Snapshot.Revision);
    await store.SaveAsync(new ReaderGroupsSnapshot(1, one.Revision, Array.Empty<ReaderGroup>()), one.Revision);
}
// Saved searches: three definitions, then a rename so that the backup holds the previous generation.
string searchesProfile = Path.Combine(output, "p-searches"); Directory.CreateDirectory(searchesProfile);
using (ProfileLease lease = ProfileLease.Acquire(searchesProfile))
{
    SavedArchiveSearchStore store = new(searchesProfile, lease);
    SavedArchiveSearchSnapshot state = store.Load();
    if (!state.CanWrite || state.Searches.Count != 0) throw new Exception("Saved searches not empty/writable.");
    state = store.Create(Guid.Parse("1a0251a0-0000-4000-8000-000000000001"), "plain text and source", new ArchiveSearchSpec("retained", new[] { "4chan" }), state.Revision);
    state = store.Create(Guid.Parse("1a0251a0-0000-4000-8000-000000000002"), "board in every source",
        new ArchiveSearchSpec("reply", boards: new[] { new ArchiveBoardConstraint("8kun", "g"), new ArchiveBoardConstraint(null, "a") },
            sourceStates: new[] { ThreadSourceState.Archived, ThreadSourceState.NotFound }), state.Revision);
    state = store.Create(Guid.Parse("1a0251a0-0000-4000-8000-000000000003"), "everything set",
        new ArchiveSearchSpec("café \"quoted\" \\ 😀", new[] { "4chan", "8kun" }, captureEnabled: true, hasPosts: false,
            sort: ArchiveThreadSort.Title, workFilter: ArchiveWorkFilter.Watching,
            dateRange: new ArchiveDateRange(new DateOnly(2026, 1, 2), new DateOnly(2026, 3, 4), "UTC")), state.Revision);
    store.Rename(Guid.Parse("1a0251a0-0000-4000-8000-000000000001"), "plain text and source (renamed)", state.Revision);
}
Directory.CreateDirectory(Path.Combine(output, "files"));
foreach ((string from, string to) in new[]
{
    ("p-groups/reader-groups.json", "reader-groups.json"), ("p-groups/reader-groups.json.bak", "reader-groups.json.bak"),
    ("p-emptied/reader-groups.json", "reader-groups-emptied.json"),
    ("p-searches/archive-searches.json", "archive-searches.json"), ("p-searches/archive-searches.json.bak", "archive-searches.json.bak")
})
    File.Copy(Path.Combine(output, from), Path.Combine(output, "files", to));
Console.WriteLine("Wrote " + Path.Combine(output, "files") + " from " + revision);
