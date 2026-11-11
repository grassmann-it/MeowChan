using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Gallery;
using MeowChan.Persistence;
using MeowChan.Storage;
using MeowChan.Tags;

namespace MeowChan.DesktopTests;

public sealed class MediaSelectionTagSessionTests
{
    private static readonly DateTimeOffset Day = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

    private static MediaLibraryEntry Entry(string thread, char hash, int captured = 0, bool missing = false) =>
        new(new ResourceId("s", "b", thread), "T" + thread, 1, Day, Day.AddHours(captured), "1", 1, null, 0,
            "att-v1:" + new string(hash, 64), missing ? null : "/dir/" + thread, missing ? null : "asset-" + thread,
            missing ? null : new string(hash, 64), missing ? null : 1, "m1", "original", "f" + thread + ".png", SavedMediaKind.Image,
            missing ? MediaLibraryPresence.Missing : MediaLibraryPresence.Present, false, false);

    private static MediaSelectionTagSession Session(PrivateTagStore store, IReadOnlyList<MediaTagCandidate> candidates,
        Func<PrivateTagLoadSnapshot>? load = null) => new(candidates,
        load ?? (() => new PrivateTagLoadSnapshot(PrivateTagLoadState.Ready, store.ReadSnapshot(), null)),
        (name, token) => Task.FromResult(store.Define(name, null, token)),
        (tag, targets, assigned, revision, token) => Task.FromResult(store.SetMediaBatch(tag, targets.ToArray(), assigned, revision, token)));

    // A tile of equal bytes in three threads (e, d, c by capture time) plus one unsaved tile.
    private static (IReadOnlyList<MediaTagCandidate> Candidates, MediaLibraryEntry[] Group) Selection()
    {
        MediaLibraryEntry a = Entry("a", 'e', 3), b = Entry("b", 'e', 2), c = Entry("c", 'e', 1), gone = Entry("d", 'd', missing: true);
        MediaLibraryEntry[] all = [a, b, c, gone];
        IReadOnlyList<MediaLibraryTile> tiles = MediaLibraryGrouping.Build(all, all, MediaLibrarySort.ByThread, includeMissing: true);
        return (MediaLibraryTagging.Candidates(tiles), [a, b, c]);
    }

    [Fact]
    public async Task EveryTaggableOccurrenceIsChosenByDefaultAndADeselectedOneIsLeftAlone()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        PrivateTagStore store = new(temp.Path, lease);
        (IReadOnlyList<MediaTagCandidate> candidates, MediaLibraryEntry[] group) = Selection();
        MediaSelectionTagSession session = Session(store, candidates);

        Assert.Equal(4, candidates.Count);
        Assert.Equal(3, session.Chosen.Count); // the unsaved tile cannot be tagged and is not chosen
        session.Choose(candidates.Count - 1, true);
        Assert.Equal(3, session.Chosen.Count); // ... and cannot be chosen either
        int middle = candidates.ToList().FindIndex(item => item.Entry == group[1]);
        session.Choose(middle, false);

        await session.AddAsync(" cute ");

        PrivateTagStoreSnapshot stored = store.ReadSnapshot();
        Assert.Null(session.MessageKey);
        Assert.True(session.Changed);
        Assert.Equal("cute", Assert.Single(stored.TagsOf(MediaLibraryFilterEngine.TagTarget(group[0])!)).Name.Display);
        Assert.Empty(stored.TagsOf(MediaLibraryFilterEngine.TagTarget(group[1])!));
        Assert.Equal("cute", Assert.Single(stored.TagsOf(MediaLibraryFilterEngine.TagTarget(group[2])!)).Name.Display);
        Assert.Equal(2, stored.MediaAssignments.Count);
        Assert.Equal(2, Assert.Single(session.Removable()).Count);
    }

    [Fact]
    public async Task RemovingTouchesOnlyChosenOccurrencesThatHaveTheTag()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        PrivateTagStore store = new(temp.Path, lease);
        (IReadOnlyList<MediaTagCandidate> candidates, MediaLibraryEntry[] group) = Selection();
        TagId red = store.Define("red").Tag;
        store.SetMedia(red, MediaLibraryFilterEngine.TagTarget(group[0])!, true);
        store.SetMedia(red, MediaLibraryFilterEngine.TagTarget(group[1])!, true);
        store.SetThread(red, group[2].Thread, true);
        MediaSelectionTagSession session = Session(store, candidates);
        session.Choose(candidates.ToList().FindIndex(item => item.Entry == group[1]), false);

        Assert.Equal(1, Assert.Single(session.Removable()).Count); // only the first occurrence is chosen and carries it
        await session.RemoveAsync(red);

        PrivateTagStoreSnapshot stored = store.ReadSnapshot();
        Assert.Empty(stored.TagsOf(MediaLibraryFilterEngine.TagTarget(group[0])!));
        Assert.Equal(red, Assert.Single(stored.TagsOf(MediaLibraryFilterEngine.TagTarget(group[1])!)).Id); // deselected, so it keeps the tag
        Assert.Equal(red, Assert.Single(stored.TagsOf(group[2].Thread)).Id); // thread tags are never touched
    }

    [Fact]
    public async Task AStaleViewAndAVanishedTagAreExplainedAndWriteNothing()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        PrivateTagStore store = new(temp.Path, lease);
        (IReadOnlyList<MediaTagCandidate> candidates, MediaLibraryEntry[] group) = Selection();
        TagId red = store.Define("red").Tag, doomed = store.Define("doomed").Tag;
        MediaSelectionTagSession session = Session(store, candidates);

        store.Define("other"); // tags changed after the dialog looked
        await session.AddExistingAsync(red);
        Assert.Equal("tags.error.stale", session.MessageKey);
        Assert.Empty(store.ReadSnapshot().MediaAssignments);
        Assert.False(session.Changed);

        await session.AddExistingAsync(red); // the dialog reloaded; the retry works
        Assert.Null(session.MessageKey);
        Assert.Equal(3, store.ReadSnapshot().MediaAssignments.Count);

        store.Delete(doomed); // the deletion is also a change after the dialog looked ...
        await session.AddExistingAsync(doomed);
        Assert.Equal("tags.error.stale", session.MessageKey);
        await session.AddExistingAsync(doomed); // ... and a click on its leftover button on the fresh view finds no such tag
        Assert.Equal("tags.manage.error.gone", session.MessageKey);
        Assert.Equal(3, store.ReadSnapshot().MediaAssignments.Count);
    }

    [Fact]
    public async Task AnUnavailableTagIndexAndAnEmptyChoiceChangeNothing()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        PrivateTagStore store = new(temp.Path, lease);
        (IReadOnlyList<MediaTagCandidate> candidates, _) = Selection();
        int writes = 0;
        MediaSelectionTagSession unavailable = new(candidates, () => new PrivateTagLoadSnapshot(PrivateTagLoadState.Unavailable, null, "broken"),
            (name, token) => { writes++; return Task.FromResult(store.Define(name, null, token)); },
            (tag, targets, assigned, revision, token) => { writes++; return Task.FromResult(store.SetMediaBatch(tag, targets.ToArray(), assigned, revision, token)); });

        await unavailable.AddAsync("red");
        Assert.Null(unavailable.Snapshot);
        Assert.Equal(0, writes);

        MediaSelectionTagSession session = Session(store, candidates);
        session.ChooseAll(false);
        await session.AddAsync("red");
        Assert.Equal("media.selection.tag.noneChosen", session.MessageKey);
        Assert.Empty(store.ReadSnapshot().Tags);
    }
}
