using System.Collections.Immutable;
using System.Security.Cryptography;
using MeowChan.Favorites;
using MeowChan.Persistence;
using MeowChan.Transfer;

namespace MeowChan.Tests.Transfer;

// Real store bytes + durable journal on synthetic profiles, then reopen with a
// different lease and no ordinary store constructors. Not product import/Apply.
public sealed class NativePrivateApplyJournalStoreInspectionTests
{
    private static readonly ResourceId Thread = new("synthetic", "b", "100");
    private static readonly Guid Operation = Guid.Parse("f717fa74-f5a4-4bd7-8fc5-0e37a0bc5e72");
    private static readonly Guid Profile = Guid.Parse("01e288ce-e400-4010-b128-c6fab2b7dce2");
    private static string Hash(char c) => new(c, 64);
    private static NativePrivateMetadataSelection Selection(int bits) => new((bits & 1) != 0, (bits & 2) != 0, (bits & 4) != 0);
    private static string StoreName(NativePrivateApplyStoreKind kind) => kind switch
    {
        NativePrivateApplyStoreKind.ThreadFavorites => "thread-favorites.json",
        NativePrivateApplyStoreKind.MediaFavorites => "media-favorites.json",
        _ => "post-bookmarks.json"
    };
    private static string OperationDirectory(string profile) => Path.Combine(profile,
        NativePrivateApplyJournal.DirectoryName, Operation.ToString("N"));

    private sealed class Prepared
    {
        internal NativePrivateApplyRequest Request { get; }
        private readonly Dictionary<NativePrivateApplyStoreKind, Func<PrivateStoreBatchInspection>> _commits = [];
        internal Prepared(ProfileLease lease, int bits, bool existing = false, bool empty = false)
        {
            var selection = Selection(bits);
            var threads = new ThreadFavoriteStore(lease.ProfileDirectory, lease);
            var media = new FavoriteStore(lease.ProfileDirectory, lease);
            var notes = new PostBookmarkStore(lease.ProfileDirectory, lease);
            if (existing)
            {
                threads.Set(Thread with { Thread = "old" }, true);
                media.SetFavorite(new(new(Thread, "old", "original", Hash('e')), 13, "old-target-name.jpg"), true);
                notes.Upsert(new(Thread, "old"), "old-target-note", notes.Read().Revision, expectedExists: false);
            }
            FavoriteEntry incomingMedia = new(new(Thread, "new", "original", Hash('f')), 23, "new.jpg");
            PostBookmark incomingNote = new(new(Thread, "101"), "incoming Ω\n exact");
            var projection = new NativePrivateMetadataProjection([Thread], selection,
                selection.ThreadFavorites && !empty ? [Thread] : [],
                selection.MediaFavorites && !empty ? [incomingMedia.Key] : [],
                selection.PostBookmarksAndNotes && !empty ? [incomingNote] : []);
            var comparison = new NativePrivateMetadataComparisonResult(selection, projection,
                projection.ThreadFavorites.Select(x => new NativePrivateThreadFavoriteComparison(x, NativePrivateMetadataDisposition.Add)).ToImmutableArray(),
                projection.MediaFavorites.Select(x => new NativePrivateMediaFavoriteComparison(x, NativePrivateMetadataDisposition.Add)).ToImmutableArray(),
                projection.PostBookmarksAndNotes.Select(x => new NativePrivateBookmarkComparison(x.Key, x.Note, NativePrivateMetadataDisposition.Add)).ToImmutableArray());
            var binding = new NativePrivateApplyRequestBinding(1024, Hash('1'), Hash('2'), 256, Hash('3'), Profile, Hash('4'));
            List<PrivateStoreBatch> Prepare(string plan)
            {
                List<PrivateStoreBatch> result = [];
                if (selection.ThreadFavorites) result.Add(threads.PrepareAdditiveBatch(Operation, plan, empty ? [] : [Thread]));
                if (selection.MediaFavorites) result.Add(media.PrepareAdditiveBatch(Operation, plan, empty ? [] : [incomingMedia]));
                if (selection.PostBookmarksAndNotes) result.Add(notes.PrepareAdditiveBatch(Operation, plan, empty ? [] : [incomingNote]));
                return result;
            }
            var seed = NativePrivateApplyRequest.Create(Operation, comparison, binding, Prepare(Hash('a')).Select(x => x.Fence).ToImmutableArray());
            var batches = Prepare(seed.ContentPlanSha256);
            Request = NativePrivateApplyRequest.Create(Operation, comparison, binding, batches.Select(x => x.Fence).ToImmutableArray());
            Assert.Equal(seed.ContentPlanSha256, Request.ContentPlanSha256);
            foreach (var batch in batches)
                _commits.Add(batch.Fence.Kind, batch.Fence.Kind switch
                {
                    NativePrivateApplyStoreKind.ThreadFavorites => () => threads.CommitPreparedBatch(batch),
                    NativePrivateApplyStoreKind.MediaFavorites => () => media.CommitPreparedBatch(batch),
                    _ => () => notes.CommitPreparedBatch(batch)
                });
        }
        internal void Commit() { foreach (var action in _commits.Values) Assert.Equal(PrivateStoreBatchInspection.ExactAfterWithMarker, action()); }
    }

    private static Dictionary<string, string> Files(string directory) => Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
        .Where(x => Path.GetFileName(x) is not (".meowchan-profile.lock" or ".ochan-profile.lock"))
        .ToDictionary(x => Path.GetRelativePath(directory, x), x => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(x))), StringComparer.Ordinal);
    private static void Unchanged(Dictionary<string, string> before, string directory)
        => Assert.Equal(before.OrderBy(x => x.Key, StringComparer.Ordinal), Files(directory).OrderBy(x => x.Key, StringComparer.Ordinal));

    [Theory]
    [InlineData(1, false)] [InlineData(2, false)] [InlineData(3, false)] [InlineData(4, false)]
    [InlineData(5, false)] [InlineData(6, false)] [InlineData(7, false)]
    [InlineData(1, true)] [InlineData(2, true)] [InlineData(3, true)] [InlineData(4, true)]
    [InlineData(5, true)] [InlineData(6, true)] [InlineData(7, true)]
    public void RestartReadsActualSelectedStoreMarkersWithoutAdvancingJournal(int bits, bool existing)
    {
        using TempDirectory temp = new();
        NativePrivateApplyRequest request;
        using (var lease = ProfileLease.Acquire(temp.Path))
        {
            var prepared = new Prepared(lease, bits, existing); request = prepared.Request;
            var journal = NativePrivateApplyJournal.Open(lease); journal.Claim(request);
            prepared.Commit();
        }
        using var reopened = ProfileLease.Acquire(temp.Path);
        var before = Files(temp.Path);
        var result = NativePrivateApplyJournal.Open(reopened).InspectStores(Operation);
        Assert.Equal(request.RequestSha256, result.Journal.RequestSha256);
        Assert.Equal(request.StoreFences.Select(x => x.Kind), result.Stores.Select(x => x.StoreKind));
        Assert.All(result.Stores, x => Assert.Equal(PrivateStoreBatchInspection.ExactAfterWithMarker, x.Inspection));
        Assert.Equal(NativePrivateApplyOperationPhase.Claimed, result.Journal.State.Phase);
        Assert.All(result.Journal.State.Steps, x => Assert.Equal(NativePrivateApplyStepPhase.Planned, x.Phase));
        Assert.Equal(0, result.Journal.EventCount); // Byte facts cannot invent an intent or receipt.
        Unchanged(before, temp.Path);
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)] [InlineData(6)] [InlineData(7)]
    public void ExactBeforeOnRestartIsOnlyASampleAndDoesNotBecomeNotStartedProven(int bits)
    {
        using TempDirectory temp = new();
        using (var lease = ProfileLease.Acquire(temp.Path))
            NativePrivateApplyJournal.Open(lease).Claim(new Prepared(lease, bits).Request);
        using var reopened = ProfileLease.Acquire(temp.Path);
        var before = Files(temp.Path);
        var result = NativePrivateApplyJournal.Open(reopened).InspectStores(Operation);
        Assert.All(result.Stores, x => Assert.Equal(PrivateStoreBatchInspection.ExactBefore, x.Inspection));
        Assert.All(result.Journal.State.Steps, x => Assert.Equal(NativePrivateApplyStepPhase.Planned, x.Phase));
        Assert.Equal(0, result.Journal.EventCount);
        Unchanged(before, temp.Path);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)] [InlineData(6)] [InlineData(7)]
    public void UnselectedCorruptStoresAndPendingEvidenceAreNeverReadOrCleaned(int bits)
    {
        using TempDirectory temp = new(); using var lease = ProfileLease.Acquire(temp.Path);
        var journal = NativePrivateApplyJournal.Open(lease); var prepared = new Prepared(lease, bits, empty: true);
        journal.Claim(prepared.Request);
        foreach (var kind in Enum.GetValues<NativePrivateApplyStoreKind>())
        {
            if (NativePrivateApplyRecovery.IsSelected(Selection(bits), kind)) continue;
            string name = StoreName(kind);
            File.WriteAllText(Path.Combine(temp.Path, name), "{broken unselected secret");
            File.WriteAllText(Path.Combine(temp.Path, name + ".bak"), "never read me");
            File.WriteAllText(Path.Combine(temp.Path, name + ".pending-" + Guid.NewGuid().ToString("N")), "preserve");
        }
        var before = Files(temp.Path); var result = journal.InspectStores(Operation);
        Assert.Equal(prepared.Request.StoreFences.Length, result.Stores.Length);
        Assert.All(result.Stores, x => Assert.Equal(PrivateStoreBatchInspection.NoChange, x.Inspection));
        Assert.Equal(0, result.Journal.EventCount);
        Unchanged(before, temp.Path);
    }

    [Fact]
    public void HistoricalReceiptBypassesMissingRequestAndCorruptCurrentStores()
    {
        using TempDirectory temp = new(); using var lease = ProfileLease.Acquire(temp.Path);
        var request = new Prepared(lease, 7, empty: true).Request;
        var journal = NativePrivateApplyJournal.Open(lease);
        var current = journal.Claim(request); current = journal.AppendPrepared(current.Snapshot); current = journal.AppendApplying(current.Snapshot);
        for (int i = 0; i < 3; i++)
            current = journal.AppendObservation(current.Snapshot, NativePrivateApplyOperationObservation.None,
                Enumerable.Range(0, 3).Select(index => index == i ? NativePrivateApplyStepObservation.NoChangeVerified
                    : NativePrivateApplyStepObservation.NotObserved).ToImmutableArray());
        var receipt = new NativePrivateApplyTerminalReceipt(Operation, request.RequestSha256, request.Selection,
            NativePrivateApplyOperationPhase.Completed, current.State.Steps);
        journal.AppendReceipt(current.Snapshot, receipt);
        File.Delete(Path.Combine(OperationDirectory(temp.Path), "request.json"));
        File.Delete(Path.Combine(OperationDirectory(temp.Path), "claim.json"));
        foreach (var kind in Enum.GetValues<NativePrivateApplyStoreKind>())
        {
            File.WriteAllText(Path.Combine(temp.Path, StoreName(kind)), "not a valid current store");
            Directory.CreateDirectory(Path.Combine(temp.Path, StoreName(kind) + ".private-pending-uncertain"));
        }
        var before = Files(temp.Path); var result = NativePrivateApplyJournal.Open(lease).InspectStores(Operation);
        Assert.True(result.Journal.HasTerminalReceipt); Assert.Empty(result.Stores);
        Assert.Equal(NativePrivateApplyOperationPhase.Completed, result.Journal.State.Phase);
        Unchanged(before, temp.Path);
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(4)]
    public void ByteSamplesNeverAdvanceOrDowngradeDurableIntentHistory(int bits)
    {
        using TempDirectory temp = new(); using var lease = ProfileLease.Acquire(temp.Path);
        var prepared = new Prepared(lease, bits); var journal = NativePrivateApplyJournal.Open(lease);
        var current = journal.Claim(prepared.Request); current = journal.AppendPrepared(current.Snapshot);
        current = journal.AppendApplying(current.Snapshot);
        current = journal.AppendObservation(current.Snapshot, NativePrivateApplyOperationObservation.None,
            [NativePrivateApplyStepObservation.IntentDurable]);
        prepared.Commit();
        var after = journal.InspectStores(Operation);
        Assert.Equal(PrivateStoreBatchInspection.ExactAfterWithMarker, Assert.Single(after.Stores).Inspection);
        Assert.Equal(NativePrivateApplyStepPhase.IntentDurable, Assert.Single(after.Journal.State.Steps).Phase);
        File.AppendAllText(Path.Combine(temp.Path, StoreName(prepared.Request.StoreFences[0].Kind)), " ");
        var before = Files(temp.Path); var drift = journal.InspectStores(Operation);
        Assert.Equal(PrivateStoreBatchInspection.Drifted, Assert.Single(drift.Stores).Inspection);
        Assert.Equal(NativePrivateApplyStepPhase.IntentDurable, Assert.Single(drift.Journal.State.Steps).Phase);
        Assert.Equal(current.EventCount, drift.Journal.EventCount);
        Assert.Equal(current.LastRecordSha256, drift.Journal.LastRecordSha256);
        Unchanged(before, temp.Path);
    }

    [Theory]
    [InlineData("request.json")] [InlineData("receipt.json")] [InlineData("claim.json")]
    public void InvalidJournalNeverFallsThroughToAStoreOrCreatesAnything(string fileName)
    {
        using TempDirectory temp = new(); using var lease = ProfileLease.Acquire(temp.Path);
        var journal = NativePrivateApplyJournal.Open(lease); journal.Claim(new Prepared(lease, 7).Request);
        File.WriteAllText(Path.Combine(OperationDirectory(temp.Path), fileName), "{broken");
        File.WriteAllText(Path.Combine(temp.Path, "post-bookmarks.json.pending-" + Guid.NewGuid().ToString("N")), "must survive");
        var before = Files(temp.Path);
        Assert.Throws<InvalidDataException>(() => journal.InspectStores(Operation));
        Unchanged(before, temp.Path);
    }

    [Fact]
    public void MissingOperationAndDisposedLeaseDoNotCreateRoots()
    {
        using TempDirectory temp = new(); var lease = ProfileLease.Acquire(temp.Path);
        var journal = NativePrivateApplyJournal.Open(lease); var before = Files(temp.Path);
        Assert.Throws<InvalidDataException>(() => journal.InspectStores(Operation)); Unchanged(before, temp.Path);
        lease.Dispose();
        Assert.Throws<ObjectDisposedException>(() => journal.InspectStores(Operation)); Unchanged(before, temp.Path);
        Assert.False(Directory.Exists(Path.Combine(temp.Path, NativePrivateApplyJournal.DirectoryName)));
    }
}
