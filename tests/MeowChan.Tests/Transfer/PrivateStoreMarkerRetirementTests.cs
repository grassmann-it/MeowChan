using System.Text.Json;
using MeowChan.Favorites;
using MeowChan.Persistence;
using MeowChan.Transfer;

namespace MeowChan.Tests.Transfer;

public sealed class PrivateStoreMarkerRetirementTests
{
    private static readonly ResourceId Thread = new("synthetic", "board", "retirement");
    private static readonly Guid Operation = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
    private static readonly string Plan = new('c', 64);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void ExactMarkerBearingAfterCanBeRetiredAndRetriedWithoutBlockingOrdinaryWriter(int family)
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        Store store = new(family, lease);
        PrivateStoreBatch batch = store.Prepare();
        Assert.Equal(PrivateStoreBatchInspection.ExactAfterWithMarker, store.Commit(batch));

        NativePrivateApplyStoreImage expectedBackup = batch.Fence.Before.Exists
            ? batch.Fence.Before : batch.Fence.BeforeBackup!;
        Assert.Equal(PrivateStoreMarkerRetirementResult.Retired,
            store.Retire(batch, batch.Fence.ExpectedAfter, expectedBackup));
        // A second call observes the exact unmarked after-image; it never rewrites
        // the store or rotates the protected backup again.
        Assert.Equal(PrivateStoreMarkerRetirementResult.AlreadyRetired,
            store.Retire(batch, batch.Fence.ExpectedAfter, expectedBackup));

        lease.Admission.CompleteStartup();
        store.AddOrdinaryValue();
        Assert.Equal(2, store.Count());
        Assert.DoesNotContain("OperationMarker", File.ReadAllText(store.PrimaryPath),
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void RetryingAfterDifferentUnmarkedDocumentDoesNotReturnAlreadyRetired(int family)
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        Store store = new(family, lease);
        PrivateStoreBatch batch = store.Prepare();
        store.Commit(batch);
        NativePrivateApplyStoreImage expectedBackup = batch.Fence.Before.Exists
            ? batch.Fence.Before : batch.Fence.BeforeBackup!;

        // Keep the original backup fence so the test isolates the retry image
        // rather than accidentally relying on backup rotation to reject it.
        string backupPath = store.PrimaryPath + ".bak";
        byte[]? originalBackup = File.Exists(backupPath) ? File.ReadAllBytes(backupPath) : null;

        Assert.Equal(PrivateStoreMarkerRetirementResult.Retired,
            store.Retire(batch, batch.Fence.ExpectedAfter, expectedBackup));
        store.AddOrdinaryValue();
        if (family == 2)
        {
            // Bookmarks have no backup fence; the changed primary alone must
            // prevent the old operation from being acknowledged.
        }
        else if (originalBackup is null)
        {
            File.Delete(backupPath);
        }
        else
        {
            File.WriteAllBytes(backupPath, originalBackup);
        }

        Assert.Throws<InvalidDataException>(() => store.Retire(
            batch, batch.Fence.ExpectedAfter, expectedBackup));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void EveryRetirementFenceIsExactAndMismatchCannotUnblockOrMutate(int family)
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        Store store = new(family, lease);
        PrivateStoreBatch batch = store.Prepare();
        store.Commit(batch);
        NativePrivateApplyStoreImage expectedBackup = batch.Fence.Before.Exists
            ? batch.Fence.Before : batch.Fence.BeforeBackup!;

        Assert.Throws<InvalidDataException>(() => store.Retire(batch, batch.Fence.ExpectedAfter,
            expectedBackup, operation: Guid.NewGuid()));
        Assert.Equal(PrivateStoreBatchInspection.ExactAfterWithMarker, store.Inspect(batch));
        Assert.Throws<InvalidDataException>(() => store.Retire(batch,
            batch.Fence.ExpectedAfter, expectedBackup, plan: new('d', 64)));
        Assert.Equal(PrivateStoreBatchInspection.ExactAfterWithMarker, store.Inspect(batch));
        Assert.Throws<InvalidDataException>(() => store.Retire(batch,
            batch.Fence.Before, expectedBackup));
        Assert.Equal(PrivateStoreBatchInspection.ExactAfterWithMarker, store.Inspect(batch));

        NativePrivateApplyStoreKind wrongKind = family == 0
            ? NativePrivateApplyStoreKind.ThreadFavorites : NativePrivateApplyStoreKind.MediaFavorites;
        if (family == 2) wrongKind = NativePrivateApplyStoreKind.MediaFavorites;
        Assert.Throws<InvalidDataException>(() => store.Retire(batch,
            batch.Fence.ExpectedAfter, expectedBackup, kind: wrongKind));
        Assert.Equal(PrivateStoreBatchInspection.ExactAfterWithMarker, store.Inspect(batch));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void ExistingPendingEvidenceFailsClosed(int family)
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        Store store = new(family, lease);
        PrivateStoreBatch batch = store.Prepare();
        store.Commit(batch);
        NativePrivateApplyStoreImage expectedBackup = batch.Fence.Before.Exists
            ? batch.Fence.Before : batch.Fence.BeforeBackup!;
        string pending = Path.Combine(temp.Path, store.FileName + ".pending-foreign");
        File.WriteAllText(pending, "evidence");
        try
        {
            Assert.Throws<InvalidDataException>(() => store.Retire(batch,
                batch.Fence.ExpectedAfter, expectedBackup));
            Assert.Equal(PrivateStoreBatchInspection.ExactAfterWithMarker, store.Inspect(batch));
        }
        finally { File.Delete(pending); }
    }

    [Fact]
    public void ForeignMarkerAndDisposedAdmissionFailClosed()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        ThreadFavoriteStore store = new(temp.Path, lease);
        PrivateStoreBatch batch = store.PrepareAdditiveBatch(Operation, Plan, [Thread]);
        store.CommitPreparedBatch(batch);
        NativePrivateApplyStoreImage backup = batch.Fence.Before.Exists
            ? batch.Fence.Before : batch.Fence.BeforeBackup!;

        string path = Path.Combine(temp.Path, "thread-favorites.json");
        byte[] committedBytes = File.ReadAllBytes(path);
        using (JsonDocument json = JsonDocument.Parse(committedBytes))
        {
            using MemoryStream bytes = new();
            using (Utf8JsonWriter writer = new(bytes))
            {
                writer.WriteStartObject();
                foreach (JsonProperty property in json.RootElement.EnumerateObject())
                {
                    if (property.NameEquals("OperationMarker"))
                    {
                        writer.WritePropertyName(property.Name);
                        writer.WriteStartObject();
                        writer.WriteString("OperationId", Guid.NewGuid());
                        writer.WriteString("ContentPlanSha256", new string('e', 64));
                        writer.WriteNumber("StoreKind", (int)NativePrivateApplyStoreKind.ThreadFavorites);
                        writer.WriteEndObject();
                    }
                    else property.WriteTo(writer);
                }
                writer.WriteEndObject();
            }
            File.WriteAllBytes(path, bytes.ToArray());
        }
        Assert.Throws<InvalidDataException>(() => store.RetirePreparedBatchMarker(
            batch, Operation, Plan, NativePrivateApplyStoreKind.ThreadFavorites,
            batch.Fence.ExpectedAfter, backup));

        // Restore the exact committed bytes before exercising the admission fence.
        // The foreign replacement above is deliberately not a recovery operation.
        File.WriteAllBytes(path, committedBytes);
        store = new ThreadFavoriteStore(temp.Path, lease);
        ProfileAdmissionToken disposed = lease.Admission.EnterStartupWriter();
        disposed.Dispose();
        Assert.Throws<InvalidOperationException>(() => store.RetirePreparedBatchMarkerAdmitted(
            batch, Operation, Plan, NativePrivateApplyStoreKind.ThreadFavorites,
            batch.Fence.ExpectedAfter, backup, disposed));
    }

    private sealed class Store
    {
        private readonly int _family;
        private readonly ProfileLease _lease;
        private readonly FavoriteStore _media;
        private readonly ThreadFavoriteStore _threads;
        private readonly PostBookmarkStore _bookmarks;
        private readonly FavoriteEntry _favorite = new(
            new FavoriteAssetKey(Thread, "media", "original", new string('a', 64)), 12, "media.jpg");
        private readonly PostBookmarkKey _bookmark = new(Thread, "post");

        internal Store(int family, ProfileLease lease)
        {
            _family = family;
            _lease = lease;
            _media = new(lease.ProfileDirectory, lease);
            _threads = new(lease.ProfileDirectory, lease);
            _bookmarks = PostBookmarkStore.OpenWithoutCleanup(lease.ProfileDirectory, lease);
        }

        internal string FileName => _family switch
        {
            0 => "media-favorites.json",
            1 => "thread-favorites.json",
            _ => "post-bookmarks.json",
        };
        internal string PrimaryPath => Path.Combine(_lease.ProfileDirectory, FileName);

        internal PrivateStoreBatch Prepare() => _family switch
        {
            0 => _media.PrepareAdditiveBatch(Operation, Plan, [_favorite]),
            1 => _threads.PrepareAdditiveBatch(Operation, Plan, [Thread]),
            _ => _bookmarks.PrepareAdditiveBatch(Operation, Plan, [new(_bookmark, "note")]),
        };
        internal PrivateStoreBatchInspection Commit(PrivateStoreBatch batch) => _family switch
        {
            0 => _media.CommitPreparedBatch(batch),
            1 => _threads.CommitPreparedBatch(batch),
            _ => _bookmarks.CommitPreparedBatch(batch),
        };
        internal PrivateStoreBatchInspection Inspect(PrivateStoreBatch batch) => _family switch
        {
            0 => _media.InspectPreparedBatch(batch),
            1 => _threads.InspectPreparedBatch(batch),
            _ => _bookmarks.InspectPreparedBatch(batch),
        };
        internal PrivateStoreMarkerRetirementResult Retire(PrivateStoreBatch batch,
            NativePrivateApplyStoreImage expectedAfter, NativePrivateApplyStoreImage expectedBackup,
            Guid? operation = null, string? plan = null,
            NativePrivateApplyStoreKind? kind = null)
        {
            Guid op = operation ?? Operation;
            string content = plan ?? Plan;
            NativePrivateApplyStoreKind storeKind = kind ?? (_family switch
            {
                0 => NativePrivateApplyStoreKind.MediaFavorites,
                1 => NativePrivateApplyStoreKind.ThreadFavorites,
                _ => NativePrivateApplyStoreKind.PostBookmarksAndNotes,
            });
            NativePrivateApplyStoreImage? backup = _family == 2 ? null : expectedBackup;
            return _family switch
            {
                0 => _media.RetirePreparedBatchMarker(batch, op, content, storeKind, expectedAfter, backup),
                1 => _threads.RetirePreparedBatchMarker(batch, op, content, storeKind, expectedAfter, backup),
                _ => _bookmarks.RetirePreparedBatchMarker(batch, op, content, storeKind, expectedAfter, backup),
            };
        }
        internal void AddOrdinaryValue()
        {
            if (_family == 0) _media.SetFavorite(_favorite with
            {
                Key = _favorite.Key with { MediaIdentifier = "media-2" }
            }, true);
            else if (_family == 1) _threads.Set(Thread with { Thread = "retirement-2" }, true);
            else _bookmarks.Upsert(new PostBookmarkKey(Thread, "post-2"), "note-2", _bookmarks.Read().Revision,
                expectedExists: false);
        }
        internal int Count() => _family switch
        {
            0 => _media.List().Count,
            1 => _threads.ReadSnapshot().Threads.Count,
            _ => _bookmarks.Read().Bookmarks.Count,
        };
    }
}
