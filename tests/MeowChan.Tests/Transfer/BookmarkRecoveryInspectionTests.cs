using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowChan.Persistence;
using MeowChan.Transfer;

namespace MeowChan.Tests.Transfer;

// Synthetic, isolated profiles only. The inspection assertions never construct a
// store. Real stores below are solely the producers of actual P06 batch fences.
public sealed class BookmarkRecoveryInspectionTests
{
    private const string FileName = "post-bookmarks.json";
    private const string PlanHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string OtherHash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private static readonly Guid Operation = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid OtherOperation = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
    private const NativePrivateApplyStoreKind Kind = NativePrivateApplyStoreKind.PostBookmarksAndNotes;

    private static string Primary(string profile) => Path.Combine(profile, FileName);
    private static PostBookmark Entry(string post = "11", string note = "synthetic note")
        => new(new(new("synthetic", "b", "10"), post), note);
    private static PrivateStoreBatch Prepare(PostBookmarkStore store, params PostBookmark[] entries)
        => store.PrepareAdditiveBatch(Operation, PlanHash, entries);
    private static byte[] LegacyBytes(long revision = 3, params PostBookmark[] entries)
        => JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1, revision,
            bookmarks = entries.Select(e => new
            {
                source = e.Key.Thread.Source, board = e.Key.Thread.Board, thread = e.Key.Thread.Thread,
                post = e.Key.Post, note = e.Note
            })
        });
    private static string[] Inventory(string profile)
        => Directory.GetFileSystemEntries(profile).Select(path => Path.GetFileName(path)!)
            .OrderBy(name => name, StringComparer.Ordinal).ToArray();

    // Capture only expressly supplied regular fixture files, never a lock,
    // unselected store, symlink, special file or bookmark backup payload.
    private static void AssertInspection(ProfileLease lease, NativePrivateApplyStoreFence fence,
        PrivateStoreBatchInspection expected, params string[] regularNames)
    {
        string[] listing = Inventory(lease.ProfileDirectory);
        byte[][] bytes = regularNames.Select(name => File.ReadAllBytes(Path.Combine(lease.ProfileDirectory, name))).ToArray();
        for (int attempt = 0; attempt < 2; attempt++)
        {
            Assert.Equal(expected, PostBookmarkStore.InspectRecoveryFence(lease, fence, Operation, PlanHash));
            Assert.Equal(listing, Inventory(lease.ProfileDirectory));
            for (int i = 0; i < regularNames.Length; i++)
                Assert.Equal(bytes[i], File.ReadAllBytes(Path.Combine(lease.ProfileDirectory, regularNames[i])));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingBaselineAndSelectedEmptyNoopSurviveNewLeaseWithoutCreatingAFile(bool change)
    {
        using TempDirectory temp = new();
        NativePrivateApplyStoreFence fence;
        using (ProfileLease lease = ProfileLease.Acquire(temp.Path))
        {
            var producer = new PostBookmarkStore(temp.Path, lease);
            PrivateStoreBatch batch = change ? Prepare(producer, Entry(note: "")) : Prepare(producer);
            fence = batch.Fence;
            Assert.Null(fence.BeforeBackup);
            Assert.False(fence.Before.Exists);
        }
        using ProfileLease reopened = ProfileLease.Acquire(temp.Path);
        AssertInspection(reopened, fence, change ? PrivateStoreBatchInspection.ExactBefore : PrivateStoreBatchInspection.NoChange);
        Assert.Empty(Directory.GetFileSystemEntries(temp.Path, "post-bookmarks*"));
    }

    [Theory]
    [InlineData(false, "")]
    [InlineData(true, "")]
    [InlineData(false, " \t\n ")]
    [InlineData(true, "🙂é\u0301\nsecond line\t")]
    public void RealPrepareCommitAndStaticRestartVerifyExactAfterAndPreserveEmptyNotes(bool legacy, string note)
    {
        using TempDirectory temp = new();
        NativePrivateApplyStoreFence fence;
        byte[] published;
        using (ProfileLease lease = ProfileLease.Acquire(temp.Path))
        {
            if (legacy) File.WriteAllBytes(Primary(temp.Path), LegacyBytes(8, Entry("kept", "")));
            var producer = new PostBookmarkStore(temp.Path, lease);
            PrivateStoreBatch batch = Prepare(producer, Entry(note: note));
            fence = batch.Fence;
            AssertInspection(lease, fence, PrivateStoreBatchInspection.ExactBefore, legacy ? [FileName] : []);
            Assert.Equal(PrivateStoreBatchInspection.ExactAfterWithMarker, producer.CommitPreparedBatch(batch));
            published = File.ReadAllBytes(Primary(temp.Path));
            Assert.Equal(batch.CopyAfterBytes(), published);
        }
        // No old lease, store or prepared-batch owner is supplied to the reader.
        using ProfileLease reopened = ProfileLease.Acquire(temp.Path);
        AssertInspection(reopened, fence, PrivateStoreBatchInspection.ExactAfterWithMarker, FileName);
        Assert.Equal(published, File.ReadAllBytes(Primary(temp.Path)));
        using JsonDocument document = JsonDocument.Parse(published);
        JsonElement[] values = document.RootElement.GetProperty("bookmarks").EnumerateArray().ToArray();
        Assert.Equal(note, values.Single(e => e.GetProperty("post").GetString() == "11").GetProperty("note").GetString());
        if (legacy) Assert.Equal("", values.Single(e => e.GetProperty("post").GetString() == "kept").GetProperty("note").GetString());
        Assert.Null(fence.BeforeBackup);
        Assert.Equal(new[] { FileName }, Inventory(temp.Path).Where(name => name.StartsWith("post-bookmarks", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LegacyEmptyNoteAndExactWhitespaceAreNotMigratedOnBeforeOrNoopInspection(bool change)
    {
        using TempDirectory temp = new();
        NativePrivateApplyStoreFence fence;
        byte[] original = LegacyBytes(8, Entry(note: "")).Concat(Encoding.UTF8.GetBytes(" \n\t")).ToArray();
        using (ProfileLease lease = ProfileLease.Acquire(temp.Path))
        {
            File.WriteAllBytes(Primary(temp.Path), original);
            var producer = new PostBookmarkStore(temp.Path, lease);
            fence = (change ? Prepare(producer, Entry("new", "")) : Prepare(producer, Entry(note: ""))).Fence;
        }
        using ProfileLease reopened = ProfileLease.Acquire(temp.Path);
        AssertInspection(reopened, fence, change ? PrivateStoreBatchInspection.ExactBefore : PrivateStoreBatchInspection.NoChange, FileName);
        Assert.Equal(original, File.ReadAllBytes(Primary(temp.Path)));
        Assert.Equal(1, fence.Before.Schema);
        Assert.Null(fence.Before.Instance);
        Assert.DoesNotContain(Inventory(temp.Path), name => name.StartsWith(FileName + ".", StringComparison.Ordinal));
    }

    [Fact]
    public void BookmarkCompanionsUseExactOrdinalPrefixAndExcludeTheBaseFile()
    {
        using TempDirectory temp = new();
        File.WriteAllBytes(Primary(temp.Path), [1]);
        // A name-only blocker exercises .bak detection without note-backup IO.
        Directory.CreateDirectory(Primary(temp.Path) + ".bak");
        File.WriteAllBytes(Primary(temp.Path) + ".pending-X", [3]);

        string[] companions = Inventory(temp.Path)
            .Where(name => name.StartsWith(FileName + ".", StringComparison.Ordinal))
            .ToArray();

        Assert.Equal(new[] { FileName + ".bak", FileName + ".pending-X" }, companions);
        Assert.DoesNotContain(FileName, companions);
    }

    [Fact]
    public void ExactBeforeAfterAnUnobservedByteRoundtripIsOnlyAPointSampleNotHistoryProof()
    {
        using TempDirectory temp = new();
        NativePrivateApplyStoreFence fence;
        byte[] before = LegacyBytes(3, Entry(note: ""));
        using (ProfileLease lease = ProfileLease.Acquire(temp.Path))
        {
            File.WriteAllBytes(Primary(temp.Path), before);
            var producer = new PostBookmarkStore(temp.Path, lease);
            fence = Prepare(producer, Entry("new", "")).Fence;
        }
        File.WriteAllBytes(Primary(temp.Path), LegacyBytes(4, Entry(note: "synthetic intervening value")));
        File.WriteAllBytes(Primary(temp.Path), before);
        using ProfileLease reopened = ProfileLease.Acquire(temp.Path);
        // Exact bytes do not prove that nothing ever happened while the lease was absent.
        AssertInspection(reopened, fence, PrivateStoreBatchInspection.ExactBefore, FileName);
        Assert.Equal(before, File.ReadAllBytes(Primary(temp.Path)));
    }

    [Theory]
    [InlineData("before")]
    [InlineData("noop")]
    [InlineData("after")]
    public void SameRevisionWhitespaceDriftIsNotAValidFence(string phase)
    {
        using TempDirectory temp = new(); using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        File.WriteAllBytes(Primary(temp.Path), LegacyBytes(3, Entry(note: "")));
        var producer = new PostBookmarkStore(temp.Path, lease);
        PrivateStoreBatch batch = phase == "noop" ? Prepare(producer) : Prepare(producer, Entry("new", ""));
        if (phase == "after") producer.CommitPreparedBatch(batch);
        File.AppendAllText(Primary(temp.Path), " ");
        AssertInspection(lease, batch.Fence, PrivateStoreBatchInspection.Drifted, FileName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AppearanceOrDisappearanceAfterPreparationIsDrift(bool disappear)
    {
        using TempDirectory temp = new(); using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        if (disappear) File.WriteAllBytes(Primary(temp.Path), LegacyBytes(3, Entry()));
        var producer = new PostBookmarkStore(temp.Path, lease);
        PrivateStoreBatch batch = Prepare(producer, Entry("new", ""));
        if (disappear) File.Delete(Primary(temp.Path));
        else File.WriteAllBytes(Primary(temp.Path), LegacyBytes(0));
        AssertInspection(lease, batch.Fence, PrivateStoreBatchInspection.Drifted, disappear ? [] : [FileName]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExactAfterBytesRequireTheRequestedOperationAndContentMarker(bool wrongContent)
    {
        using TempDirectory temp = new(); using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        var producer = new PostBookmarkStore(temp.Path, lease);
        PrivateStoreBatch batch = Prepare(producer, Entry(note: ""));
        producer.CommitPreparedBatch(batch);
        byte[] before = File.ReadAllBytes(Primary(temp.Path)); string[] listing = Inventory(temp.Path);
        Assert.Equal(PrivateStoreBatchInspection.Drifted, PostBookmarkStore.InspectRecoveryFence(lease,
            batch.Fence, wrongContent ? Operation : OtherOperation, wrongContent ? OtherHash : PlanHash));
        Assert.Equal(before, File.ReadAllBytes(Primary(temp.Path)));
        Assert.Equal(listing, Inventory(temp.Path));
    }

    [Theory]
    [InlineData("operation")]
    [InlineData("content")]
    public void ValidForeignMarkerIsDriftEvenWhenExpectedAfterHashMatchesIt(string defect)
    {
        using TempDirectory temp = new(); using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        var producer = new PostBookmarkStore(temp.Path, lease);
        PrivateStoreBatch batch = Prepare(producer, Entry(note: ""));
        JsonObject root = JsonNode.Parse(batch.CopyAfterBytes())!.AsObject();
        if (defect == "operation") root["operationMarker"]!["OperationId"] = OtherOperation.ToString();
        else root["operationMarker"]!["ContentPlanSha256"] = OtherHash;
        byte[] bytes = Encoding.UTF8.GetBytes(root.ToJsonString());
        File.WriteAllBytes(Primary(temp.Path), bytes);
        var fence = new NativePrivateApplyStoreFence(Kind, batch.Fence.Before,
            PrivateStoreBatchFiles.Image(bytes, 2, Operation, 1));
        AssertInspection(lease, fence, PrivateStoreBatchInspection.Drifted, FileName);
    }

    [Fact]
    public void AValidMarkerCannotBeLaunderedAsAnUnchangedBeforeImage()
    {
        using TempDirectory temp = new(); using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        var producer = new PostBookmarkStore(temp.Path, lease);
        PrivateStoreBatch batch = Prepare(producer, Entry(note: ""));
        producer.CommitPreparedBatch(batch);
        var noop = new NativePrivateApplyStoreFence(Kind, batch.Fence.ExpectedAfter, batch.Fence.ExpectedAfter);
        AssertInspection(lease, noop, PrivateStoreBatchInspection.Drifted, FileName);
    }

    [Theory]
    [InlineData("instance")]
    [InlineData("revision")]
    [InlineData("note")]
    [InlineData("unmarked-legacy")]
    public void MatchingMarkerAloneCannotReplaceTheCompleteAfterImage(string defect)
    {
        using TempDirectory temp = new(); using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        var producer = new PostBookmarkStore(temp.Path, lease);
        PrivateStoreBatch batch = Prepare(producer, Entry(note: ""));
        JsonObject root = JsonNode.Parse(batch.CopyAfterBytes())!.AsObject();
        switch (defect)
        {
            case "instance": root["instanceId"] = OtherOperation.ToString(); break;
            case "revision": root["revision"] = 2; break;
            case "note": root["bookmarks"]![0]!["note"] = "synthetic changed note"; break;
            case "unmarked-legacy": root["schemaVersion"] = 1; root.Remove("instanceId"); root.Remove("operationMarker"); break;
        }
        File.WriteAllText(Primary(temp.Path), root.ToJsonString());
        AssertInspection(lease, batch.Fence, PrivateStoreBatchInspection.Drifted, FileName);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("null")]
    [InlineData("wrong-kind")]
    [InlineData("empty-operation")]
    [InlineData("bad-guid")]
    [InlineData("uppercase-hash")]
    [InlineData("missing-content")]
    [InlineData("foreign-field")]
    [InlineData("duplicate-field")]
    public void MalformedMarkerIsUnreadableEvenWithAnExactExpectedAfterHash(string defect)
    {
        using TempDirectory temp = new(); using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        var producer = new PostBookmarkStore(temp.Path, lease);
        PrivateStoreBatch batch = Prepare(producer, Entry(note: ""));
        JsonObject root = JsonNode.Parse(batch.CopyAfterBytes())!.AsObject();
        JsonObject marker = root["operationMarker"]!.AsObject();
        switch (defect)
        {
            case "missing": root.Remove("operationMarker"); break;
            case "null": root["operationMarker"] = null; break;
            case "wrong-kind": marker["StoreKind"] = (int)NativePrivateApplyStoreKind.ThreadFavorites; break;
            case "empty-operation": marker["OperationId"] = Guid.Empty.ToString(); break;
            case "bad-guid": marker["OperationId"] = "synthetic-invalid-guid"; break;
            case "uppercase-hash": marker["ContentPlanSha256"] = PlanHash.ToUpperInvariant(); break;
            case "missing-content": marker.Remove("ContentPlanSha256"); break;
            case "foreign-field": marker["RequestSha256"] = PlanHash; break;
        }
        string json = root.ToJsonString();
        if (defect == "duplicate-field") json = json.Replace("\"StoreKind\":2", "\"StoreKind\":2,\"StoreKind\":2", StringComparison.Ordinal);
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        File.WriteAllBytes(Primary(temp.Path), bytes);
        var fence = new NativePrivateApplyStoreFence(Kind, batch.Fence.Before,
            PrivateStoreBatchFiles.Image(bytes, 2, Operation, 1));
        AssertInspection(lease, fence, PrivateStoreBatchInspection.Unreadable, FileName);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("json")]
    [InlineData("utf8")]
    [InlineData("unsupported-schema")]
    [InlineData("duplicate-property")]
    [InlineData("duplicate-bookmark")]
    [InlineData("unknown-field")]
    [InlineData("negative-revision")]
    [InlineData("cr-note")]
    [InlineData("null-note")]
    [InlineData("long-note")]
    [InlineData("trailing-json")]
    public void StrictParserRejectsMalformedDocumentsWithoutNormalizingOrRepairing(string defect)
    {
        using TempDirectory temp = new(); using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        JsonObject root = JsonNode.Parse(LegacyBytes(3, Entry(note: "")))!.AsObject();
        switch (defect)
        {
            case "unsupported-schema": root["schemaVersion"] = 99; break;
            case "duplicate-bookmark": root["bookmarks"]!.AsArray().Add(root["bookmarks"]![0]!.DeepClone()); break;
            case "unknown-field": root["syntheticUnknown"] = "not repaired"; break;
            case "negative-revision": root["revision"] = -1; break;
            case "cr-note": root["bookmarks"]![0]!["note"] = "a\r\nb"; break;
            case "null-note": root["bookmarks"]![0]!["note"] = null; break;
            case "long-note": root["bookmarks"]![0]!["note"] = new string('x', PostBookmarkStore.MaximumNoteCodepoints + 1); break;
        }
        string json = root.ToJsonString();
        byte[] bytes = defect switch
        {
            "empty" => [], "json" => Encoding.UTF8.GetBytes("{ synthetic invalid private document"),
            "utf8" => [0xff, 0xfe],
            "duplicate-property" => Encoding.UTF8.GetBytes(json.Replace("\"revision\":3", "\"revision\":3,\"revision\":3", StringComparison.Ordinal)),
            "trailing-json" => Encoding.UTF8.GetBytes(json + " {}"),
            _ => Encoding.UTF8.GetBytes(json)
        };
        File.WriteAllBytes(Primary(temp.Path), bytes);
        // For nonempty malformed bytes, bind their exact hash. Syntax validation
        // must precede successful classification, not just compare the digest.
        NativePrivateApplyStoreImage image = bytes.Length == 0 ? PrivateStoreBatchFiles.Missing()
            : PrivateStoreBatchFiles.Image(bytes, 1, null, 3);
        AssertInspection(lease, new(Kind, image, image), PrivateStoreBatchInspection.Unreadable, FileName);
    }

    [Theory]
    [InlineData("directory")]
    [InlineData("oversize")]
    public void NonRegularAndOversizedPrimaryFailClosedWithoutReplacingEvidence(string defect)
    {
        using TempDirectory temp = new(); using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        var producer = new PostBookmarkStore(temp.Path, lease);
        PrivateStoreBatch batch = Prepare(producer, Entry(note: ""));
        if (defect == "directory") Directory.CreateDirectory(Primary(temp.Path));
        else
        {
            using FileStream file = File.Create(Primary(temp.Path));
            file.SetLength(PostBookmarkStore.MaximumDocumentBytes + 1L);
        }
        AssertInspection(lease, batch.Fence, PrivateStoreBatchInspection.Unreadable);
        if (defect == "directory") Assert.Empty(Directory.GetFileSystemEntries(Primary(temp.Path)));
        else Assert.Equal(PostBookmarkStore.MaximumDocumentBytes + 1L, new FileInfo(Primary(temp.Path)).Length);
        Assert.DoesNotContain(Inventory(temp.Path), name => name.StartsWith(FileName + ".", StringComparison.Ordinal));
    }

    [NativePrivateUnixTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void PrimarySymbolicLinkIncludingDanglingLinkIsUnreadableAndNotFollowed(bool dangling)
    {
        using TempDirectory temp = new(); using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        var producer = new PostBookmarkStore(temp.Path, lease);
        PrivateStoreBatch batch = Prepare(producer, Entry(note: ""));
        string target = Path.Combine(temp.Path, "synthetic-link-target");
        byte[] targetBytes = LegacyBytes(0);
        if (!dangling) File.WriteAllBytes(target, targetBytes);
        File.CreateSymbolicLink(Primary(temp.Path), target);
        AssertInspection(lease, batch.Fence, PrivateStoreBatchInspection.Unreadable);
        Assert.Equal(target, new FileInfo(Primary(temp.Path)).LinkTarget);
        if (!dangling) Assert.Equal(targetBytes, File.ReadAllBytes(target));
        else Assert.False(File.Exists(target));
    }

    [NativePrivateSharingTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void InaccessibleSelectedPrimaryIsUnreadableAndTheSameBytesRemainUsable(bool after)
    {
        using TempDirectory temp = new(); using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        File.WriteAllBytes(Primary(temp.Path), LegacyBytes(3, Entry(note: "")));
        var producer = new PostBookmarkStore(temp.Path, lease);
        PrivateStoreBatch batch = Prepare(producer, Entry("new", ""));
        if (after) producer.CommitPreparedBatch(batch);
        byte[] bytes = File.ReadAllBytes(Primary(temp.Path));
        string[] listing = Inventory(temp.Path);
        using (FileStream writer = new(Primary(temp.Path), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.ThrowsAny<IOException>(() => { using var control = RegularProfileFile.OpenRead(Primary(temp.Path)); });
            Assert.Equal(PrivateStoreBatchInspection.Unreadable,
                PostBookmarkStore.InspectRecoveryFence(lease, batch.Fence, Operation, PlanHash));
        }
        Assert.Equal(bytes, File.ReadAllBytes(Primary(temp.Path)));
        Assert.Equal(listing, Inventory(temp.Path));
        AssertInspection(lease, batch.Fence, after ? PrivateStoreBatchInspection.ExactAfterWithMarker : PrivateStoreBatchInspection.ExactBefore, FileName);
    }

    [Fact]
    public void DotBookmarkTempIsPendingEvidenceAndStaticInspectionNeverConstructsACleaningStore()
    {
        using TempDirectory temp = new(); using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        var producer = new PostBookmarkStore(temp.Path, lease);
        PrivateStoreBatch batch = Prepare(producer, Entry(note: ""));
        string name = "." + FileName + "." + Operation.ToString("N") + ".tmp";
        byte[] evidence = Encoding.UTF8.GetBytes("synthetic bookmark temp; preserve exactly");
        File.WriteAllBytes(Path.Combine(temp.Path, name), evidence);

        // This is the additional real-temp shape used by the common helper. It
        // is intentionally not opened, parsed, deleted or treated as a backup.
        AssertInspection(lease, batch.Fence, PrivateStoreBatchInspection.Unreadable, name);
        Assert.Equal(evidence, File.ReadAllBytes(Path.Combine(temp.Path, name)));
        Assert.False(File.Exists(Primary(temp.Path)));
    }

    [Theory]
    [InlineData(".pending-11111111222233334444555555555555", false)]
    [InlineData(".pending-not-a-guid", false)]
    [InlineData(".pending-", false)]
    [InlineData(".private-pending-11111111222233334444555555555555", false)]
    [InlineData(".private-pending-unknown", false)]
    [InlineData(".private-pending-", false)]
    [InlineData(".pending-not-a-guid", true)]
    [InlineData(".private-pending-not-a-guid", true)]
    public void EverySelectedPendingNameIsUnreadableAndPreservedIncludingMalformedSuffixes(string suffix, bool directory)
    {
        using TempDirectory temp = new(); using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        File.WriteAllBytes(Primary(temp.Path), LegacyBytes(3, Entry(note: "")));
        var producer = new PostBookmarkStore(temp.Path, lease);
        PrivateStoreBatch batch = Prepare(producer, Entry("new", ""));
        string name = FileName + suffix, path = Path.Combine(temp.Path, name);
        if (directory) Directory.CreateDirectory(path);
        else File.WriteAllBytes(path, batch.CopyAfterBytes());
        AssertInspection(lease, batch.Fence, PrivateStoreBatchInspection.Unreadable, directory ? [FileName] : [FileName, name]);
        if (directory) Assert.Empty(Directory.GetFileSystemEntries(path));
    }

    [NativePrivateUnixTheory]
    [InlineData(".pending-unknown")]
    [InlineData(".private-pending-unknown")]
    public void DanglingPendingLinksArePreservedWithoutOpeningTheirTargets(string suffix)
    {
        using TempDirectory temp = new(); using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        var producer = new PostBookmarkStore(temp.Path, lease);
        PrivateStoreBatch batch = Prepare(producer, Entry(note: ""));
        string pending = Path.Combine(temp.Path, FileName + suffix);
        string target = Path.Combine(temp.Path, "synthetic-nonexistent-target");
        File.CreateSymbolicLink(pending, target);
        AssertInspection(lease, batch.Fence, PrivateStoreBatchInspection.Unreadable);
        Assert.Equal(target, new FileInfo(pending).LinkTarget);
        Assert.False(File.Exists(target));
        Assert.False(File.Exists(Primary(temp.Path)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OrdinaryGuidPendingThatAConstructorWouldDeleteSurvivesStaticRestart(bool primaryExists)
    {
        using TempDirectory temp = new();
        NativePrivateApplyStoreFence fence;
        string name = FileName + ".pending-" + Operation.ToString("N");
        byte[] evidence = Encoding.UTF8.GetBytes("synthetic interrupted ordinary note write");
        using (ProfileLease lease = ProfileLease.Acquire(temp.Path))
        {
            if (primaryExists) File.WriteAllBytes(Primary(temp.Path), LegacyBytes(3, Entry(note: "")));
            var producer = new PostBookmarkStore(temp.Path, lease);
            fence = Prepare(producer, Entry("new", "")).Fence;
            // No private pending and no marker/corrupt primary: this exact ordinary
            // GUID name satisfies the existing constructor's deletion conditions.
            File.WriteAllBytes(Path.Combine(temp.Path, name), evidence);
        }
        using ProfileLease reopened = ProfileLease.Acquire(temp.Path);
        AssertInspection(reopened, fence, PrivateStoreBatchInspection.Unreadable, primaryExists ? [FileName, name] : [name]);
        Assert.Equal(evidence, File.ReadAllBytes(Path.Combine(temp.Path, name)));
        Assert.Equal(primaryExists, File.Exists(Primary(temp.Path)));
    }

    [Fact]
    public void PendingPreventsReadingEvenAnExclusivelyLockedPrimaryAndDoesNotCleanEitherFile()
    {
        using TempDirectory temp = new(); using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        var producer = new PostBookmarkStore(temp.Path, lease);
        PrivateStoreBatch batch = Prepare(producer, Entry(note: ""));
        producer.CommitPreparedBatch(batch);
        string name = FileName + ".private-pending-malformed";
        File.WriteAllBytes(Path.Combine(temp.Path, name), [1, 2, 3]);
        string[] listing = Inventory(temp.Path);
        using (FileStream writer = new(Primary(temp.Path), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.Equal(PrivateStoreBatchInspection.Unreadable,
                PostBookmarkStore.InspectRecoveryFence(lease, batch.Fence, Operation, PlanHash));
        Assert.Equal(batch.CopyAfterBytes(), File.ReadAllBytes(Primary(temp.Path)));
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(Path.Combine(temp.Path, name)));
        Assert.Equal(listing, Inventory(temp.Path));
        AssertInspection(lease, batch.Fence, PrivateStoreBatchInspection.Unreadable, FileName, name);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BookmarkBackupBlockerAndUnselectedStoresAreNotReadOrUsedForRecovery(bool after)
    {
        using TempDirectory temp = new(); using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        var producer = new PostBookmarkStore(temp.Path, lease);
        PrivateStoreBatch batch = Prepare(producer, Entry(note: ""));
        if (after) producer.CommitPreparedBatch(batch);
        // Deliberately unreadable objects, not note backup payloads. No test reads
        // or writes .bak note contents. Even backup presence/drift is irrelevant.
        string backup = Primary(temp.Path) + ".bak";
        Directory.CreateDirectory(backup);
        Directory.CreateDirectory(Path.Combine(temp.Path, "media-favorites.json"));
        Directory.CreateDirectory(Path.Combine(temp.Path, "thread-favorites.json"));
        string unselectedPending = "media-favorites.json.pending-unknown";
        File.WriteAllBytes(Path.Combine(temp.Path, unselectedPending), [7, 8]);
        PrivateStoreBatchInspection expected = after ? PrivateStoreBatchInspection.ExactAfterWithMarker : PrivateStoreBatchInspection.ExactBefore;
        AssertInspection(lease, batch.Fence, expected, after ? [FileName, unselectedPending] : [unselectedPending]);
        Directory.CreateDirectory(Path.Combine(backup, "synthetic-nonpayload-drift"));
        AssertInspection(lease, batch.Fence, expected, after ? [FileName, unselectedPending] : [unselectedPending]);
        Assert.Equal(new[] { "synthetic-nonpayload-drift" }, Inventory(backup));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingOrReplacedLeasedDirectoryCannotMasqueradeAsAnEmptyStore(bool replacement)
    {
        using TempDirectory temp = new();
        string profile = Path.Combine(temp.Path, "profile"), moved = Path.Combine(temp.Path, "original");
        using ProfileLease lease = ProfileLease.Acquire(profile);
        var producer = new PostBookmarkStore(profile, lease);
        PrivateStoreBatch batch = Prepare(producer, Entry(note: ""));
        string[] original = Inventory(profile);
        Directory.Move(profile, moved);
        if (replacement) Directory.CreateDirectory(profile);
        Assert.Equal(PrivateStoreBatchInspection.Unreadable,
            PostBookmarkStore.InspectRecoveryFence(lease, batch.Fence, Operation, PlanHash));
        Assert.Equal(original, Inventory(moved));
        if (replacement) Assert.Empty(Directory.GetFileSystemEntries(profile));
        else Assert.False(Directory.Exists(profile));
    }

    [Theory]
    [InlineData("null-fence")]
    [InlineData("wrong-kind")]
    [InlineData("backup-shape")]
    [InlineData("empty-operation")]
    [InlineData("invalid-hash")]
    [InlineData("null-before")]
    [InlineData("bad-missing-image")]
    public void InvalidArgumentsAreFailuresNotUnreadableDiskResults(string defect)
    {
        using TempDirectory temp = new(); using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        NativePrivateApplyStoreImage missing = PrivateStoreBatchFiles.Missing();
        NativePrivateApplyStoreFence fence = defect switch
        {
            "null-fence" => null!,
            "wrong-kind" => new(NativePrivateApplyStoreKind.ThreadFavorites, missing, missing, missing),
            "backup-shape" => new(Kind, missing, missing, missing),
            "null-before" => new(Kind, null!, missing),
            "bad-missing-image" => new(Kind, new(false, 1, null, 0, 0, null), missing),
            _ => new(Kind, missing, missing)
        };
        string[] before = Inventory(temp.Path);
        InvalidDataException error = Assert.Throws<InvalidDataException>(() => PostBookmarkStore.InspectRecoveryFence(
            lease, fence, defect == "empty-operation" ? Guid.Empty : Operation, defect == "invalid-hash" ? "bad" : PlanHash));
        Assert.Null(error.InnerException);
        Assert.DoesNotContain(temp.Path, error.ToString());
        Assert.Equal(before, Inventory(temp.Path));
    }

    [Fact]
    public void NullOrDisposedLeaseIsAProgrammerFailureAndNeverCreatesStoreFiles()
    {
        using TempDirectory temp = new();
        NativePrivateApplyStoreImage missing = PrivateStoreBatchFiles.Missing();
        var fence = new NativePrivateApplyStoreFence(Kind, missing, missing);
        Assert.Throws<ArgumentNullException>(() => PostBookmarkStore.InspectRecoveryFence(null!, fence, Operation, PlanHash));
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        lease.Dispose();
        Assert.Throws<ObjectDisposedException>(() => PostBookmarkStore.InspectRecoveryFence(lease, fence, Operation, PlanHash));
        Assert.Empty(Directory.GetFileSystemEntries(temp.Path, "post-bookmarks*"));
    }
}
