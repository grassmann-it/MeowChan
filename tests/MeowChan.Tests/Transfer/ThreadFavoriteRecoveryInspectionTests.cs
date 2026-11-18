using System.Text;
using System.Text.Json;
using MeowChan.Favorites;
using MeowChan.Persistence;
using MeowChan.Transfer;

namespace MeowChan.Tests.Transfer;

// Synthetic isolated profiles only. Construction/publication is fixture setup;
// every inspected restart uses only a new lease and copied scalar fences.
public sealed class ThreadFavoriteRecoveryInspectionTests
{
    private const string FileName = "thread-favorites.json";
    private static readonly Guid Operation = Guid.Parse("42424242-1111-2222-3333-444444444444");
    private static readonly Guid OtherOperation = Guid.Parse("53535353-1111-2222-3333-555555555555");
    private static readonly string PlanHash = new('a', 64);
    private static ResourceId Thread(string id) => new("synthetic", "b", id);
    private static string Primary(string root) => Path.Combine(root, FileName);
    private static NativePrivateApplyStoreImage Copy(NativePrivateApplyStoreImage image) =>
        new(image.Exists, image.Schema, image.Instance, image.Revision, image.ByteLength, image.Sha256);
    private static NativePrivateApplyStoreFence Copy(NativePrivateApplyStoreFence fence) =>
        new(fence.Kind, Copy(fence.Before), Copy(fence.ExpectedAfter),
            fence.BeforeBackup is null ? null : Copy(fence.BeforeBackup));
    private static NativePrivateApplyStoreFence MissingFence()
    {
        var missing = PrivateStoreBatchFiles.Missing();
        return new(NativePrivateApplyStoreKind.ThreadFavorites, missing, missing, missing);
    }
    private static ThreadFavoriteStore Seed(string root, ProfileLease lease)
    {
        var store = new ThreadFavoriteStore(root, lease);
        store.Set(Thread("old-a"), true);
        store.Set(Thread("old-b"), true); // Primary and distinct valid backup.
        return store;
    }
    private static PrivateStoreBatch Prepare(ThreadFavoriteStore store) =>
        store.PrepareAdditiveBatch(Operation, PlanHash, [Thread("new")]);
    private static PrivateStoreBatchInspection Inspect(ProfileLease lease, NativePrivateApplyStoreFence fence) =>
        ThreadFavoriteStore.InspectRecoveryFence(lease, fence, Operation, PlanHash);
    private static void InspectUnchanged(ProfileLease lease, NativePrivateApplyStoreFence fence,
        PrivateStoreBatchInspection expected)
    {
        DiskEntry[] before = Capture(lease.ProfileDirectory);
        Assert.Equal(expected, Inspect(lease, fence));
        Assert.Equal(expected, Inspect(lease, fence)); // No hidden recovery on the next read.
        AssertUnchanged(before, Capture(lease.ProfileDirectory));
    }

    [Fact]
    public void MissingBaselineIsScalarNoChangeWithoutConstructingAnyStoreOrCreatingFiles()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        InspectUnchanged(lease, MissingFence(), PrivateStoreBatchInspection.NoChange);
        Assert.Empty(Directory.EnumerateFileSystemEntries(temp.Path, "*favorites*"));
    }

    [Fact]
    public void RealLegacyNoopSurvivesRestartWithoutMigrationOrBackupRotation()
    {
        using TempDirectory temp = new();
        NativePrivateApplyStoreFence fence;
        using (ProfileLease lease = ProfileLease.Acquire(temp.Path))
        {
            var store = Seed(temp.Path, lease);
            var batch = store.PrepareAdditiveBatch(Operation, PlanHash, [Thread("old-a")]);
            Assert.False(batch.HasChanges);
            fence = Copy(batch.Fence);
        }
        using ProfileLease restarted = ProfileLease.Acquire(temp.Path);
        InspectUnchanged(restarted, fence, PrivateStoreBatchInspection.NoChange);
        using JsonDocument json = JsonDocument.Parse(File.ReadAllBytes(Primary(temp.Path)));
        Assert.Equal(1, json.RootElement.GetProperty("Version").GetInt32());
        Assert.False(json.RootElement.TryGetProperty("OperationMarker", out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RealPreparedFenceIsExactBeforeUnderNewLeaseButNotAContinuousHistoryProof(bool existing)
    {
        using TempDirectory temp = new();
        NativePrivateApplyStoreFence fence;
        using (ProfileLease lease = ProfileLease.Acquire(temp.Path))
        {
            var store = existing ? Seed(temp.Path, lease) : new ThreadFavoriteStore(temp.Path, lease);
            fence = Copy(Prepare(store).Fence);
        }
        using ProfileLease restarted = ProfileLease.Acquire(temp.Path);
        InspectUnchanged(restarted, fence, PrivateStoreBatchInspection.ExactBefore);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RealBatchCommitThenStaticRestartVerifiesExactAfterMarkerAndExpectedBackup(bool existing)
    {
        using TempDirectory temp = new();
        NativePrivateApplyStoreFence fence;
        using (ProfileLease lease = ProfileLease.Acquire(temp.Path))
        {
            var store = existing ? Seed(temp.Path, lease) : new ThreadFavoriteStore(temp.Path, lease);
            var batch = Prepare(store);
            fence = Copy(batch.Fence);
            Assert.Equal(PrivateStoreBatchInspection.ExactAfterWithMarker, store.CommitPreparedBatch(batch));
        }
        using ProfileLease restarted = ProfileLease.Acquire(temp.Path);
        InspectUnchanged(restarted, fence, PrivateStoreBatchInspection.ExactAfterWithMarker);
        Assert.Equal(existing, File.Exists(Primary(temp.Path) + ".bak"));
    }

    [Fact]
    public void RestoringExactBeforeAfterARealCommitStillOnlyReportsAPointSample()
    {
        using TempDirectory temp = new();
        NativePrivateApplyStoreFence fence;
        using (ProfileLease lease = ProfileLease.Acquire(temp.Path))
        {
            var store = Seed(temp.Path, lease);
            byte[] primary = File.ReadAllBytes(Primary(temp.Path));
            byte[] backup = File.ReadAllBytes(Primary(temp.Path) + ".bak");
            var batch = Prepare(store);
            fence = Copy(batch.Fence);
            Assert.Equal(PrivateStoreBatchInspection.ExactAfterWithMarker, store.CommitPreparedBatch(batch));
            // Deliberate fixture ABA: inspection must not pretend it knows the history.
            File.WriteAllBytes(Primary(temp.Path), primary);
            File.WriteAllBytes(Primary(temp.Path) + ".bak", backup);
        }
        using ProfileLease restarted = ProfileLease.Acquire(temp.Path);
        InspectUnchanged(restarted, fence, PrivateStoreBatchInspection.ExactBefore);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExactAfterBytesWithWrongExpectedOperationOrContentAreDrifted(bool wrongContent)
    {
        using TempDirectory temp = new();
        NativePrivateApplyStoreFence fence;
        using (ProfileLease lease = ProfileLease.Acquire(temp.Path))
        {
            var store = new ThreadFavoriteStore(temp.Path, lease);
            var batch = Prepare(store);
            fence = Copy(batch.Fence);
            store.CommitPreparedBatch(batch);
        }
        using ProfileLease restarted = ProfileLease.Acquire(temp.Path);
        DiskEntry[] before = Capture(temp.Path);
        Assert.Equal(PrivateStoreBatchInspection.Drifted, ThreadFavoriteStore.InspectRecoveryFence(
            restarted, fence, wrongContent ? Operation : OtherOperation, wrongContent ? new('b', 64) : PlanHash));
        AssertUnchanged(before, Capture(temp.Path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ValidForeignMarkerIsDriftEvenWhenExpectedAfterImageMatchesItsActualBytes(bool wrongContent)
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        var batch = Prepare(new ThreadFavoriteStore(temp.Path, lease));
        var foreign = new PrivateStoreOperationMarker(wrongContent ? Operation : OtherOperation,
            wrongContent ? new('b', 64) : PlanHash, NativePrivateApplyStoreKind.ThreadFavorites);
        byte[] bytes = ReplaceMarker(batch.CopyAfterBytes(), JsonSerializer.Serialize(foreign));
        File.WriteAllBytes(Primary(temp.Path), bytes);
        var fence = new NativePrivateApplyStoreFence(batch.Fence.Kind, batch.Fence.Before,
            PrivateStoreBatchFiles.Image(bytes, 2, Operation, 1), batch.Fence.BeforeBackup);
        InspectUnchanged(lease, fence, PrivateStoreBatchInspection.Drifted);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ExactNoopImagesWithAnyOwnOrForeignPrimaryOrBackupMarkerAreDrifted(bool backup, bool foreign)
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        var batch = Prepare(new ThreadFavoriteStore(temp.Path, lease));
        byte[] marked = batch.CopyAfterBytes();
        if (foreign) marked = ReplaceMarker(marked, JsonSerializer.Serialize(new PrivateStoreOperationMarker(
            OtherOperation, PlanHash, NativePrivateApplyStoreKind.ThreadFavorites)));
        byte[] legacy = Encoding.UTF8.GetBytes("{\"Version\":1,\"Revision\":0,\"Threads\":[]}");
        File.WriteAllBytes(Primary(temp.Path), backup ? legacy : marked);
        if (backup) File.WriteAllBytes(Primary(temp.Path) + ".bak", marked);
        var image = backup ? PrivateStoreBatchFiles.Image(legacy, 1, null, 0)
            : PrivateStoreBatchFiles.Image(marked, 2, Operation, 1);
        var backupImage = backup ? PrivateStoreBatchFiles.Image(marked, 2, Operation, 1)
            : PrivateStoreBatchFiles.Missing();
        var fence = new NativePrivateApplyStoreFence(batch.Fence.Kind, image, image, backupImage);
        InspectUnchanged(lease, fence, PrivateStoreBatchInspection.Drifted);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("{\"OperationId\":\"42424242-1111-2222-3333-444444444444\",\"ContentPlanSha256\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"StoreKind\":1}")]
    [InlineData("{\"OperationId\":\"42424242-1111-2222-3333-444444444444\",\"ContentPlanSha256\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"StoreKind\":0,\"extra\":1}")]
    [InlineData("{\"OperationId\":\"42424242-1111-2222-3333-444444444444\",\"ContentPlanSha256\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"StoreKind\":0,\"StoreKind\":0}")]
    [InlineData("{\"OperationId\":\"00000000-0000-0000-0000-000000000000\",\"ContentPlanSha256\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"StoreKind\":0}")]
    public void MalformedOrWrongKindMarkerIsUnreadableNotPublishedAndNeverRepaired(string marker)
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        var batch = Prepare(new ThreadFavoriteStore(temp.Path, lease));
        File.WriteAllBytes(Primary(temp.Path), ReplaceMarker(batch.CopyAfterBytes(), marker));
        InspectUnchanged(lease, batch.Fence, PrivateStoreBatchInspection.Unreadable);
    }

    [Theory]
    [InlineData("before", false)] [InlineData("before", true)]
    [InlineData("after", false)] [InlineData("after", true)]
    [InlineData("noop", false)] [InlineData("noop", true)]
    public void BackupByteDriftOrRemovalPreventsEveryExactClassification(string phase, bool remove)
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        var store = Seed(temp.Path, lease);
        var batch = phase == "noop" ? store.PrepareAdditiveBatch(Operation, PlanHash, []) : Prepare(store);
        if (phase == "after") store.CommitPreparedBatch(batch);
        string backup = Primary(temp.Path) + ".bak";
        if (remove) File.Delete(backup);
        else File.AppendAllText(backup, " \n"); // Semantics/revision unchanged; bytes differ.
        InspectUnchanged(lease, batch.Fence, PrivateStoreBatchInspection.Drifted);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ValidPrimaryWhitespaceDriftIsNotExactBeforeOrExactAfter(bool committed)
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        var store = Seed(temp.Path, lease);
        var batch = Prepare(store);
        if (committed) store.CommitPreparedBatch(batch);
        File.AppendAllText(Primary(temp.Path), " \n");
        InspectUnchanged(lease, batch.Fence, PrivateStoreBatchInspection.Drifted);
    }

    [Fact]
    public void MissingPrimaryWithValidBackupIsUnreadableAndNotRestored()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        var store = Seed(temp.Path, lease);
        var batch = Prepare(store);
        File.Delete(Primary(temp.Path));
        InspectUnchanged(lease, batch.Fence, PrivateStoreBatchInspection.Unreadable);
        Assert.False(File.Exists(Primary(temp.Path)));
    }

    [Theory]
    [InlineData(false, "")]
    [InlineData(true, "")]
    [InlineData(false, "{synthetic invalid")]
    [InlineData(true, "{synthetic invalid")]
    [InlineData(false, "{\"Version\":1,\"Revision\":0,\"Threads\":[],\"Unknown\":1}")]
    [InlineData(true, "{\"Version\":1,\"Revision\":0,\"Threads\":[],\"Unknown\":1}")]
    [InlineData(false, "{\"Version\":1,\"Version\":1,\"Revision\":0,\"Threads\":[]}")]
    [InlineData(true, "{\"Version\":1,\"Version\":1,\"Revision\":0,\"Threads\":[]}")]
    public void StrictExistingParserRejectsMalformedPrimaryOrBackupWithoutMutation(bool backup, string raw)
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        var batch = Prepare(Seed(temp.Path, lease));
        File.WriteAllText(Primary(temp.Path) + (backup ? ".bak" : ""), raw);
        InspectUnchanged(lease, batch.Fence, PrivateStoreBatchInspection.Unreadable);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void InvalidUtf8AndOverLimitDocumentsFailClosedWithoutRewriting(bool oversize)
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        if (oversize)
        {
            using FileStream file = File.Create(Primary(temp.Path));
            file.SetLength(ProfileStoreLimits.DefaultFavoriteFileBytes + 1L);
        }
        else File.WriteAllBytes(Primary(temp.Path), [0xff, 0xfe, 0x80]);
        InspectUnchanged(lease, MissingFence(), PrivateStoreBatchInspection.Unreadable);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void DirectoryInPrimaryOrBackupSlotIsUnreadableAndPreserved(bool backup)
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        string path = Primary(temp.Path) + (backup ? ".bak" : "");
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "synthetic-evidence"), "keep");
        InspectUnchanged(lease, MissingFence(), PrivateStoreBatchInspection.Unreadable);
    }

    [NativePrivateUnixTheory]
    [InlineData(false, false)] [InlineData(false, true)]
    [InlineData(true, false)] [InlineData(true, true)]
    public void LinkedOrDanglingPrimaryAndBackupAreUnreadableWithoutFollowingTargets(bool backup, bool dangling)
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        string target = Path.Combine(temp.Path, "synthetic-link-target");
        if (!dangling) File.WriteAllText(target, "synthetic target, never a recovery source");
        File.CreateSymbolicLink(Primary(temp.Path) + (backup ? ".bak" : ""), target);
        InspectUnchanged(lease, MissingFence(), PrivateStoreBatchInspection.Unreadable);
    }

    [NativePrivateSharingTheory]
    [InlineData(false)] [InlineData(true)]
    public void InaccessiblePrimaryOrBackupReturnsOnlyUnreadable(bool backup)
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        var batch = Prepare(Seed(temp.Path, lease));
        DiskEntry[] before = Capture(temp.Path);
        string path = Primary(temp.Path) + (backup ? ".bak" : "");
        using (FileStream exclusive = new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.ThrowsAny<IOException>(() => { using var control = RegularProfileFile.OpenRead(path); });
            Assert.Equal(PrivateStoreBatchInspection.Unreadable, Inspect(lease, batch.Fence));
        }
        AssertUnchanged(before, Capture(temp.Path));
    }

    [Theory]
    [InlineData("thread-favorites.json.pending-", false)]
    [InlineData("thread-favorites.json.pending-not-a-guid", true)]
    [InlineData("thread-favorites.json.private-pending-", false)]
    [InlineData("thread-favorites.json.private-pending-malformed", true)]
    [InlineData(".thread-favorites.json.42424242111122223333444444444444.tmp", false)]
    public void PendingIncludingActualAtomicJsonTempIsUnreadableAndPreserved(string name, bool directory)
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        string path = Path.Combine(temp.Path, name);
        if (directory)
        {
            Directory.CreateDirectory(path);
            File.WriteAllText(Path.Combine(path, "synthetic-pending-evidence"), "keep");
        }
        else File.WriteAllBytes(path, [0, 1, 2, 255]);
        InspectUnchanged(lease, MissingFence(), PrivateStoreBatchInspection.Unreadable);
        Assert.False(File.Exists(Primary(temp.Path)));
    }

    [Fact]
    public void ActualAtomicJsonTempAlsoBlocksAnOtherwiseExactCommittedAfterOnRestart()
    {
        using TempDirectory temp = new();
        NativePrivateApplyStoreFence fence;
        using (ProfileLease lease = ProfileLease.Acquire(temp.Path))
        {
            var store = Seed(temp.Path, lease);
            var batch = Prepare(store);
            fence = Copy(batch.Fence);
            Assert.Equal(PrivateStoreBatchInspection.ExactAfterWithMarker, store.CommitPreparedBatch(batch));
        }
        using ProfileLease restarted = ProfileLease.Acquire(temp.Path);
        File.WriteAllText(Path.Combine(temp.Path, ".thread-favorites.json.synthetic-interrupted.tmp"),
            "synthetic interrupted atomic write; do not restore or remove");
        InspectUnchanged(restarted, fence, PrivateStoreBatchInspection.Unreadable);
    }

    [NativePrivateUnixTheory]
    [InlineData(".pending-not-a-guid")]
    [InlineData(".private-pending-not-a-guid")]
    public void DanglingPendingLinkAlsoBlocksWithoutOpeningOrRemovingIt(string suffix)
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        File.CreateSymbolicLink(Primary(temp.Path) + suffix, Path.Combine(temp.Path, "missing-synthetic-target"));
        InspectUnchanged(lease, MissingFence(), PrivateStoreBatchInspection.Unreadable);
    }

    [Fact]
    public void StaticInspectionNeverConstructsUnselectedBookmarkStoreOrTouchesUnselectedData()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        string bookmark = Path.Combine(temp.Path, "post-bookmarks.json");
        // A normal bookmark constructor would clean this valid legacy pending file.
        File.WriteAllText(bookmark + ".pending-" + Operation.ToString("N"), "synthetic pending note");
        File.WriteAllText(bookmark + ".bak", "synthetic note backup must never be parsed");
        File.WriteAllText(Path.Combine(temp.Path, "media-favorites.json"), "synthetic invalid unselected document");
        File.WriteAllText(Path.Combine(temp.Path, "media-favorites.json.private-pending-malformed"), "keep");
        File.WriteAllText(bookmark, "{\"schemaVersion\":1,\"revision\":0,\"bookmarks\":[]}");
        InspectUnchanged(lease, MissingFence(), PrivateStoreBatchInspection.NoChange);
    }

    [NativePrivateUnixTheory]
    [InlineData(false)] [InlineData(true)]
    public void MissingOrReplacedLeasedDirectoryCannotMasqueradeAsMissingStore(bool replace)
    {
        using TempDirectory temp = new();
        string profile = Path.Combine(temp.Path, "synthetic-profile");
        using ProfileLease lease = ProfileLease.Acquire(profile);
        string moved = profile + "-moved";
        Directory.Move(profile, moved);
        if (replace) Directory.CreateDirectory(profile);
        DiskEntry[] before = Capture(temp.Path);
        Assert.Equal(PrivateStoreBatchInspection.Unreadable, Inspect(lease, MissingFence()));
        AssertUnchanged(before, Capture(temp.Path));
    }

    [Fact]
    public void ProgrammerArgumentAndDisposedLeaseErrorsAreNotUnreadableDiskResults()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        var missing = PrivateStoreBatchFiles.Missing();
        DiskEntry[] before = Capture(temp.Path);
        Assert.Throws<ArgumentNullException>(() => ThreadFavoriteStore.InspectRecoveryFence(null!, MissingFence(), Operation, PlanHash));
        Assert.Throws<InvalidDataException>(() => Inspect(lease, null!));
        Assert.Throws<InvalidDataException>(() => Inspect(lease, new(NativePrivateApplyStoreKind.MediaFavorites, missing, missing, missing)));
        Assert.Throws<InvalidDataException>(() => Inspect(lease, new(NativePrivateApplyStoreKind.ThreadFavorites, missing, missing)));
        Assert.Throws<InvalidDataException>(() => ThreadFavoriteStore.InspectRecoveryFence(lease, MissingFence(), Guid.Empty, PlanHash));
        Assert.Throws<InvalidDataException>(() => ThreadFavoriteStore.InspectRecoveryFence(lease, MissingFence(), Operation, "invalid"));
        AssertUnchanged(before, Capture(temp.Path));
        lease.Dispose();
        Assert.Throws<ObjectDisposedException>(() => Inspect(lease, MissingFence()));
        AssertUnchanged(before, Capture(temp.Path));
    }

    private static byte[] ReplaceMarker(byte[] bytes, string marker)
    {
        using JsonDocument json = JsonDocument.Parse(bytes);
        // Keep the supplied raw marker, including duplicate fields (no JsonNode normalization).
        return Encoding.UTF8.GetBytes("{" + string.Join(",", json.RootElement.EnumerateObject().Select(p =>
            JsonSerializer.Serialize(p.Name) + ":" + (p.Name == "OperationMarker" ? marker : p.Value.GetRawText()))) + "}");
    }

    private sealed record DiskEntry(string Name, FileAttributes Attributes, DateTime LastWriteUtc,
        string? LinkTarget, long? Length, byte[]? Bytes);

    private static DiskEntry[] Capture(string root)
    {
        var entries = new List<DiskEntry>();
        void Visit(string directory)
        {
            foreach (string path in Directory.EnumerateFileSystemEntries(directory).Order(StringComparer.Ordinal))
            {
                FileAttributes attributes = File.GetAttributes(path);
                bool isDirectory = (attributes & FileAttributes.Directory) != 0;
                FileSystemInfo info = isDirectory ? new DirectoryInfo(path) : new FileInfo(path);
                string? link = info.LinkTarget;
                bool regular = !isDirectory && link is null && (attributes & FileAttributes.ReparsePoint) == 0;
                // Managed exclusive lease locks cannot be opened by the inventory.
                bool leaseLock = Path.GetFileName(path) is ".meowchan-profile.lock" or ".ochan-profile.lock";
                entries.Add(new(Path.GetRelativePath(root, path), attributes, info.LastWriteTimeUtc,
                    link, regular ? new FileInfo(path).Length : null,
                    regular && !leaseLock ? File.ReadAllBytes(path) : null));
                if (isDirectory && link is null && (attributes & FileAttributes.ReparsePoint) == 0) Visit(path);
            }
        }
        Visit(root);
        return entries.ToArray();
    }

    private static void AssertUnchanged(DiskEntry[] before, DiskEntry[] after)
    {
        Assert.Equal(before.Select(e => e.Name), after.Select(e => e.Name));
        for (int i = 0; i < before.Length; i++)
        {
            Assert.Equal(before[i].Attributes, after[i].Attributes);
            Assert.Equal(before[i].LastWriteUtc, after[i].LastWriteUtc);
            Assert.Equal(before[i].LinkTarget, after[i].LinkTarget);
            Assert.Equal(before[i].Length, after[i].Length);
            Assert.Equal(before[i].Bytes, after[i].Bytes);
        }
    }
}
