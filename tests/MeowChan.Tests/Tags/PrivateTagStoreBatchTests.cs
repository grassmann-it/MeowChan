using MeowChan.Persistence;
using MeowChan.Storage;
using MeowChan.Tags;
using MeowChan.Transfer;

namespace MeowChan.Tests.Tags;

// One tag on several exact media versions in one write (the Medien page's "tag the selection").
public sealed class PrivateTagStoreBatchTests
{
    private const string FileName = "private-tags.json";
    private static ResourceId Thread(string id = "42") => new("source", "board", id);

    private static PrivateMediaTagTarget Media(string thread, string post = "100", char key = 'a', char sha = 'c')
    {
        Assert.True(PrivateMediaTagTarget.TryCreate(
            new AttachmentRevisionReference(Thread(thread), post, 0, "att-v2:" + new string(key, 64)),
            "media-" + post, "original", TransferMediaRole.Original, new string(sha, 64), out PrivateMediaTagTarget? target, out _));
        return target!;
    }

    private static PrivateTagStoreError Rejected(Action action) => Assert.Throws<PrivateTagStoreException>(action).Error;

    [Fact]
    public void SeveralTargetsAreTaggedAndUntaggedInOneWriteEach()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        PrivateTagStore store = new(temp.Path, lease);
        TagId cat = store.Define("cat").Tag;
        PrivateMediaTagTarget a = Media("1"), b = Media("2"), c = Media("3");
        long before = store.ReadSnapshot().Revision;

        PrivateTagCommit added = store.SetMediaBatch(cat, [a, b, c], true);

        Assert.True(added.Changed);
        Assert.Equal(before + 1, added.Snapshot.Revision); // three targets, one committed revision
        Assert.All(new[] { a, b, c }, target => Assert.Equal(cat, Assert.Single(added.Snapshot.TagsOf(target)).Id));

        PrivateTagCommit removed = store.SetMediaBatch(cat, [a, c], false);

        Assert.True(removed.Changed);
        Assert.Equal(before + 2, removed.Snapshot.Revision);
        Assert.Empty(removed.Snapshot.TagsOf(a));
        Assert.Empty(removed.Snapshot.TagsOf(c));
        Assert.Equal(cat, Assert.Single(removed.Snapshot.TagsOf(b)).Id); // the one left out keeps the tag
    }

    [Fact]
    public void OnlyMissingMembershipsAreWrittenAndRepeatsDoNotTouchTheFile()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        PrivateTagStore store = new(temp.Path, lease);
        TagId cat = store.Define("cat").Tag;
        PrivateMediaTagTarget a = Media("1"), b = Media("2");
        store.SetMedia(cat, a, true);

        PrivateTagCommit both = store.SetMediaBatch(cat, [a, b, b], true); // a is already there, b is listed twice
        byte[] written = File.ReadAllBytes(Path.Combine(temp.Path, FileName));
        PrivateTagCommit again = store.SetMediaBatch(cat, [a, b], true);
        PrivateTagCommit absent = store.SetMediaBatch(cat, [Media("9")], false);
        PrivateTagCommit empty = store.SetMediaBatch(cat, [], true);

        Assert.True(both.Changed);
        Assert.Equal(2, both.Snapshot.MediaAssignments.Count);
        Assert.False(again.Changed);
        Assert.False(absent.Changed);
        Assert.False(empty.Changed);
        Assert.Equal(written, File.ReadAllBytes(Path.Combine(temp.Path, FileName)));
    }

    [Fact]
    public void AnUnknownOrDeletedTagRefusesTheWholeBatchWithoutWriting()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        PrivateTagStore store = new(temp.Path, lease);
        TagId cat = store.Define("cat").Tag, doomed = store.Define("doomed").Tag;
        PrivateMediaTagTarget a = Media("1"), b = Media("2");
        store.SetMedia(cat, a, true);
        store.Delete(doomed);
        byte[] before = File.ReadAllBytes(Path.Combine(temp.Path, FileName));

        Assert.Equal(PrivateTagStoreError.UnknownTag, Rejected(() => store.SetMediaBatch(doomed, [a, b], true)));
        Assert.Equal(PrivateTagStoreError.UnknownTag, Rejected(() => store.SetMediaBatch(new TagId(Guid.NewGuid()), [a, b], false)));

        Assert.Equal(before, File.ReadAllBytes(Path.Combine(temp.Path, FileName)));
        Assert.Equal(cat, Assert.Single(store.ReadSnapshot().TagsOf(a)).Id);
        Assert.Empty(store.ReadSnapshot().TagsOf(b));
    }

    [Fact]
    public void OneInvalidTargetRefusesTheWholeBatch()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        PrivateTagStore store = new(temp.Path, lease);
        TagId cat = store.Define("cat").Tag;
        byte[] before = File.ReadAllBytes(Path.Combine(temp.Path, FileName));

        Assert.Equal(PrivateTagStoreError.InvalidTarget, Rejected(() => store.SetMediaBatch(cat, [Media("1"), null], true)));

        Assert.Equal(before, File.ReadAllBytes(Path.Combine(temp.Path, FileName)));
        Assert.Empty(store.ReadSnapshot().MediaAssignments);
    }

    [Fact]
    public void AStaleRevisionRefusesTheBatchAndLeavesTheNewerStateAlone()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        PrivateTagStore store = new(temp.Path, lease);
        TagId cat = store.Define("cat").Tag;
        long seen = store.ReadSnapshot().Revision;
        store.Define("dog"); // someone else changed the tags after the caller looked
        byte[] before = File.ReadAllBytes(Path.Combine(temp.Path, FileName));

        Assert.Equal(PrivateTagStoreError.StaleRevision, Rejected(() => store.SetMediaBatch(cat, [Media("1"), Media("2")], true, seen)));

        Assert.Equal(before, File.ReadAllBytes(Path.Combine(temp.Path, FileName)));
        PrivateTagCommit fresh = store.SetMediaBatch(cat, [Media("1"), Media("2")], true, seen + 1);
        Assert.Equal(2, fresh.Snapshot.MediaAssignments.Count);
    }

    [Fact]
    public void BatchesNeverTouchThreadTagsAndSurviveReopening()
    {
        using TempDirectory temp = new();
        TagId cat;
        PrivateMediaTagTarget a = Media("1"), b = Media("2");
        using (ProfileLease lease = ProfileLease.Acquire(temp.Path))
        {
            PrivateTagStore store = new(temp.Path, lease);
            cat = store.Define("cat").Tag;
            store.SetThread(cat, Thread("1"), true);
            store.SetMediaBatch(cat, [a, b], true);
            store.SetMediaBatch(cat, [a, b], false);
            store.SetMediaBatch(cat, [a], true);
        }

        using ProfileLease reopened = ProfileLease.Acquire(temp.Path);
        PrivateTagStoreSnapshot snapshot = new PrivateTagStore(temp.Path, reopened).ReadSnapshot();

        Assert.Equal(cat, Assert.Single(snapshot.TagsOf(Thread("1"))).Id); // the thread tag stayed, removal was media only
        Assert.Equal(cat, Assert.Single(snapshot.TagsOf(a)).Id);
        Assert.Empty(snapshot.TagsOf(b));
        Assert.Single(snapshot.MediaAssignments);
    }
}
