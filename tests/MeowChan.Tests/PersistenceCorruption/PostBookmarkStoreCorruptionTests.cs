using MeowChan.Interfaces;
using MeowChan.Persistence;
using Xunit.Abstractions;

namespace MeowChan.Tests.PersistenceCorruption;

/// <summary>post-bookmarks.json keeps no backup by design: a damaged file must be refused, never replaced.</summary>
internal sealed class PostBookmarkStoreAdapter : StoreAdapter
{
    internal override string Name => "post-bookmarks";
    internal override string PrimaryName => "post-bookmarks.json";
    internal override string VersionProperty => "schemaVersion";
    internal override long ReadCeilingBytes => PostBookmarkStore.MaximumDocumentBytes;
    internal override bool HasBackup => false;

    private static PostBookmarkKey Key(int index) => new(new ResourceId("4chan", "g", "123"), "12" + index);

    internal override void Seed(string directory, ProfileLease lease)
    {
        PostBookmarkStore store = PostBookmarkStore.OpenWithoutCleanup(directory, lease);
        PostBookmarkSnapshot first = store.Upsert(Key(1), "note " + Secret, 0, false);
        store.Upsert(Key(2), "", first.Revision, false);
    }

    internal override ReadResult Read(string directory, ProfileLease lease) => ReadResult.Guard(() =>
    {
        PostBookmarkSnapshot snapshot = PostBookmarkStore.ReadOnly(directory, lease);
        return snapshot.Revision + ":" + string.Join("|", snapshot.Bookmarks.Select(bookmark => bookmark.Key + "=" + bookmark.Note));
    });

    internal override void Mutate(string directory, ProfileLease lease) =>
        PostBookmarkStore.OpenWithoutCleanup(directory, lease).Upsert(Key(3), "new", 2, false);
}

public sealed class PostBookmarkStoreCorruptionTests(ITestOutputHelper log) : StoreCorruptionTests(log)
{
    internal override StoreAdapter Adapter { get; } = new PostBookmarkStoreAdapter();
}
