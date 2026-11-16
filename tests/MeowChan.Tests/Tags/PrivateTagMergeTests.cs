using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MeowChan.Archive;
using MeowChan.Persistence;
using MeowChan.Storage;
using MeowChan.Tags;
using MeowChan.Transfer;
using Xunit.Abstractions;

namespace MeowChan.Tests.Tags;

// Tag merge, aliases (private-tag-store v3) and the duplicate normaliser. Temp profiles only.
public sealed class PrivateTagMergeTests
{
    private const string FileName = "private-tags.json";
    private const string RevisionKey = "att-v2:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static readonly ResourceId Thread = new("source", "board", "42");

    private static string Hex(string seed) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(seed))).ToLowerInvariant();

    private static PrivateMediaTagTarget Bound(string post = "100", ResourceId? thread = null)
    {
        Assert.True(PrivateMediaTagTarget.TryCreate(new AttachmentRevisionReference(thread ?? Thread, post, 0, RevisionKey), "media-1", "original",
            TransferMediaRole.Original, Hex("bound" + post), out PrivateMediaTagTarget? target, out _));
        return target!;
    }

    private static LocalMediaObservation Observe(string file = "a.png")
    {
        Assert.True(LocalMediaObservation.TryCreate(Thread, "/media/42", file, Hex("/media/42|" + file), 3, Hex("bytes" + file),
            out LocalMediaObservation? observation));
        return observation!;
    }

    private static PrivateTagStoreError Rejected(Action action) => Assert.Throws<PrivateTagStoreException>(action).Error;

    private static PrivateTagName Name(string text)
    {
        Assert.True(PrivateTagName.TryCreate(text, out PrivateTagName? name, out _));
        return name!;
    }

    [Fact]
    public void MergeMovesEveryAssignmentKindDeduplicatesAndKeepsTheMergedNamesAsAliases()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        PrivateTagStore store = new(temp.Path, lease);
        TagId abdl = store.Define("abdl").Tag, slash = store.Define("/abdl/").Tag, cute = store.Define("cute").Tag;
        ResourceId other = Thread with { Thread = "43" };
        store.SetThread(abdl, Thread, true);
        store.SetThread(slash, Thread, true);   // both on one thread: one assignment remains
        store.SetThread(slash, other, true);
        store.SetMedia(abdl, Bound("100"), true);
        store.SetMedia(slash, Bound("100"), true); // both on one file
        store.SetMedia(slash, Bound("101"), true);
        store.SetMedia(cute, Bound("101"), true);
        store.SetMediaSubjects(slash, [new MediaTagSubject.Local(Observe("a.png"), null)], true);
        store.SetMediaSubjects(abdl, [new MediaTagSubject.Local(Observe("a.png"), null)], true);
        store.SetMediaSubjects(slash, [new MediaTagSubject.Local(Observe("b.png"), null)], true);
        long revision = store.ReadSnapshot().Revision;

        PrivateTagMergeCommit merged = store.Merge(abdl, [slash], revision);

        PrivateTagStoreSnapshot after = merged.Commit.Snapshot;
        Assert.True(merged.Commit.Changed);
        Assert.Equal(revision + 1, after.Revision);
        Assert.Equal(new[] { "abdl", "cute" }, after.Tags.Select(tag => tag.Name.Display));
        Assert.Equal(2, after.ThreadAssignments.Count);
        Assert.All(after.ThreadAssignments, item => Assert.Equal(abdl, (TagId)item.TagId));
        Assert.Equal(2, after.MediaAssignments.Count(item => (TagId)item.TagId == abdl));
        Assert.Single(after.MediaAssignments, item => (TagId)item.TagId == cute);
        Assert.Equal(2, after.LocalAssignments.Count);
        Assert.All(after.LocalAssignments, item => Assert.Equal(abdl, item.TagId));
        Assert.DoesNotContain(after.ThreadAssignments, item => (TagId)item.TagId == slash);
        PrivateTagAlias alias = Assert.Single(after.Aliases);
        Assert.Equal("/abdl/", alias.Name.Display);
        Assert.Equal(abdl, alias.Tag);
        Assert.Equal(slash, alias.Former);
        Assert.Equal(3, after.FormatVersion);
        // Durable: a new instance reads exactly that.
        PrivateTagStoreSnapshot reread = new PrivateTagStore(temp.Path, lease).ReadSnapshot();
        Assert.Equal(after.Fingerprint, reread.Fingerprint);
        Assert.Single(reread.Aliases);
        Assert.Equal(3, PrivateTagStore.ValidateDocumentBytes(File.ReadAllBytes(Path.Combine(temp.Path, FileName))));
    }

    [Fact]
    public void ThreeTagsMergeInOneWriteAndChainedMergesKeepEveryOldIdResolvable()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        PrivateTagStore store = new(temp.Path, lease);
        TagId piss = store.Define("piss").Tag, pee = store.Define("pee").Tag, peeing = store.Define("#peeing").Tag, wet = store.Define("wet").Tag;
        store.SetThread(pee, Thread, true);
        store.Merge(piss, [pee, peeing]);
        PrivateTagStoreSnapshot snapshot = store.Merge(wet, [piss]).Commit.Snapshot;

        Assert.Equal(new[] { "wet" }, snapshot.Tags.Select(tag => tag.Name.Display));
        Assert.Equal(3, snapshot.Aliases.Count);
        Assert.All(snapshot.Aliases, item => Assert.Equal(wet, item.Tag));
        Assert.Equal(wet, snapshot.ResolveId(pee));
        Assert.Equal(wet, snapshot.ResolveId(peeing));
        Assert.Equal(wet, snapshot.ResolveId(piss));
        Assert.Null(snapshot.ResolveId(new TagId(Guid.NewGuid())));
        Assert.Equal(wet, (TagId)Assert.Single(snapshot.ThreadAssignments).TagId);
    }

    [Fact]
    public void InvalidMergesAreRejectedAndWriteNothing()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        PrivateTagStore store = new(temp.Path, lease);
        TagId a = store.Define("a").Tag, b = store.Define("b").Tag;
        byte[] before = File.ReadAllBytes(Path.Combine(temp.Path, FileName));

        Assert.Equal(PrivateTagStoreError.InvalidMerge, Rejected(() => store.Merge(a, [])));
        Assert.Equal(PrivateTagStoreError.InvalidMerge, Rejected(() => store.Merge(a, [a, b])));
        Assert.Equal(PrivateTagStoreError.UnknownTag, Rejected(() => store.Merge(a, [new TagId(Guid.NewGuid())])));
        Assert.Equal(PrivateTagStoreError.StaleRevision, Rejected(() => store.Merge(a, [b], expectedRevision: 0)));
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(temp.Path, FileName)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void AFailureDuringTheMergeLeavesTheOldDocumentExactly(int failAt)
    {
        PrivateTagWritePhase phase = failAt == 0 ? PrivateTagWritePhase.BeforeBackupCheck : PrivateTagWritePhase.BeforeReplace;
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        PrivateTagStore plain = new(temp.Path, lease);
        TagId a = plain.Define("a").Tag, b = plain.Define("b").Tag;
        plain.SetThread(b, Thread, true);
        plain.SetMedia(b, Bound(), true);
        string path = Path.Combine(temp.Path, FileName);
        byte[] before = File.ReadAllBytes(path);
        PrivateTagStore failing = new(temp.Path, lease)
        { WritePhaseForTests = current => { if (current == phase) throw new IOException("injected"); } };

        Assert.Throws<IOException>(() => failing.Merge(a, [b]));

        Assert.Equal(before, File.ReadAllBytes(path));
        PrivateTagStoreSnapshot snapshot = plain.ReadSnapshot();
        Assert.Equal(2, snapshot.Tags.Count);
        Assert.Empty(snapshot.Aliases);
        Assert.Equal(b, (TagId)Assert.Single(snapshot.ThreadAssignments).TagId);
    }

    [Fact]
    public void UndoRestoresTheMergedTagsTheirAssignmentsAndRemovesTheAliasesItMade()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        PrivateTagStore store = new(temp.Path, lease);
        TagId abdl = store.Define("abdl").Tag, slash = store.Define("/abdl/").Tag;
        store.AddAlias(abdl, "ab dl");
        store.SetThread(abdl, Thread, true);
        store.SetThread(slash, Thread, true);
        store.SetMedia(slash, Bound("101"), true);
        PrivateTagStoreSnapshot before = store.ReadSnapshot();

        PrivateTagMergeCommit merged = store.Merge(abdl, [slash]);
        PrivateTagStoreSnapshot undone = store.UndoMerge(merged.Undo).Snapshot;

        Assert.Equal(merged.Commit.Snapshot.Revision + 1, undone.Revision);
        Assert.Equal(before.Tags.Select(tag => (tag.Id, tag.Name.Display)), undone.Tags.Select(tag => (tag.Id, tag.Name.Display)));
        Assert.Equal(before.ThreadAssignments, undone.ThreadAssignments);
        Assert.Equal(before.MediaAssignments, undone.MediaAssignments);
        Assert.Equal("ab dl", Assert.Single(undone.Aliases).Name.Display);
        // The version never goes back, and a second undo is stale.
        Assert.Equal(3, undone.FormatVersion);
        Assert.Equal(PrivateTagStoreError.StaleRevision, Rejected(() => store.UndoMerge(merged.Undo)));
    }

    [Fact]
    public void UndoIsRefusedAfterAnyOtherWrite()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        PrivateTagStore store = new(temp.Path, lease);
        TagId a = store.Define("a").Tag, b = store.Define("b").Tag;
        PrivateTagMergeCommit merged = store.Merge(a, [b]);
        store.Define("c");
        byte[] current = File.ReadAllBytes(Path.Combine(temp.Path, FileName));

        Assert.Equal(PrivateTagStoreError.StaleRevision, Rejected(() => store.UndoMerge(merged.Undo)));
        Assert.Equal(current, File.ReadAllBytes(Path.Combine(temp.Path, FileName)));
    }

    [Fact]
    public void AliasesResolveTypedNamesThroughTheFoldAndDefineGetsTheTarget()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        PrivateTagStore store = new(temp.Path, lease);
        TagId piss = store.Define("piss").Tag, pee = store.Define("pee").Tag;
        store.Merge(piss, [pee]);

        Assert.Equal(piss, store.Define("pee").Tag);
        Assert.Equal(piss, store.Define("PEE").Tag);
        Assert.Equal(piss, store.Define("/pee/").Tag);
        Assert.Equal(piss, store.Define("#pee").Tag);
        PrivateTagStoreSnapshot snapshot = store.ReadSnapshot();
        Assert.Single(snapshot.Tags);
        Assert.Equal(piss, snapshot.Resolve("/Pee/", out PrivateTagAlias? via)?.Id);
        Assert.Equal("pee", via!.Name.Display);
        Assert.Equal(piss, snapshot.Resolve("piss", out via)?.Id);
        Assert.Null(via);
        Assert.Null(snapshot.Resolve("pees", out _)); // the plural rule is for suggestions only
        // Create-and-assign by an alias name assigns the target and creates nothing.
        Assert.Equal(piss, store.DefineAndSetMediaSubjects("pee", [new MediaTagSubject.Bound(Bound())]).Tag);
        Assert.Single(store.ReadSnapshot().Tags);
    }

    [Fact]
    public void AliasEditsAreValidatedPersistedAndFollowRenameAndDelete()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        PrivateTagStore store = new(temp.Path, lease);
        TagId cat = store.Define("cat").Tag, dog = store.Define("dog").Tag;
        Assert.Equal(1, store.ReadSnapshot().FormatVersion);

        Assert.True(store.AddAlias(cat, "kitty").Changed);
        Assert.False(store.AddAlias(cat, "KITTY").Changed);
        Assert.Equal(PrivateTagStoreError.NameCollision, Rejected(() => store.AddAlias(dog, "kitty")));
        Assert.Equal(PrivateTagStoreError.NameCollision, Rejected(() => store.AddAlias(dog, "Cat")));
        Assert.Equal(PrivateTagStoreError.NameCollision, Rejected(() => store.Rename(dog, "kitty")));
        Assert.Equal(3, store.ReadSnapshot().FormatVersion);
        Assert.True(File.Exists(Path.Combine(temp.Path, PrivateTagStore.V1BackupFileName)));

        // Renaming a tag to its own alias takes the alias back as its name.
        Assert.True(store.Rename(cat, "Kitty").Changed);
        PrivateTagStoreSnapshot renamed = store.ReadSnapshot();
        Assert.Empty(renamed.Aliases);
        Assert.Equal("Kitty", renamed.Find(cat)!.Name.Display);

        store.AddAlias(cat, "cat");
        Assert.True(store.RemoveAlias("CAT").Changed);
        Assert.False(store.RemoveAlias("cat").Changed);
        store.AddAlias(dog, "puppy");
        store.Delete(dog);
        Assert.Empty(store.ReadSnapshot().Aliases);
        Assert.Equal(3, store.ReadSnapshot().FormatVersion); // v3 stays v3
    }

    [Fact]
    public void RenamingATagToItsMergedAwayNameKeepsTheMergedIdResolving()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        PrivateTagStore store = new(temp.Path, lease);
        TagId keep = store.Define("cats").Tag, gone = store.Define("felines").Tag;
        store.Merge(keep, [gone]);
        Assert.Equal(keep, store.ReadSnapshot().ResolveId(gone));

        // The tag takes the merged-away name; the former ID keeps resolving without any name reserved.
        Assert.True(store.Rename(keep, "Felines").Changed);
        PrivateTagStoreSnapshot renamed = store.ReadSnapshot();
        Assert.Equal("Felines", renamed.Find(keep)!.Name.Display);
        Assert.Empty(renamed.Aliases);
        PrivateTagFormerId mapping = Assert.Single(renamed.Formers);
        Assert.Equal((gone, keep), (mapping.Former, mapping.Tag));
        Assert.Equal(keep, renamed.ResolveId(gone));
        // The old name is free: typing it gives nothing, and a new tag may take it.
        Assert.Null(renamed.Resolve("cats", out _));
        // Durable (nameless entry in the v3 aliases array).
        Assert.Equal(keep, new PrivateTagStore(temp.Path, lease).ReadSnapshot().ResolveId(gone));
        TagId fresh = store.Define("cats").Tag;
        Assert.NotEqual(keep, fresh);
        Assert.Equal(keep, store.ReadSnapshot().ResolveId(gone));
        // A later rename keeps the mapping; deleting the tag takes it with it.
        Assert.True(store.Rename(keep, "Lions").Changed);
        Assert.Equal(keep, store.ReadSnapshot().ResolveId(gone));
        store.Delete(keep);
        Assert.Null(store.ReadSnapshot().ResolveId(gone));
        Assert.Empty(store.ReadSnapshot().Formers);
    }

    [Fact]
    public void ANamelessFormerIdFollowsALaterMergeAndUndo()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        PrivateTagStore store = new(temp.Path, lease);
        TagId keep = store.Define("cats").Tag, gone = store.Define("felines").Tag, other = store.Define("lions").Tag;
        store.Merge(keep, [gone]);
        store.Rename(keep, "felines");
        PrivateTagMergeCommit merge = store.Merge(other, [keep]);
        Assert.Equal(other, store.ReadSnapshot().ResolveId(gone));
        Assert.Equal(other, Assert.Single(store.ReadSnapshot().Formers).Tag);
        store.UndoMerge(merge.Undo);
        Assert.Equal(keep, store.ReadSnapshot().ResolveId(gone));
    }

    [Fact]
    public void TheParserRejectsInvalidAliasSections()
    {
        string tag = "00000000-0000-0000-0000-0000000000a1", other = "00000000-0000-0000-0000-0000000000a2";
        string Document(string aliases) => "{\"Version\":3,\"Revision\":1,\"Tags\":[{\"Id\":\"" + tag + "\",\"Name\":\"cat\"}],"
            + "\"Threads\":[],\"Media\":[],\"LocalOccurrences\":[],\"LocalMedia\":[],\"Aliases\":[" + aliases + "]}";
        Assert.Equal(3, PrivateTagStore.ValidateDocumentBytes(Encoding.UTF8.GetBytes(Document(
            "{\"Name\":\"kitty\",\"Tag\":\"" + tag + "\",\"Former\":\"" + other + "\"}"))));
        // A nameless entry names a former ID; older documents without one stay readable.
        Assert.Equal(3, PrivateTagStore.ValidateDocumentBytes(Encoding.UTF8.GetBytes(Document(
            "{\"Name\":null,\"Tag\":\"" + tag + "\",\"Former\":\"" + other + "\"}"))));
        foreach (string bad in new[]
        {
            "{\"Name\":null,\"Tag\":\"" + tag + "\",\"Former\":null}",                       // nameless without a former ID
            "{\"Name\":null,\"Tag\":\"" + tag + "\",\"Former\":\"" + tag + "\"}",             // former ID still a tag
            "{\"Name\":null,\"Tag\":\"" + tag + "\",\"Former\":\"" + other + "\"},{\"Name\":\"kitty\",\"Tag\":\"" + tag + "\",\"Former\":\"" + other + "\"}",
            "{\"Name\":\"kitty\",\"Tag\":\"" + other + "\",\"Former\":null}",                      // unknown tag
            "{\"Name\":\"CAT\",\"Tag\":\"" + tag + "\",\"Former\":null}",                         // a tag's name
            "{\"Name\":\"kitty\",\"Tag\":\"" + tag + "\",\"Former\":\"" + tag + "\"}",              // former ID still a tag
            "{\"Name\":\"kitty\",\"Tag\":\"" + tag + "\",\"Former\":null},{\"Name\":\"Kitty\",\"Tag\":\"" + tag + "\",\"Former\":null}",
            "{\"Name\":\" kitty\",\"Tag\":\"" + tag + "\",\"Former\":null}",                      // not canonical
            "{\"Name\":\"kitty\",\"Tag\":\"" + tag + "\"}"                                        // missing field
        })
            Assert.Throws<InvalidDataException>(() => PrivateTagStore.ValidateDocumentBytes(Encoding.UTF8.GetBytes(Document(bad))));
        // Aliases in a v2 document are unknown fields.
        Assert.Throws<InvalidDataException>(() => PrivateTagStore.ValidateDocumentBytes(Encoding.UTF8.GetBytes(
            Document("").Replace("\"Version\":3", "\"Version\":2"))));
    }

    [Fact]
    public void SavedThreadSearchesThatNameAMergedTagKeepMatchingTheTarget()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        PrivateTagStore store = new(temp.Path, lease);
        TagId abdl = store.Define("abdl").Tag, slash = store.Define("/abdl/").Tag;
        ResourceId other = Thread with { Thread = "43" };
        store.SetThread(slash, Thread, true);
        store.SetThread(abdl, other, true);
        ArchiveTagFilter filter = new([slash.Value], []);
        PrivateTagMergeCommit merged = store.Merge(abdl, [slash]);

        Assert.True(ThreadTagMembership.TryCreate(filter, merged.Commit.Snapshot, out ThreadTagMembership? membership));
        Assert.True(membership!.Matches(Thread));
        Assert.True(membership.Matches(other));
        // Both the old and the new ID in one filter count as the one tag.
        Assert.True(ThreadTagMembership.TryCreate(new ArchiveTagFilter([slash.Value, abdl.Value], []),
            merged.Commit.Snapshot, out _));
        // Removing the merged-away alias forgets the old ID: the saved search reports an unknown tag, never guesses.
        PrivateTagStoreSnapshot forgotten = store.RemoveAlias("/abdl/").Snapshot;
        Assert.False(ThreadTagMembership.TryCreate(filter, forgotten, out _));
    }

    [Fact]
    public void TheNormaliserFoldsDecorationAndSuggestsDuplicatesOnly()
    {
        Assert.Equal("ABDL", TagNameFold.Fold(Name("/abdl/")));
        Assert.Equal("ABDL", TagNameFold.Fold(Name("#ABDL")));
        Assert.Equal("BIG TITS", TagNameFold.Fold(Name("big_tits")));
        Assert.Equal("BIG TITS", TagNameFold.Fold(Name("  Big   Tits ")));
        Assert.Equal("/", TagNameFold.Fold(Name("/")));     // nothing would remain: the key itself
        Assert.Equal("CAT", TagNameFold.Loose(Name("cats")));
        Assert.Equal("KISS", TagNameFold.Loose(Name("kiss")));
        Assert.Equal("BUS", TagNameFold.Loose(Name("bus")));
        Assert.Equal("XRAY", TagNameFold.Loose(Name("x-ray")));

        PrivateTagDefinition Tag(string name) => new(new TagId(Guid.NewGuid()), Name(name));
        PrivateTagDefinition abdl = Tag("abdl"), slash = Tag("/abdl/"), hash = Tag("#ABDL"), cat = Tag("cat"), cats = Tag("Cats"), dog = Tag("dog");
        Dictionary<TagId, int> uses = new() { [abdl.Id] = 5, [slash.Id] = 9, [cat.Id] = 1, [cats.Id] = 2 };

        IReadOnlyList<TagDuplicateGroup> groups = TagNameFold.Duplicates([abdl, slash, hash, cat, cats, dog], uses);

        Assert.Equal(2, groups.Count);
        Assert.Equal(new[] { slash, abdl, hash }, groups[0].Tags);
        Assert.Equal(TagDuplicateReason.Spelling, groups[0].Reason);
        Assert.Equal(new[] { cats, cat }, groups[1].Tags);
        Assert.Equal(TagDuplicateReason.Similar, groups[1].Reason);
    }
}

/// <summary>The merge on a large library: one bounded write; timings are observations, never assertions.</summary>
[Collection(PrivateTagStoreScaleCollection.Name)]
public sealed class PrivateTagMergeScaleEvidenceTests(ITestOutputHelper output)
{
    private static readonly ResourceId Thread = new("source", "board", "42");
    private const string RevisionKey = "att-v2:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private static PrivateMediaTagTarget Bound(string post, ResourceId thread)
    {
        Assert.True(PrivateMediaTagTarget.TryCreate(new AttachmentRevisionReference(thread, post, 0, RevisionKey), "media-1", "original",
            TransferMediaRole.Original, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(post))).ToLowerInvariant(),
            out PrivateMediaTagTarget? target, out _));
        return target!;
    }

    [Fact]
    [Trait("Category", "TagStoreScaleEvidence")]
    public void MergingSixteenThousandAssignmentsStaysOneBoundedWrite()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        PrivateTagStore store = new(temp.Path, lease);
        TagId target = store.Define("abdl").Tag, source = store.Define("/abdl/").Tag;
        const int count = 16_000;
        PrivateMediaTagTarget[] files = Enumerable.Range(0, count)
            .Select(index => Bound((1_000_000 + index).ToString(CultureInfo.InvariantCulture), Thread with { Thread = (index / 100).ToString(CultureInfo.InvariantCulture) }))
            .ToArray();
        store.SetMediaBatch(source, files, true);
        store.SetMediaBatch(target, files[..1000], true); // 1000 files carry both

        Stopwatch elapsed = Stopwatch.StartNew();
        PrivateTagMergeCommit merged = store.Merge(target, [source]);
        double merge = elapsed.Elapsed.TotalMilliseconds;
        elapsed.Restart();
        store.UndoMerge(merged.Undo);
        double undo = elapsed.Elapsed.TotalMilliseconds;

        Assert.Equal(count, merged.Commit.Snapshot.MediaAssignments.Count);
        Assert.All(merged.Commit.Snapshot.MediaAssignments, item => Assert.Equal(target, (TagId)item.TagId));
        Assert.Equal(count + 1000, store.ReadSnapshot().MediaAssignments.Count);
        output.WriteLine(FormattableString.Invariant($"merge of {count} media assignments: {merge:F0} ms, undo {undo:F0} ms"));
    }
}
