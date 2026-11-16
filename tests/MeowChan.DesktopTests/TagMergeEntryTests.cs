using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;
using MeowChan.Persistence;
using MeowChan.Storage;
using MeowChan.Tags;

namespace MeowChan.DesktopTests;

// Tag merge in the tag manager session, aliases and the multi-tag entry of the tag fields (parser, completion, confirmation).
[Collection("Localization")]
public sealed class TagMergeEntryTests : IDisposable
{
    private static readonly DateTimeOffset Day = new(2026, 11, 15, 0, 0, 0, TimeSpan.Zero);
    public TagMergeEntryTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage("en"); }

    private static ResourceId Thread(string id) => new("source", "board", id);

    private static MediaLibraryEntry Entry(string thread, char hash) =>
        new(new ResourceId("s", "b", thread), "T" + thread, 1, Day, Day, "1", 1, null, 0,
            "att-v1:" + new string(hash, 64), "/dir/" + thread, "asset-" + thread, new string(hash, 64), 1, "m1", "original",
            "f" + thread + ".png", SavedMediaKind.Image, MediaLibraryPresence.Present, false, false);

    private static PrivateTagLoadSnapshot Load(PrivateTagStore store) => new(PrivateTagLoadState.Ready, store.ReadSnapshot(), null);

    private static MediaTagSession Single(PrivateTagStore store, MediaLibraryEntry entry) =>
        new(new MediaTagSubject.Bound(MediaLibraryFilterEngine.TagTarget(entry)!), () => Load(store),
            (name, subject, token) => Task.FromResult(store.DefineAndSetMediaSubjects(name, [subject], null, token)),
            (id, subject, assigned, token) => Task.FromResult(store.SetMediaSubjects(id, [subject], assigned, null, token)));

    private static TagManagerSession Manager(PrivateTagStore store) => new(
        () => Load(store),
        (id, name, revision, token) => Task.FromResult(store.Rename(id, name, revision, token)),
        (id, revision, token) => Task.FromResult(store.Delete(id, revision, token)),
        merge: new TagMergeCommands(
            (target, merged, revision, token) => Task.FromResult(store.Merge(target, merged, revision, token)),
            (undo, token) => Task.FromResult(store.UndoMerge(undo, token)),
            (tag, name, revision, token) => Task.FromResult(store.AddAlias(tag, name, revision, token)),
            (name, revision, token) => Task.FromResult(store.RemoveAlias(name, revision, token))));

    private static string[] Names(TagEntry entry) => entry.Items.Select(item => item.Name.Display).ToArray();

    // ---- parser ----------------------------------------------------------------------------------------------------

    [Fact]
    public void SpacesSeparateQuotesKeepOneTagAndCommasSeparateWithSpaces()
    {
        Assert.Equal(new[] { "piss", "pee", "cute", "anal" }, Names(TagEntryParser.Parse("piss pee  cute anal", null)));
        Assert.Equal(new[] { "big tits", "anal" }, Names(TagEntryParser.Parse("\"big tits\" anal", null)));
        Assert.Equal(new[] { "say \"hi\"", "x" }, Names(TagEntryParser.Parse("\"say \\\"hi\\\"\" x", null)));
        Assert.Equal(new[] { "big tits", "anal" }, Names(TagEntryParser.Parse("big tits, anal", null)));
        Assert.Equal(new[] { "big tits", "anal" }, Names(TagEntryParser.Parse("\"big tits\", anal,", null)));
        Assert.Equal(new[] { "cute" }, Names(TagEntryParser.Parse("cute cute CUTE", null)));
        Assert.Empty(TagEntryParser.Parse("   ", null).Items);
    }

    [Fact]
    public void ExistingNamesWithSpacesOrCommasStayOneTagAndAliasesMapWithDuplicatesOnce()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        PrivateTagStore store = new(temp.Path, lease);
        TagId piss = store.Define("piss").Tag, pee = store.Define("pee").Tag;
        TagId big = store.Define("big tits").Tag;
        store.Define("a, b");
        store.Merge(piss, [pee]);
        PrivateTagStoreSnapshot snapshot = store.ReadSnapshot();

        TagEntry entry = TagEntryParser.Parse("piss pee big tits anal /PEE/", snapshot);

        Assert.Equal(new[] { "piss", "big tits", "anal" }, Names(entry));
        Assert.Equal(piss, entry.Items[0].Id);
        Assert.False(entry.Items[0].Mapped);
        Assert.Equal(big, entry.Items[1].Id);
        Assert.Null(entry.Items[2].Id);
        Assert.Equal(new[] { "a, b" }, Names(TagEntryParser.Parse("a, b", snapshot)));
        TagEntryItem mapped = Assert.Single(TagEntryParser.Parse("pee", snapshot).Items);
        Assert.True(mapped.Mapped);
        Assert.Equal("piss", mapped.Name.Display);
        Assert.Equal("Added: piss (from pee), cute (new), anal (new)", Texts.T("tags.entry.added",
            TagEntryParser.Describe(TagEntryParser.Parse("pee cute anal", snapshot).Items)));
    }

    [Fact]
    public void CompletionReplacesOnlyTheLastWordAndQuotesWhenNeeded()
    {
        Assert.Equal(("piss ", "pe"), TagEntryParser.SplitLast("piss pe"));
        Assert.Equal(("", "pe"), TagEntryParser.SplitLast("pe"));
        Assert.Equal(("a, ", "b"), TagEntryParser.SplitLast("a, b"));
        Assert.Equal(("x ", "big t"), TagEntryParser.SplitLast("x \"big t"));
        Assert.Equal("x \"big tits\"", TagEntryParser.Complete("x ", "big tits"));
        Assert.Equal("x cute", TagEntryParser.Complete("x ", "cute"));
    }

    [Fact]
    public void SuggestionsCompleteTheLastWordAndOfferAliasTargets()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        PrivateTagStore store = new(temp.Path, lease);
        TagId piss = store.Define("piss").Tag, pee = store.Define("pee").Tag;
        store.Define("cute");
        store.Merge(piss, [pee]);
        PrivateTagStoreSnapshot snapshot = store.ReadSnapshot();

        IdentitySuggestion[] first = TagSuggestionIndex.CombineEntry(snapshot, null, "pe", new HashSet<TagId>());
        IdentitySuggestion alias = Assert.Single(first, item => item.Source == TagSuggestionIndex.AliasGroup);
        Assert.Equal("piss", alias.Value);
        Assert.Equal("pee → piss", alias.Label);
        // A typed alias creates nothing new.
        Assert.DoesNotContain(TagSuggestionIndex.CombineEntry(snapshot, null, "pee", new HashSet<TagId>()),
            item => item.Source == TagSuggestionIndex.CreateGroup);

        IdentitySuggestion[] later = TagSuggestionIndex.CombineEntry(snapshot, null, "anal cu", new HashSet<TagId>());
        Assert.Contains(later, item => item.Value == "anal cute");
        Assert.DoesNotContain(later, item => item.Source == TagSuggestionIndex.CreateGroup);
        Assert.Contains(TagSuggestionIndex.CombineEntry(snapshot, null, "anal pe", new HashSet<TagId>()),
            item => item.Value == "anal piss" && item.Source == TagSuggestionIndex.AliasGroup);
    }

    [Fact]
    public async Task TheQuickTagFieldAddsEveryNameAndSaysWhichNamesWereMapped()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        PrivateTagStore store = new(temp.Path, lease);
        TagId piss = store.Define("piss").Tag, pee = store.Define("pee").Tag;
        store.Merge(piss, [pee]);
        MediaLibraryEntry entry = Entry("a", 'a');
        using QuickTagSession session = new(QuickTagTarget.For(Single(store, entry)));

        await session.AddAsync("pee cute \"big tits\" piss");

        PrivateMediaTagTarget target = MediaLibraryFilterEngine.TagTarget(entry)!;
        Assert.Equal(new[] { "big tits", "cute", "piss" },
            store.ReadSnapshot().TagsOf(target).Select(tag => tag.Name.Display).Order(StringComparer.Ordinal));
        Assert.Equal(QuickTagStatusKind.AddedSeveral, session.Status.Kind);
        Assert.Equal("Added: piss (from pee), cute (new), big tits (new)", session.Status.Text);
        // One entry is one undo step (MultiTagEntryTests covers the undo itself).
        Assert.Single(session.UndoSteps);
        Assert.Equal(2, store.ReadSnapshot().Tags.Count(tag => tag.Name.Display is "cute" or "big tits"));
    }

    // ---- tag manager: merge, preview, undo, aliases, suggestions ---------------------------------------------------

    [Fact]
    public async Task TheManagerPreviewsMergesUndoesAndEditsAliases()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        PrivateTagStore store = new(temp.Path, lease);
        TagId abdl = store.Define("abdl").Tag, slash = store.Define("/abdl/").Tag;
        store.SetThread(abdl, Thread("1"), true);
        store.SetThread(slash, Thread("1"), true);
        store.SetThread(slash, Thread("2"), true);
        store.SetThread(abdl, Thread("3"), true);
        store.SetThread(abdl, Thread("4"), true);
        store.SetThread(abdl, Thread("5"), true);
        store.SetMedia(slash, MediaLibraryFilterEngine.TagTarget(Entry("m", 'b'))!, true);
        TagManagerSession session = Manager(store);

        TagDuplicateGroup group = Assert.Single(session.Duplicates());
        Assert.Equal(new[] { abdl, slash }, group.Tags.Select(tag => tag.Id));
        Assert.Equal(abdl, session.DefaultMergeTarget([slash, abdl]));
        TagMergePreview preview = session.Preview([abdl, slash], abdl)!;
        Assert.Equal(1, preview.Media);
        Assert.Equal(1, preview.Threads);
        Assert.Equal("Media (1) and threads (1) will get “abdl”; “/abdl/” will be removed.",
            Texts.T("tags.merge.preview", preview.Media, preview.Threads, preview.Target.Name, preview.MergedNames));

        await session.MergeAsync([abdl, slash], abdl);

        Assert.Equal("tags.merge.done", session.MessageKey);
        Assert.Equal("abdl", session.LastMergeTarget);
        Assert.Equal(new[] { "abdl" }, session.Rows.Select(row => row.Name));
        Assert.Equal(5, session.Rows[0].Threads);
        Assert.Equal("/abdl/", Assert.Single(session.AliasesOf(abdl)).Name.Display);
        Assert.Empty(session.Duplicates());
        Assert.True(session.CanUndoMerge);

        await session.UndoMergeAsync();
        Assert.Equal("tags.merge.undone", session.MessageKey);
        Assert.Equal(2, session.Rows.Count);
        Assert.Empty(session.AliasesOf(abdl));
        Assert.False(session.CanUndoMerge);

        await session.AddAliasAsync(abdl, "ab dl");
        Assert.Equal("tags.alias.added", session.MessageKey);
        await session.AddAliasAsync(slash, "AB DL");
        Assert.Equal("tags.alias.error.collision", session.MessageKey);
        await session.RemoveAliasAsync("ab dl");
        Assert.Equal("tags.alias.removed", session.MessageKey);
        Assert.Empty(session.AliasesOf(abdl));
    }

    [Fact]
    public async Task AnUndoAfterAnotherChangeIsRefusedAndForgotten()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        PrivateTagStore store = new(temp.Path, lease);
        TagId a = store.Define("a").Tag, b = store.Define("b").Tag;
        TagManagerSession session = Manager(store);
        await session.MergeAsync([a, b], a);
        store.Define("c"); // a write elsewhere (another window, a background carry-over)
        session.Reload();

        Assert.False(session.CanUndoMerge);
        await session.UndoMergeAsync();

        Assert.Equal("tags.merge.error.undoStale", session.MessageKey);
        Assert.Null(session.LastMerge);
        Assert.Equal(new[] { "a", "c" }, store.ReadSnapshot().Tags.Select(tag => tag.Name.Display));
    }
}
