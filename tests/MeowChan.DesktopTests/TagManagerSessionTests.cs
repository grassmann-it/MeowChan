using MeowChan.Desktop;
using MeowChan.Persistence;
using MeowChan.Storage;
using MeowChan.Tags;
using MeowChan.Transfer;

namespace MeowChan.DesktopTests;

public sealed class TagManagerSessionTests
{
    private static ResourceId Thread(string id) => new("source", "board", id);

    private static PrivateMediaTagTarget Media(string thread)
    {
        Assert.True(PrivateMediaTagTarget.TryCreate(new AttachmentRevisionReference(Thread(thread), "p", 0, "att-v2:" + new string('a', 64)),
            "m", "original", TransferMediaRole.Original, new string('c', 64), out PrivateMediaTagTarget? target, out _));
        return target!;
    }

    private static TagManagerSession Session(PrivateTagStore store) => new(
        () => new PrivateTagLoadSnapshot(PrivateTagLoadState.Ready, store.ReadSnapshot(), null),
        (id, name, revision, token) => Task.FromResult(store.Rename(id, name, revision, token)),
        (id, revision, token) => Task.FromResult(store.Delete(id, revision, token)));

    [Fact]
    public async Task RowsCountUsesAndRenameKeepsThem()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        PrivateTagStore store = new(temp.Path, lease);
        TagId cat = store.Define("cat").Tag, dog = store.Define("dog").Tag;
        store.SetThread(cat, Thread("1"), true); store.SetThread(cat, Thread("2"), true); store.SetMedia(cat, Media("1"), true);
        TagManagerSession session = Session(store);
        int updates = 0;
        session.Updated += () => updates++;

        Assert.Equal(new[] { new TagManagerRow(cat, "cat", 2, 1), new TagManagerRow(dog, "dog", 0, 0) }, session.Rows);
        Assert.False(session.Changed);

        await session.RenameAsync(cat, "Katze");

        Assert.Equal(new TagManagerRow(cat, "Katze", 2, 1), session.Rows[0]);
        Assert.True(session.Changed);
        Assert.Equal("tags.manage.renamed", session.MessageKey);
        Assert.False(session.MessageIsError);
        Assert.False(session.Busy);
        Assert.Equal(2, updates);
        // Same name again is not a change and says nothing.
        await session.RenameAsync(cat, "Katze");
        Assert.Null(session.MessageKey);
    }

    [Fact]
    public void MediaCountIsDistinctFilesNotAssignmentsAcrossRevisionKeys()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        PrivateTagStore store = new(temp.Path, lease);
        TagId cat = store.Define("cat").Tag;
        string later = "att-v2:" + new string('b', 64);
        store.SetMedia(cat, Media("1"), true);
        Assert.Equal(1, Session(store).Rows.Single().Media);

        // The same file after a hand-over to a later revision key: two assignments, one file.
        using ProfileAdmissionToken admission = lease.Admission.EnterStoreWriter(default);
        Assert.True(store.CarryMediaAssignmentsAdmitted([new MediaTagKeyHandover("att-v2:" + new string('a', 64),
            new AttachmentRevisionReference(Thread("1"), "p", 0, later), new string('c', 64))], admission).Changed);
        Assert.Equal(2, store.ReadSnapshot().MediaAssignments.Count);
        Assert.Equal(1, Session(store).Rows.Single().Media);

        // A second, different file with the same tag counts as the second.
        store.SetMedia(cat, Media("2"), true);
        Assert.Equal(2, Session(store).Rows.Single().Media);
        Assert.Equal(2, store.ReadSnapshot().DistinctMediaCount(cat));
    }

    [Fact]
    public async Task CollisionAndInvalidNamesAreExplainedAndChangeNothing()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        PrivateTagStore store = new(temp.Path, lease);
        TagId cat = store.Define("cat").Tag;
        store.Define("dog");
        TagManagerSession session = Session(store);
        byte[] before = File.ReadAllBytes(Path.Combine(temp.Path, PrivateTagStore.FileName));

        await session.RenameAsync(cat, "DOG");
        Assert.Equal("tags.manage.error.collision", session.MessageKey);
        Assert.True(session.MessageIsError);
        await session.RenameAsync(cat, "line\nbreak");
        Assert.Equal("tags.error.invalidName", session.MessageKey);
        await session.RenameAsync(cat, null);
        Assert.Equal("tags.error.invalidName", session.MessageKey);

        Assert.False(session.Changed);
        Assert.Equal("cat", session.Rows[0].Name);
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(temp.Path, PrivateTagStore.FileName)));
    }

    [Fact]
    public async Task AnOutdatedViewIsRefusedRefreshedAndThenWorks()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        PrivateTagStore store = new(temp.Path, lease);
        TagId cat = store.Define("cat").Tag;
        TagManagerSession session = Session(store);
        TagId later = store.Define("added elsewhere").Tag;

        await session.DeleteAsync(cat);

        // The delete was confirmed against an older list: nothing is deleted, the list is refreshed.
        Assert.Equal("tags.error.stale", session.MessageKey);
        Assert.False(session.Changed);
        Assert.Equal(new[] { cat, later }, session.Rows.Select(row => row.Id));
        Assert.NotNull(store.ReadSnapshot().Find(cat));

        await session.DeleteAsync(cat);
        Assert.Equal("tags.manage.deleted", session.MessageKey);
        Assert.True(session.Changed);
        Assert.Equal(new[] { later }, session.Rows.Select(row => row.Id));

        // A tag that is already gone is reported and the list stays current.
        await session.DeleteAsync(cat);
        Assert.Equal("tags.manage.error.gone", session.MessageKey);
        Assert.Equal(new[] { later }, session.Rows.Select(row => row.Id));
    }

    [Fact]
    public async Task DeleteRemovesTheTagWithAllItsUses()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        PrivateTagStore store = new(temp.Path, lease);
        TagId cat = store.Define("cat").Tag, dog = store.Define("dog").Tag;
        store.SetThread(cat, Thread("1"), true); store.SetThread(dog, Thread("1"), true); store.SetMedia(cat, Media("1"), true);
        TagManagerSession session = Session(store);

        await session.DeleteAsync(cat);

        Assert.Equal(new[] { new TagManagerRow(dog, "dog", 1, 0) }, session.Rows);
        PrivateTagStoreSnapshot stored = store.ReadSnapshot();
        Assert.Equal(dog, Assert.Single(stored.TagsOf(Thread("1"))).Id);
        Assert.Empty(stored.MediaAssignments);
    }

    [Fact]
    public async Task UnavailableIndexAndBusySessionAcceptNoCommands()
    {
        int calls = 0;
        TagManagerSession unavailable = new(
            () => new PrivateTagLoadSnapshot(PrivateTagLoadState.Unavailable, null, "damaged"),
            (_, _, _, _) => { calls++; throw new InvalidOperationException(); },
            (_, _, _) => { calls++; throw new InvalidOperationException(); });
        await unavailable.RenameAsync(new TagId(Guid.NewGuid()), "x");
        await unavailable.DeleteAsync(new TagId(Guid.NewGuid()));
        Assert.Null(unavailable.Snapshot);
        Assert.Empty(unavailable.Rows);
        Assert.Equal(0, calls);

        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        PrivateTagStore store = new(temp.Path, lease);
        TagId cat = store.Define("cat").Tag, dog = store.Define("dog").Tag;
        TaskCompletionSource<PrivateTagCommit> gate = new();
        int deletes = 0;
        TagManagerSession session = new(
            () => new PrivateTagLoadSnapshot(PrivateTagLoadState.Ready, store.ReadSnapshot(), null),
            (_, _, _, _) => gate.Task,
            (id, revision, token) => { deletes++; return Task.FromResult(store.Delete(id, revision, token)); });
        Task running = session.RenameAsync(cat, "slow");
        Assert.True(session.Busy);
        // A second command while one is running is ignored, not queued.
        await session.DeleteAsync(dog);
        Assert.Equal(0, deletes);
        gate.SetResult(store.Rename(cat, "slow", session.Snapshot!.Revision));
        await running;
        Assert.False(session.Busy);
        Assert.Equal("slow", session.Rows[0].Name);
        Assert.NotNull(store.ReadSnapshot().Find(dog));
    }

    [Fact]
    public async Task UnexpectedFailureIsReportedAndTheListReloaded()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        PrivateTagStore store = new(temp.Path, lease);
        TagId cat = store.Define("cat").Tag;
        TagManagerSession session = new(
            () => new PrivateTagLoadSnapshot(PrivateTagLoadState.Ready, store.ReadSnapshot(), null),
            (_, _, _, _) => throw new IOException("disk"),
            (_, _, _) => throw new IOException("disk"));
        store.Define("later");

        await session.RenameAsync(cat, "x");

        Assert.Equal("tags.error.other", session.MessageKey);
        Assert.True(session.MessageIsError);
        Assert.False(session.Changed);
        Assert.Equal(2, session.Rows.Count);
    }
}
