using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowChan.Persistence;
using MeowChan.Transfer;

namespace MeowChan.Tests.Transfer;

// All files and notes here are synthetic, under owned test-only TempDirectory roots.
public sealed class PostBookmarkPrivateBatchTests
{
    private static readonly Guid Operation = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid OtherOperation = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
    private const string PlanHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string OldNote = "SYNTHETIC-OLD-NOTE-DO-NOT-LOG";
    private const string IncomingNote = "SYNTHETIC-INCOMING-NOTE-DO-NOT-LOG";
    private static PostBookmarkKey Key(string post = "11", string source = "synthetic", string board = "b", string thread = "10")
        => new(new(source, board, thread), post);
    private static PostBookmark Entry(string post = "11", string note = IncomingNote) => new(Key(post), note);
    private static PrivateStoreBatch Prepare(PostBookmarkStore store, params PostBookmark[] entries)
        => store.PrepareAdditiveBatch(Operation, PlanHash, entries);
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static void Legacy(string path, long revision, params PostBookmark[] entries)
        => File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1, revision,
            bookmarks = entries.Select(e => new
            {
                source = e.Key.Thread.Source, board = e.Key.Thread.Board, thread = e.Key.Thread.Thread,
                post = e.Key.Post, note = e.Note
            })
        }));
    private static void Opaque(Exception error, string profile)
    {
        Assert.Null(error.InnerException);
        Assert.DoesNotContain(profile, error.ToString());
        Assert.DoesNotContain(OldNote, error.ToString());
        Assert.DoesNotContain(IncomingNote, error.ToString());
    }

    [Fact]
    public void MissingSelectedEmptyIsExplicitAndNeverWrites()
    {
        using TempDirectory temp = new(); using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        var store = new PostBookmarkStore(temp.Path, lease);
        PrivateStoreBatch batch = Prepare(store);
        Assert.False(batch.HasChanges); Assert.Equal(0, batch.Additions); Assert.Empty(batch.CopyAfterBytes());
        Assert.False(batch.Fence.Before.Exists); Assert.Equal(0, batch.Fence.Before.Schema);
        Assert.Null(batch.Fence.Before.Instance); Assert.Equal(0, batch.Fence.Before.Revision);
        Assert.Equal(0, batch.Fence.Before.ByteLength); Assert.Null(batch.Fence.Before.Sha256);
        Assert.Null(batch.Fence.BeforeBackup);
        Assert.True(PrivateStoreBatchFiles.Same(batch.Fence.Before, batch.Fence.ExpectedAfter));
        Assert.Equal(PrivateStoreBatchInspection.NoChange, store.InspectPreparedBatch(batch));
        Assert.Equal(PrivateStoreBatchInspection.NoChange, store.CommitPreparedBatch(batch));
        Assert.False(File.Exists(store.Path)); Assert.Empty(Directory.GetFiles(temp.Path, "post-bookmarks*"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LegacyEmptyOrExistingOnlyRetainsExactBytesAndRevision(bool existingOnly)
    {
        using TempDirectory temp = new(); using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        var store = new PostBookmarkStore(temp.Path, lease);
        Legacy(store.Path, 8, Entry(note: ""));
        File.AppendAllText(store.Path, " \n"); byte[] before = File.ReadAllBytes(store.Path);
        PrivateStoreBatch batch = existingOnly ? Prepare(store, Entry(note: "")) : Prepare(store);
        Assert.False(batch.HasChanges); Assert.Equal(1, batch.Fence.Before.Schema);
        Assert.Equal(8, batch.Fence.Before.Revision); Assert.Null(batch.Fence.Before.Instance);
        Assert.Equal(Hash(before), batch.Fence.Before.Sha256);
        Assert.True(PrivateStoreBatchFiles.Same(batch.Fence.Before, batch.Fence.ExpectedAfter));
        Assert.Equal(PrivateStoreBatchInspection.NoChange, store.CommitPreparedBatch(batch));
        Assert.Equal(before, File.ReadAllBytes(store.Path));
        Assert.Equal(new[] { store.Path }, Directory.GetFiles(temp.Path, "post-bookmarks*"));
    }

    [Fact]
    public void PrepareIsDeterministicAdditiveOwnedAndDoesNotPublish()
    {
        using TempDirectory temp = new(); using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        var store = new PostBookmarkStore(temp.Path, lease);
        Legacy(store.Path, 8, Entry("kept-z", OldNote), Entry("kept-a", ""));
        byte[] before = File.ReadAllBytes(store.Path);
        PostBookmark[] input = [Entry("z"), Entry("a", " \n\t🙂é\u0301 "), Entry("kept-z", OldNote)];
        PrivateStoreBatch first = Prepare(store, input);
        PrivateStoreBatch second = Prepare(store, input.Reverse().ToArray());
        Assert.False(first.IsOwnedBy(store)); Assert.False(first.IsOwnedBy(lease));
        Assert.Equal(first.CopyAfterBytes(), second.CopyAfterBytes());
        Assert.Equal(before, File.ReadAllBytes(store.Path)); Assert.Equal(2, first.Additions);
        Assert.Equal(9, first.Fence.ExpectedAfter.Revision); Assert.Equal(2, first.Fence.ExpectedAfter.Schema);
        Assert.Equal(Operation, first.Fence.ExpectedAfter.Instance);
        Assert.Null(first.Fence.BeforeBackup); Assert.Equal(Hash(first.CopyAfterBytes()), first.Fence.ExpectedAfter.Sha256);
        Assert.Equal(first.CopyAfterBytes().Length, first.Fence.ExpectedAfter.ByteLength);
        input[0] = Entry("changed", "not frozen");
        byte[] scratch = first.CopyAfterBytes(); scratch[0] = 0;
        Assert.Equal(second.CopyAfterBytes(), first.CopyAfterBytes());
        using JsonDocument json = JsonDocument.Parse(first.CopyAfterBytes());
        JsonElement root = json.RootElement;
        Assert.Equal(2, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(Operation, root.GetProperty("instanceId").GetGuid());
        JsonElement marker = root.GetProperty("operationMarker");
        Assert.Equal(Operation, marker.GetProperty("OperationId").GetGuid());
        Assert.Equal(PlanHash, marker.GetProperty("ContentPlanSha256").GetString());
        Assert.Equal((int)NativePrivateApplyStoreKind.PostBookmarksAndNotes, marker.GetProperty("StoreKind").GetInt32());
        Assert.Equal(3, marker.EnumerateObject().Count());
        Assert.DoesNotContain("RequestSha", Encoding.UTF8.GetString(first.CopyAfterBytes()));
        Assert.Equal(new[] { "kept-z", "kept-a", "a", "z" }, root.GetProperty("bookmarks").EnumerateArray()
            .Select(e => e.GetProperty("post").GetString()));
        Assert.Equal(PrivateStoreBatchInspection.ExactBefore, store.InspectPreparedBatch(first));
        Assert.Equal(PrivateStoreBatchInspection.ExactAfterWithMarker, store.CommitPreparedBatch(first));
        Assert.Equal(first.CopyAfterBytes(), File.ReadAllBytes(store.Path));
        Assert.Equal(OldNote, store.Read().Bookmarks[0].Note);
        Assert.Equal(" \n\t🙂é\u0301 ", store.Read().Bookmarks[2].Note);
        Assert.Equal(new[] { store.Path }, Directory.GetFiles(temp.Path, "post-bookmarks*"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \t\n ")]
    [InlineData("🙂é\u0301\nsecond line\t")]
    public void MissingStorePublishesExactLfNotesAndCanBeReadPassively(string note)
    {
        using TempDirectory temp = new(); using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        var store = new PostBookmarkStore(temp.Path, lease);
        PrivateStoreBatch batch = Prepare(store, Entry(note: note));
        Assert.Equal(1, batch.Fence.ExpectedAfter.Revision);
        Assert.Equal(PrivateStoreBatchInspection.ExactAfterWithMarker, store.CommitPreparedBatch(batch));
        Assert.Equal(note, Assert.Single(PostBookmarkStore.ReadOnly(temp.Path, lease).Bookmarks).Note);
        var snapshot = PostBookmarkStore.ReadTransferSnapshot(temp.Path, lease);
        Assert.Equal(note, Assert.Single(snapshot.Bookmarks).Note);
        Assert.True(PostBookmarkStore.IsTransferSnapshotCurrent(temp.Path, lease, snapshot));
        Assert.Equal(Hash(File.ReadAllBytes(store.Path)).ToUpperInvariant(), snapshot.Fingerprint);
        byte[] after = File.ReadAllBytes(store.Path);
        Assert.Equal(PrivateStoreBatchInspection.ExactAfterWithMarker, store.CommitPreparedBatch(batch));
        Assert.Equal(after, File.ReadAllBytes(store.Path)); Assert.Single(Directory.GetFiles(temp.Path, "post-bookmarks*"));
    }

    [Theory]
    [InlineData("", IncomingNote)]
    [InlineData(OldNote, "")]
    [InlineData(OldNote, IncomingNote)]
    [InlineData("é", "e\u0301")]
    public void DifferentSameKeyIsOpaqueConflictNeverLww(string oldNote, string incoming)
    {
        using TempDirectory temp = new(); using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        var store = new PostBookmarkStore(temp.Path, lease); Legacy(store.Path, 2, Entry(note: oldNote));
        byte[] before = File.ReadAllBytes(store.Path);
        Opaque(Assert.Throws<InvalidDataException>(() => Prepare(store, Entry("safe"), Entry(note: incoming))), temp.Path);
        Assert.Equal(before, File.ReadAllBytes(store.Path)); Assert.Single(store.Read().Bookmarks);
        Assert.Single(Directory.GetFiles(temp.Path, "post-bookmarks*"));
    }

    [Theory]
    [InlineData("a\rb")]
    [InlineData("a\r\nb")]
    [InlineData("a\0b")]
    public void IncomingInvalidOrNonLfNoteIsRejectedRatherThanRepaired(string note)
    {
        using TempDirectory temp = new(); using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        var store = new PostBookmarkStore(temp.Path, lease);
        Opaque(Assert.Throws<InvalidDataException>(() => Prepare(store, Entry(note: note))), temp.Path);
        Assert.False(File.Exists(store.Path)); Assert.Empty(Directory.GetFiles(temp.Path, "post-bookmarks*"));
    }

    [Fact]
    public void InvalidInputShapesKeysDuplicatesAndLongNotesFailWithoutWrites()
    {
        using TempDirectory temp = new(); using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        var store = new PostBookmarkStore(temp.Path, lease);
        PostBookmark[][] invalid =
        [
            [Entry(), Entry()], [null!], [new(null!, "")], [Entry() with { Note = null! }],
            [new(new(new("", "b", "10"), "11"), "")],
            [new(new(new("synthetic", "b", "10"), "\uD800"), "")],
            [Entry(note: "\uD800")],
            [Entry(note: new string('a', PostBookmarkStore.MaximumNoteCodepoints + 1))]
        ];
        foreach (PostBookmark[] values in invalid)
            Opaque(Assert.Throws<InvalidDataException>(() => Prepare(store, values)), temp.Path);
        Assert.Throws<InvalidDataException>(() => store.PrepareAdditiveBatch(Operation, PlanHash, null!));
        Assert.Throws<InvalidDataException>(() => Prepare(store, new PostBookmark[PostBookmarkStore.MaximumEntries + 1]));
        Assert.False(File.Exists(store.Path));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-hash")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public void InvalidMarkerIsRejectedEvenForEmptyBatch(string hash)
    {
        using TempDirectory temp = new(); using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        var store = new PostBookmarkStore(temp.Path, lease);
        Assert.Throws<InvalidDataException>(() => store.PrepareAdditiveBatch(Operation, hash, []));
        Assert.Throws<InvalidDataException>(() => store.PrepareAdditiveBatch(Guid.Empty, PlanHash, []));
        Assert.False(File.Exists(store.Path));
    }

    [Fact]
    public void OwnerCannotBeForgedFromStoreLeaseOrAnotherStoreEvenOnSameProfile()
    {
        using TempDirectory temp = new(); using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        var store = new PostBookmarkStore(temp.Path, lease); var other = new PostBookmarkStore(temp.Path, lease);
        PrivateStoreBatch valid = Prepare(store, Entry());
        foreach (object publicOwner in new object[] { store, lease, new object() })
        {
            PrivateStoreBatch forged = new(publicOwner, valid.Marker, valid.Fence, valid.CopyAfterBytes(), valid.Additions);
            Opaque(Assert.Throws<InvalidDataException>(() => store.CommitPreparedBatch(forged)), temp.Path);
            Assert.Throws<InvalidDataException>(() => store.InspectPreparedBatch(forged));
        }
        Assert.Throws<InvalidDataException>(() => other.CommitPreparedBatch(valid));
        Assert.Throws<InvalidDataException>(() => other.InspectPreparedBatch(valid));
        Assert.Throws<InvalidDataException>(() => store.CommitPreparedBatch(null!));
        Assert.Throws<InvalidDataException>(() => store.InspectPreparedBatch(null!));
        Assert.False(File.Exists(store.Path));
    }

    [Fact]
    public void RawByteDriftAtSameRevisionRejectsBothChangedAndNoopBatch()
    {
        using TempDirectory temp = new(); using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        var store = new PostBookmarkStore(temp.Path, lease); Legacy(store.Path, 4, Entry(note: OldNote));
        PrivateStoreBatch changed = Prepare(store, Entry("added")), noop = Prepare(store, Entry(note: OldNote));
        File.AppendAllText(store.Path, " "); byte[] drifted = File.ReadAllBytes(store.Path);
        Assert.Equal(PrivateStoreBatchInspection.Drifted, store.InspectPreparedBatch(changed));
        Assert.Equal(PrivateStoreBatchInspection.Drifted, store.InspectPreparedBatch(noop));
        Assert.Throws<InvalidDataException>(() => store.CommitPreparedBatch(changed));
        Assert.Throws<InvalidDataException>(() => store.CommitPreparedBatch(noop));
        Assert.Equal(drifted, File.ReadAllBytes(store.Path));
    }

    [Fact]
    public void OrdinaryAbaOrMissingCreateAfterPrepareCannotPublishOldBatch()
    {
        using TempDirectory temp = new(); using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        var store = new PostBookmarkStore(temp.Path, lease); PrivateStoreBatch missing = Prepare(store, Entry());
        store.Upsert(Key("other"), OldNote, 0, false);
        Assert.Throws<InvalidDataException>(() => store.CommitPreparedBatch(missing));
        PrivateStoreBatch beforeAba = Prepare(store, Entry());
        store.Remove(Key("other"), 1); store.Upsert(Key("other"), OldNote, 2, false);
        Assert.Equal(PrivateStoreBatchInspection.Drifted, store.InspectPreparedBatch(beforeAba));
        Assert.Throws<InvalidDataException>(() => store.CommitPreparedBatch(beforeAba));
        Assert.Equal(OldNote, Assert.Single(store.Read().Bookmarks).Note);
    }

    [Theory]
    [InlineData("operation")]
    [InlineData("plan")]
    [InlineData("instance")]
    [InlineData("revision")]
    public void ExactAfterRequiresEntireImageAndExactMarker(string field)
    {
        using TempDirectory temp = new(); using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        var store = new PostBookmarkStore(temp.Path, lease); PrivateStoreBatch batch = Prepare(store, Entry());
        Assert.Equal(PrivateStoreBatchInspection.ExactAfterWithMarker, store.CommitPreparedBatch(batch));
        JsonObject root = JsonNode.Parse(File.ReadAllBytes(store.Path))!.AsObject();
        switch (field)
        {
            case "operation": root["operationMarker"]!["OperationId"] = OtherOperation.ToString(); break;
            case "plan": root["operationMarker"]!["ContentPlanSha256"] = new string('b', 64); break;
            case "instance": root["instanceId"] = OtherOperation.ToString(); break;
            case "revision": root["revision"] = 2; break;
        }
        File.WriteAllText(store.Path, root.ToJsonString()); byte[] drifted = File.ReadAllBytes(store.Path);
        Assert.Equal(PrivateStoreBatchInspection.Drifted, store.InspectPreparedBatch(batch));
        Assert.Throws<InvalidDataException>(() => store.CommitPreparedBatch(batch));
        Assert.Equal(drifted, File.ReadAllBytes(store.Path));
    }

    [Theory]
    [InlineData("instance-missing")]
    [InlineData("instance-empty")]
    [InlineData("instance-null")]
    [InlineData("marker-missing")]
    [InlineData("marker-null")]
    [InlineData("wrong-kind")]
    [InlineData("final-hash")]
    [InlineData("note-cr")]
    [InlineData("zero-revision")]
    public void InvalidSchema2IsUnreadableAndCannotBeReplaced(string defect)
    {
        using TempDirectory temp = new(); using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        var store = new PostBookmarkStore(temp.Path, lease); PrivateStoreBatch batch = Prepare(store, Entry());
        JsonObject root = JsonNode.Parse(batch.CopyAfterBytes())!.AsObject();
        switch (defect)
        {
            case "instance-missing": root.Remove("instanceId"); break;
            case "instance-empty": root["instanceId"] = Guid.Empty.ToString(); break;
            case "instance-null": root["instanceId"] = null; break;
            case "marker-missing": root.Remove("operationMarker"); break;
            case "marker-null": root["operationMarker"] = null; break;
            case "wrong-kind": root["operationMarker"]!["StoreKind"] = (int)NativePrivateApplyStoreKind.ThreadFavorites; break;
            case "final-hash": root["operationMarker"]!.AsObject().Remove("ContentPlanSha256");
                root["operationMarker"]!["RequestSha256"] = PlanHash; break;
            case "note-cr": root["bookmarks"]![0]!["note"] = "a\r\nb"; break;
            case "zero-revision": root["revision"] = 0; break;
        }
        File.WriteAllText(store.Path, root.ToJsonString()); byte[] before = File.ReadAllBytes(store.Path);
        Assert.Equal(PrivateStoreBatchInspection.Unreadable, store.InspectPreparedBatch(batch));
        Opaque(Assert.Throws<InvalidDataException>(() => store.Read()), temp.Path);
        Assert.Throws<InvalidDataException>(() => store.CommitPreparedBatch(batch));
        Assert.Throws<InvalidDataException>(() => store.Upsert(Key(), "", 1, true));
        Assert.Equal(before, File.ReadAllBytes(store.Path));
    }

    [Fact]
    public void MarkerBlocksSameValueUpsertRemoveAndNewBatchAcrossStoreInstancesAndRestart()
    {
        using TempDirectory temp = new();
        byte[] published;
        using (ProfileLease lease = ProfileLease.Acquire(temp.Path))
        {
            var store = new PostBookmarkStore(temp.Path, lease); PrivateStoreBatch batch = Prepare(store, Entry());
            store.CommitPreparedBatch(batch); published = File.ReadAllBytes(store.Path);
            var other = new PostBookmarkStore(temp.Path, lease);
            Assert.Throws<InvalidDataException>(() => other.Upsert(Key(), IncomingNote, 1, true));
            Assert.Throws<InvalidDataException>(() => other.Upsert(Key("new"), "", 1, false));
            Assert.Throws<InvalidDataException>(() => other.Remove(Key(), 1));
            Assert.Throws<InvalidDataException>(() => Prepare(other));
            Assert.Throws<InvalidDataException>(() => Prepare(other, Entry()));
        }
        using (ProfileLease lease = ProfileLease.Acquire(temp.Path))
        {
            var restarted = new PostBookmarkStore(temp.Path, lease);
            Assert.Equal(IncomingNote, Assert.Single(restarted.Read().Bookmarks).Note);
            Assert.Throws<InvalidDataException>(() => restarted.Remove(Key(), 1));
            Assert.Throws<InvalidDataException>(() => restarted.Upsert(Key(), IncomingNote, 1, true));
            Assert.Equal(published, File.ReadAllBytes(restarted.Path));
            Assert.Single(Directory.GetFiles(temp.Path, "post-bookmarks*"));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConstructorPreservesLegacyTempsWhenPrimaryIsMarkedOrUncertain(bool corrupt)
    {
        using TempDirectory temp = new(); using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        var store = new PostBookmarkStore(temp.Path, lease); PrivateStoreBatch batch = Prepare(store, Entry());
        if (corrupt) File.WriteAllText(store.Path, "{ SYNTHETIC INVALID PRIMARY");
        else store.CommitPreparedBatch(batch);
        string pending = store.Path + ".pending-" + Guid.NewGuid().ToString("N"); File.WriteAllText(pending, OldNote);
        byte[] primary = File.ReadAllBytes(store.Path);
        _ = new PostBookmarkStore(temp.Path, lease);
        Assert.Equal(primary, File.ReadAllBytes(store.Path)); Assert.Equal(OldNote, File.ReadAllText(pending));
    }

    [Fact]
    public void ConstructorWithoutCleanupDoesNotReadUnrelatedInvalidPrimaryAndLegacyCleanupStillWorks()
    {
        using TempDirectory temp = new(); using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        var store = new PostBookmarkStore(temp.Path, lease);
        File.WriteAllText(store.Path, "{ invalid"); string unknown = store.Path + ".pending-user-work";
        File.WriteAllText(unknown, "keep");
        _ = new PostBookmarkStore(temp.Path, lease); // Invalid primary is not constructor admission.
        Assert.Throws<InvalidDataException>(() => store.Read());
        File.Delete(store.Path);
        string exact = store.Path + ".pending-" + Guid.NewGuid().ToString("N"); File.WriteAllText(exact, "old ordinary temp");
        _ = new PostBookmarkStore(temp.Path, lease);
        Assert.False(File.Exists(exact)); Assert.Equal("keep", File.ReadAllText(unknown));
    }

    [Fact]
    public void InterruptedPrivatePendingBlocksOrdinaryWritesAndConstructorCleanupWithoutRestoringNotes()
    {
        using TempDirectory temp = new(); using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        var store = new PostBookmarkStore(temp.Path, lease);
        PrivateStoreBatch batch = Prepare(store, Entry());
        string pending = Path.Combine(temp.Path, "post-bookmarks.json.private-pending-" + Operation.ToString("N"));
        File.WriteAllBytes(pending, batch.CopyAfterBytes());
        string ordinary = store.Path + ".pending-" + Guid.NewGuid().ToString("N"); File.WriteAllText(ordinary, OldNote);
        var next = new PostBookmarkStore(temp.Path, lease);
        Assert.Throws<InvalidDataException>(() => next.Upsert(Key(), "", 0, false));
        Assert.Throws<InvalidDataException>(() => Prepare(next, Entry()));
        Assert.Throws<InvalidDataException>(() => store.CommitPreparedBatch(batch));
        Assert.True(File.Exists(ordinary)); Assert.Equal(batch.CopyAfterBytes(), File.ReadAllBytes(pending));
        Assert.False(File.Exists(store.Path)); Assert.Empty(next.Read().Bookmarks);
    }

    [Fact]
    public void LegacyReadMayNormalizeCrButBatchNeverRewritesSuchTargetNotes()
    {
        using TempDirectory temp = new(); using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        var store = new PostBookmarkStore(temp.Path, lease); Legacy(store.Path, 3, Entry(note: "a\r\nb"));
        byte[] raw = File.ReadAllBytes(store.Path);
        Assert.Equal("a\nb", Assert.Single(store.Read().Bookmarks).Note);
        Assert.Throws<InvalidDataException>(() => Prepare(store, Entry("new")));
        Assert.Equal(raw, File.ReadAllBytes(store.Path));
    }

    [Fact]
    public void CancellationBeforePrepareOrCommitNeverCreatesTempsOrModifiesNotes()
    {
        using TempDirectory temp = new(); using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        var store = new PostBookmarkStore(temp.Path, lease); Legacy(store.Path, 2, Entry(note: OldNote));
        byte[] before = File.ReadAllBytes(store.Path); PrivateStoreBatch batch = Prepare(store, Entry("new"));
        using CancellationTokenSource cancel = new(); cancel.Cancel();
        Assert.Throws<OperationCanceledException>(() => store.PrepareAdditiveBatch(Operation, PlanHash, [Entry()], cancel.Token));
        Assert.Throws<OperationCanceledException>(() => store.CommitPreparedBatch(batch, cancel.Token));
        Assert.Equal(PrivateStoreBatchInspection.ExactBefore, store.InspectPreparedBatch(batch));
        Assert.Equal(before, File.ReadAllBytes(store.Path)); Assert.Single(Directory.GetFiles(temp.Path, "post-bookmarks*"));
        store.CommitPreparedBatch(batch);
        Assert.Throws<OperationCanceledException>(() => store.CommitPreparedBatch(batch, cancel.Token));
        Assert.Equal(PrivateStoreBatchInspection.ExactAfterWithMarker, store.InspectPreparedBatch(batch));
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("oversize")]
    [InlineData("directory")]
    public void InspectionFailsClosedOnUnreadablePrimaryWithoutTouchingIt(string defect)
    {
        using TempDirectory temp = new(); using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        var store = new PostBookmarkStore(temp.Path, lease); PrivateStoreBatch batch = Prepare(store, Entry());
        if (defect == "directory") Directory.CreateDirectory(store.Path);
        else if (defect == "oversize")
        {
            using FileStream file = File.Create(store.Path); file.SetLength(PostBookmarkStore.MaximumDocumentBytes + 1L);
        }
        else File.WriteAllText(store.Path, "{ " + OldNote);
        Assert.Equal(PrivateStoreBatchInspection.Unreadable, store.InspectPreparedBatch(batch));
        Opaque(Assert.Throws<InvalidDataException>(() => store.CommitPreparedBatch(batch)), temp.Path);
        Assert.Empty(Directory.GetFiles(temp.Path, "*.private-pending-*"));
        if (defect == "directory") Assert.True(Directory.Exists(store.Path));
    }

    [Fact]
    public void NoBookmarkBackupIsReadWrittenRotatedOrUsedForRecovery()
    {
        using TempDirectory temp = new(); using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        var store = new PostBookmarkStore(temp.Path, lease);
        string backup = store.Path + ".bak"; File.WriteAllText(backup, OldNote + " not JSON");
        PrivateStoreBatch batch = Prepare(store, Entry()); Assert.Null(batch.Fence.BeforeBackup);
        File.AppendAllText(backup, " externally changed"); string unrelated = File.ReadAllText(backup);
        Assert.Equal(PrivateStoreBatchInspection.ExactBefore, store.InspectPreparedBatch(batch));
        store.CommitPreparedBatch(batch);
        Assert.Equal(unrelated, File.ReadAllText(backup)); Assert.Equal(2, Directory.GetFiles(temp.Path, "post-bookmarks*").Length);
        Assert.False(File.Exists(store.Path + ".corrupt"));
    }

    [Fact]
    public void RevisionOverflowAndCombinedEntryCapRejectBeforeWriting()
    {
        using TempDirectory temp = new(); using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        var store = new PostBookmarkStore(temp.Path, lease); Legacy(store.Path, long.MaxValue);
        byte[] maxRevision = File.ReadAllBytes(store.Path);
        Assert.Throws<InvalidDataException>(() => Prepare(store, Entry())); Assert.Equal(maxRevision, File.ReadAllBytes(store.Path));
        Legacy(store.Path, 4, Enumerable.Range(0, PostBookmarkStore.MaximumEntries).Select(i => Entry(i.ToString(), "")).ToArray());
        byte[] maxEntries = File.ReadAllBytes(store.Path);
        Assert.Throws<InvalidDataException>(() => Prepare(store, Entry("new", "")));
        Assert.False(Prepare(store, Entry("0", "")).HasChanges);
        Assert.Equal(maxEntries, File.ReadAllBytes(store.Path));
    }

    [Fact]
    public void AggregateIncomingUtf8BudgetAndEscapedOutputBudgetRejectWithoutWrites()
    {
        using TempDirectory temp = new(); using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        var store = new PostBookmarkStore(temp.Path, lease);
        string emoji = string.Concat(Enumerable.Repeat("🙂", PostBookmarkStore.MaximumNoteCodepoints));
        PostBookmark[] largeInput = Enumerable.Range(0, 1050).Select(i => Entry(i.ToString(), emoji)).ToArray();
        Assert.Throws<InvalidDataException>(() => Prepare(store, largeInput));
        Assert.False(File.Exists(store.Path));
        string escaped = new('\u00A0', PostBookmarkStore.MaximumNoteCodepoints);
        Legacy(store.Path, 4, Enumerable.Range(0, 698).Select(i => Entry(i.ToString(), escaped)).ToArray());
        byte[] before = File.ReadAllBytes(store.Path); Assert.True(before.Length < PostBookmarkStore.MaximumDocumentBytes);
        Assert.Throws<InvalidDataException>(() => Prepare(store, Entry("new", escaped)));
        Assert.Equal(before, File.ReadAllBytes(store.Path)); Assert.Single(Directory.GetFiles(temp.Path, "post-bookmarks*"));
    }

    [Fact]
    public void FullComponentsRemainDistinctWithoutDelimiterOrCaseFolding()
    {
        using TempDirectory temp = new(); using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        var store = new PostBookmarkStore(temp.Path, lease);
        PostBookmark[] entries =
        [
            new(Key("11", "a/b", "c", "d"), "1"), new(Key("11", "a", "b/c", "d"), "2"),
            new(Key("11", "A", "b", "d"), "3"), new(Key("11", "a", "b", "d"), "4")
        ];
        store.CommitPreparedBatch(Prepare(store, entries));
        Assert.Equal(4, store.Read().Bookmarks.Count);
        Assert.True(entries.Select(e => e.Key).ToHashSet().SetEquals(store.Read().Bookmarks.Select(e => e.Key)));
    }

    [Fact]
    public void ReleasedOwnerCannotPrepareInspectOrCommit()
    {
        using TempDirectory temp = new(); ProfileLease lease = ProfileLease.Acquire(temp.Path);
        var store = new PostBookmarkStore(temp.Path, lease); PrivateStoreBatch batch = Prepare(store, Entry()); lease.Dispose();
        Assert.Throws<ObjectDisposedException>(() => Prepare(store, Entry()));
        Assert.Throws<ObjectDisposedException>(() => store.InspectPreparedBatch(batch));
        Assert.Throws<ObjectDisposedException>(() => store.CommitPreparedBatch(batch));
        Assert.False(File.Exists(store.Path));
    }
}
