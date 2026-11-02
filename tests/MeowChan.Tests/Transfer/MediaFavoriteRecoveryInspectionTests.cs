using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowChan.Favorites;
using MeowChan.Persistence;
using MeowChan.Transfer;

namespace MeowChan.Tests.Transfer;

// Synthetic isolated profiles only. Inspection never constructs a store or
// receives a transient PrivateStoreBatch. These are disk facts, not Apply tests.
public sealed class MediaFavoriteRecoveryInspectionTests
{
    private const string FileName = "media-favorites.json";
    private static readonly Guid Operation = Guid.Parse("638b60cb-3fc8-4a32-b72d-ea0f12345678");
    private static readonly Guid OtherOperation = Guid.Parse("938b60cb-3fc8-4a32-b72d-ea0f12345678");
    private static readonly string PlanHash = new('a', 64);
    private static readonly string OtherHash = new('c', 64);
    private static FavoriteEntry Entry(string media = "1") => new(
        new(new("synthetic-source", "synthetic-board", "100"), media, "original", new('b', 64)),
        12, "synthetic-original.jpg");
    private static string StorePath(string profile) => Path.Combine(profile, FileName);
    private static byte[] Legacy(params FavoriteEntry[] entries)
        => JsonSerializer.SerializeToUtf8Bytes(new { Version = 1, Entries = entries });
    private static NativePrivateApplyStoreImage Image(byte[] bytes)
    {
        using JsonDocument json = JsonDocument.Parse(bytes);
        JsonElement root = json.RootElement;
        int schema = root.GetProperty("Version").GetInt32();
        return PrivateStoreBatchFiles.Image(bytes, schema,
            schema == 1 ? null : root.GetProperty("InstanceId").GetGuid(),
            schema == 1 ? 0 : root.GetProperty("Revision").GetInt64());
    }
    private static NativePrivateApplyStoreFence MissingFence() => new(
        NativePrivateApplyStoreKind.MediaFavorites, PrivateStoreBatchFiles.Missing(),
        PrivateStoreBatchFiles.Missing(), PrivateStoreBatchFiles.Missing());
    private static PrivateStoreBatchInspection Inspect(ProfileLease lease, NativePrivateApplyStoreFence fence)
        => FavoriteStore.InspectRecoveryFence(lease, fence, Operation, PlanHash);

    // Excludes only lock-handle bytes (FileShare.None), not their directory entry.
    // Never follows symlinks, including dangling ones, or opens pending links.
    // Full byte encodings, rather than parsed JSON, detect migration/normalizing.
    private static string[] Inventory(string profile)
    {
        var entries = new List<string>();
        Walk(profile);
        return entries.OrderBy(value => value, StringComparer.Ordinal).ToArray();

        void Walk(string directory)
        {
            foreach (string path in Directory.EnumerateFileSystemEntries(directory))
            {
                string relative = Path.GetRelativePath(profile, path);
                FileInfo info = new(path);
                string? linkTarget = info.LinkTarget;
                if (linkTarget is not null)
                    entries.Add(relative + "|link|" + linkTarget);
                else if ((info.Attributes & FileAttributes.Directory) != 0)
                {
                    entries.Add(relative + "|directory");
                    Walk(path);
                }
                else if (Path.GetFileName(path) is ".meowchan-profile.lock" or ".ochan-profile.lock")
                    entries.Add(relative + "|lease-lock");
                else
                    entries.Add(relative + "|bytes|" + Convert.ToBase64String(File.ReadAllBytes(path)));
            }
        }
    }

    private static void AssertReadOnly(ProfileLease lease, NativePrivateApplyStoreFence fence,
        PrivateStoreBatchInspection expected)
    {
        string[] before = Inventory(lease.ProfileDirectory);
        for (int attempt = 0; attempt < 2; attempt++)
        {
            Assert.Equal(expected, Inspect(lease, fence)); // Must reread; no cached store state.
            Assert.Equal(before, Inventory(lease.ProfileDirectory));
        }
    }

    [Fact]
    public void MissingBaselineIsNoChangeAndDoesNotCreateAnyStoreOrBackup()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        AssertReadOnly(lease, MissingFence(), PrivateStoreBatchInspection.NoChange);
        Assert.False(File.Exists(StorePath(temp.Path)));
        Assert.False(File.Exists(StorePath(temp.Path) + ".bak"));
    }

    [Theory]
    [InlineData(1, false)] [InlineData(1, true)]
    [InlineData(2, false)] [InlineData(2, true)]
    public void ExistingNoopDoesNotMigrateOrRewritePrimaryOrBackup(int version, bool backup)
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        string path = StorePath(temp.Path);
        byte[] bytes = version == 1 ? Legacy(Entry()) : JsonSerializer.SerializeToUtf8Bytes(new
        { Version = 2, Entries = new[] { Entry() }, InstanceId = Operation, Revision = 7L });
        File.WriteAllBytes(path, bytes);
        if (backup) File.WriteAllBytes(path + ".bak", Legacy());
        FavoriteStore store = new(temp.Path, lease);
        PrivateStoreBatch batch = store.PrepareAdditiveBatch(Operation, PlanHash, [Entry()]);
        Assert.False(batch.HasChanges);
        AssertReadOnly(lease, batch.Fence, PrivateStoreBatchInspection.NoChange);
        Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void RealCommittedBatchIsRecognizedAfterNewLeaseWithoutConstructingAStore(int baseline)
    {
        using TempDirectory temp = new();
        string path = StorePath(temp.Path);
        NativePrivateApplyStoreFence fence;
        using (ProfileLease lease = ProfileLease.Acquire(temp.Path))
        {
            FavoriteStore store = new(temp.Path, lease);
            if (baseline > 0) File.WriteAllBytes(path, Legacy(Entry()));
            if (baseline == 2) File.WriteAllBytes(path + ".bak", Legacy(Entry("older")));
            PrivateStoreBatch batch = store.PrepareAdditiveBatch(Operation, PlanHash, [Entry("new")]);
            fence = batch.Fence;
            AssertReadOnly(lease, fence, PrivateStoreBatchInspection.ExactBefore);
            Assert.Equal(PrivateStoreBatchInspection.ExactAfterWithMarker, store.CommitPreparedBatch(batch));
            Assert.Equal(batch.CopyAfterBytes(), File.ReadAllBytes(path));
            if (baseline > 0) Assert.Equal(Legacy(Entry()), File.ReadAllBytes(path + ".bak"));
        }
        using ProfileLease restarted = ProfileLease.Acquire(temp.Path);
        AssertReadOnly(restarted, fence, PrivateStoreBatchInspection.ExactAfterWithMarker);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ExactBeforeOnRestartIsOnlyAPointSampleEvenAfterByteRestoration(bool existing)
    {
        using TempDirectory temp = new();
        string path = StorePath(temp.Path);
        NativePrivateApplyStoreFence fence;
        using (ProfileLease lease = ProfileLease.Acquire(temp.Path))
        {
            if (existing) File.WriteAllBytes(path, Legacy(Entry()));
            FavoriteStore store = new(temp.Path, lease);
            fence = store.PrepareAdditiveBatch(Operation, PlanHash, [Entry("new")]).Fence;
        }
        using ProfileLease restarted = ProfileLease.Acquire(temp.Path);
        AssertReadOnly(restarted, fence, PrivateStoreBatchInspection.ExactBefore);
        // An external fixture edit followed by restoration is deliberately not
        // distinguishable from no edit. Never claim NotStartedProven from this.
        File.WriteAllBytes(path, Legacy(Entry("intermediate")));
        AssertReadOnly(restarted, fence, PrivateStoreBatchInspection.Drifted);
        if (existing) File.WriteAllBytes(path, Legacy(Entry()));
        else File.Delete(path);
        AssertReadOnly(restarted, fence, PrivateStoreBatchInspection.ExactBefore);
    }

    [Theory]
    [InlineData(false, false)] [InlineData(false, true)]
    [InlineData(true, false)] [InlineData(true, true)]
    public void ParsedValueEqualByteDriftIsNotExactBeforeOrAfter(bool after, bool backup)
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        string path = StorePath(temp.Path);
        File.WriteAllBytes(path, Legacy(Entry()));
        File.WriteAllBytes(path + ".bak", Legacy());
        FavoriteStore store = new(temp.Path, lease);
        PrivateStoreBatch batch = store.PrepareAdditiveBatch(Operation, PlanHash, [Entry("new")]);
        if (after) store.CommitPreparedBatch(batch);
        File.AppendAllText(path + (backup ? ".bak" : ""), " ");
        AssertReadOnly(lease, batch.Fence, PrivateStoreBatchInspection.Drifted);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ExactAfterBytesWithWrongExpectedOperationOrContentAreDrifted(bool content)
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        FavoriteStore store = new(temp.Path, lease);
        PrivateStoreBatch batch = store.PrepareAdditiveBatch(Operation, PlanHash, [Entry()]);
        store.CommitPreparedBatch(batch);
        string[] before = Inventory(temp.Path);
        Assert.Equal(PrivateStoreBatchInspection.Drifted, FavoriteStore.InspectRecoveryFence(lease,
            batch.Fence, content ? Operation : OtherOperation, content ? OtherHash : PlanHash));
        Assert.Equal(before, Inventory(temp.Path));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ValidForeignMarkerNeverPassesEvenWhenExpectedAfterImageMatchesItsBytes(bool content)
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        FavoriteStore store = new(temp.Path, lease);
        PrivateStoreBatch batch = store.PrepareAdditiveBatch(Operation, PlanHash, [Entry()]);
        JsonObject document = JsonNode.Parse(batch.CopyAfterBytes())!.AsObject();
        if (content) document["OperationMarker"]!["ContentPlanSha256"] = OtherHash;
        else document["OperationMarker"]!["OperationId"] = OtherOperation.ToString("D");
        byte[] foreign = Encoding.UTF8.GetBytes(document.ToJsonString());
        File.WriteAllBytes(StorePath(temp.Path), foreign);
        var fence = new NativePrivateApplyStoreFence(NativePrivateApplyStoreKind.MediaFavorites,
            batch.Fence.Before, Image(foreign), batch.Fence.BeforeBackup);
        AssertReadOnly(lease, fence, PrivateStoreBatchInspection.Drifted);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void MarkerInByteExactBeforeOrNoopIsNotAnUnmarkedBaseline(bool changes)
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        FavoriteStore store = new(temp.Path, lease);
        PrivateStoreBatch batch = store.PrepareAdditiveBatch(Operation, PlanHash, [Entry()]);
        byte[] marked = batch.CopyAfterBytes();
        File.WriteAllBytes(StorePath(temp.Path), marked);
        NativePrivateApplyStoreImage before = Image(marked);
        NativePrivateApplyStoreImage after = changes
            ? new(true, 3, before.Instance, before.Revision + 1, before.ByteLength, OtherHash) : before;
        var fence = new NativePrivateApplyStoreFence(NativePrivateApplyStoreKind.MediaFavorites,
            before, after, PrivateStoreBatchFiles.Missing());
        AssertReadOnly(lease, fence, PrivateStoreBatchInspection.Drifted);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void MarkedBackupIsDriftEvenWithByteExactExpectedBackup(bool after)
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        FavoriteStore store = new(temp.Path, lease);
        PrivateStoreBatch batch = store.PrepareAdditiveBatch(Operation, PlanHash, [Entry()]);
        byte[] marked = batch.CopyAfterBytes();
        File.WriteAllBytes(StorePath(temp.Path) + ".bak", marked);
        JsonObject next = JsonNode.Parse(marked)!.AsObject();
        next["Revision"] = 2L;
        byte[] primary = after ? Encoding.UTF8.GetBytes(next.ToJsonString()) : Legacy();
        File.WriteAllBytes(StorePath(temp.Path), primary);
        NativePrivateApplyStoreImage backup = Image(marked);
        // On the after branch the rotated backup is byte-exactly Before;
        // on the noop branch it is byte-exactly BeforeBackup. Neither can
        // launder a valid operation marker in the backup into an unmarked fact.
        var fence = new NativePrivateApplyStoreFence(NativePrivateApplyStoreKind.MediaFavorites,
            after ? backup : Image(primary), Image(primary), backup);
        AssertReadOnly(lease, fence, PrivateStoreBatchInspection.Drifted);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void BackupAloneIsUnreadableAndNeverRestored(bool malformed)
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        File.WriteAllBytes(StorePath(temp.Path) + ".bak", malformed ? "{"u8.ToArray() : Legacy(Entry()));
        AssertReadOnly(lease, MissingFence(), PrivateStoreBatchInspection.Unreadable);
        Assert.False(File.Exists(StorePath(temp.Path)));
    }

    public static IEnumerable<object[]> MalformedDocuments()
    {
        yield return [Array.Empty<byte>()];
        yield return [new byte[] { 0xff, 0xfe, 0x80 }];
        yield return ["{"u8.ToArray()];
        yield return ["{\"Version\":1,\"Entries\":[],\"Entries\":[]}"u8.ToArray()];
        yield return [JsonSerializer.SerializeToUtf8Bytes(new { Version = 99, Entries = Array.Empty<FavoriteEntry>() })];
        yield return [JsonSerializer.SerializeToUtf8Bytes(new { Version = 1, Entries = new[] { Entry(), Entry() } })];
        yield return [JsonSerializer.SerializeToUtf8Bytes(new { Version = 1, Entries = new[] { Entry() with { Length = -1 } } })];
        // Reuse the established strict marker corpus: absent/null/duplicate fields,
        // foreign store kind, invalid hashes/operation, extra fields and wrong schema.
        foreach (object[] row in MediaFavoritePrivateBatchTests.BadMarkedDocuments())
            yield return [Encoding.UTF8.GetBytes((string)row[0])];
    }

    [Theory]
    [MemberData(nameof(MalformedDocuments))]
    public void MalformedPrimaryIsUnreadableAndNeverMigrated(byte[] bytes)
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        File.WriteAllBytes(StorePath(temp.Path), bytes);
        AssertReadOnly(lease, MissingFence(), PrivateStoreBatchInspection.Unreadable);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void MalformedBackupIsUnreadableEvenWithExactPrimary(bool after)
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        FavoriteStore store = new(temp.Path, lease);
        PrivateStoreBatch batch = store.PrepareAdditiveBatch(Operation, PlanHash, [Entry()]);
        if (after) store.CommitPreparedBatch(batch);
        File.WriteAllText(StorePath(temp.Path) + ".bak", "synthetic-malformed-backup");
        AssertReadOnly(lease, batch.Fence, PrivateStoreBatchInspection.Unreadable);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void OversizedPrimaryAndBackupAreUnreadable(bool backup)
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        File.WriteAllBytes(StorePath(temp.Path), Legacy());
        File.WriteAllBytes(StorePath(temp.Path) + (backup ? ".bak" : ""), new byte[FavoriteStore.MaximumBytes + 1]);
        AssertReadOnly(lease, MissingFence(), PrivateStoreBatchInspection.Unreadable);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void DirectoryInsteadOfPrimaryOrBackupIsUnreadableAndUntouched(bool backup)
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        if (backup) File.WriteAllBytes(StorePath(temp.Path), Legacy());
        string path = StorePath(temp.Path) + (backup ? ".bak" : "");
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "synthetic-evidence"), "do not clean");
        AssertReadOnly(lease, MissingFence(), PrivateStoreBatchInspection.Unreadable);
    }

    [NativePrivateUnixTheory]
    [InlineData(false, false)] [InlineData(false, true)]
    [InlineData(true, false)] [InlineData(true, true)]
    public void SymbolicPrimaryOrBackupIsUnreadableWithoutFollowingOrDeletingIt(bool backup, bool dangling)
    {
        using TempDirectory temp = new();
        using TempDirectory outside = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        string target = Path.Combine(outside.Path, "synthetic.json");
        byte[] bytes = Legacy(Entry());
        if (!dangling) File.WriteAllBytes(target, bytes);
        if (backup) File.WriteAllBytes(StorePath(temp.Path), Legacy());
        File.CreateSymbolicLink(StorePath(temp.Path) + (backup ? ".bak" : ""), target);
        AssertReadOnly(lease, MissingFence(), PrivateStoreBatchInspection.Unreadable);
        if (dangling) Assert.False(File.Exists(target));
        else Assert.Equal(bytes, File.ReadAllBytes(target));
    }

    [NativePrivateSharingTheory]
    [InlineData(false)] [InlineData(true)]
    public void ExclusiveFileIsUnreadableAndItsBytesRemainUnchanged(bool backup)
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        string path = StorePath(temp.Path);
        File.WriteAllBytes(path, Legacy());
        if (backup) path += ".bak";
        File.WriteAllBytes(path, Legacy(Entry()));
        string[] before = Inventory(temp.Path);
        using (FileStream exclusive = new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.ThrowsAny<IOException>(() => { using FileStream control = RegularProfileFile.OpenRead(path); });
            Assert.Equal(PrivateStoreBatchInspection.Unreadable, Inspect(lease, MissingFence()));
        }
        Assert.Equal(before, Inventory(temp.Path));
    }

    [NativePrivatePermissionTheory]
    [InlineData(false)] [InlineData(true)]
    public void InaccessiblePrimaryOrBackupIsUnreadableAndKeepsPermissionsAndBytes(bool backup)
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        string path = StorePath(temp.Path);
        File.WriteAllBytes(path, Legacy());
        if (backup) path += ".bak";
        File.WriteAllBytes(path, Legacy(Entry()));
        string[] before = Inventory(temp.Path);
        UnixFileMode mode = File.GetUnixFileMode(path);
        try
        {
            File.SetUnixFileMode(path, UnixFileMode.None);
            Assert.Throws<UnauthorizedAccessException>(() => { using FileStream control = RegularProfileFile.OpenRead(path); });
            Assert.Equal(PrivateStoreBatchInspection.Unreadable, Inspect(lease, MissingFence()));
            Assert.Equal(UnixFileMode.None, File.GetUnixFileMode(path));
        }
        finally { File.SetUnixFileMode(path, mode); }
        Assert.Equal(before, Inventory(temp.Path));
    }

    [Theory]
    [InlineData(".pending-", false)] [InlineData(".pending-malformed", false)]
    [InlineData(".pending-11111111111111111111111111111111", true)]
    [InlineData(".private-pending-", false)] [InlineData(".private-pending-not-a-guid", true)]
    public void PendingEvidenceBlocksMissingAndNoopWithoutConstructorCleanup(string suffix, bool directory)
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        string pending = StorePath(temp.Path) + suffix;
        if (directory)
        {
            Directory.CreateDirectory(pending);
            File.WriteAllText(Path.Combine(pending, "synthetic-evidence"), "preserve");
        }
        else File.WriteAllText(pending, "synthetic-pending-private-evidence");
        AssertReadOnly(lease, MissingFence(), PrivateStoreBatchInspection.Unreadable);
        File.WriteAllBytes(StorePath(temp.Path), Legacy());
        NativePrivateApplyStoreImage image = Image(Legacy());
        AssertReadOnly(lease, new(NativePrivateApplyStoreKind.MediaFavorites, image, image,
            PrivateStoreBatchFiles.Missing()), PrivateStoreBatchInspection.Unreadable);
    }

    [Theory]
    [InlineData(".pending-malformed")] [InlineData(".private-pending-malformed")]
    public void PendingEvidenceBlocksRealAfterOnRestartWithoutRemovingBytes(string suffix)
    {
        using TempDirectory temp = new();
        NativePrivateApplyStoreFence fence;
        using (ProfileLease lease = ProfileLease.Acquire(temp.Path))
        {
            FavoriteStore store = new(temp.Path, lease);
            PrivateStoreBatch batch = store.PrepareAdditiveBatch(Operation, PlanHash, [Entry()]);
            store.CommitPreparedBatch(batch);
            fence = batch.Fence;
            File.WriteAllText(StorePath(temp.Path) + suffix, "synthetic-pending");
        }
        using ProfileLease restarted = ProfileLease.Acquire(temp.Path);
        AssertReadOnly(restarted, fence, PrivateStoreBatchInspection.Unreadable);
    }

    [Fact]
    public void ActualAtomicJsonFileTempBlocksRealAfterOnRestartAndPreservesExactBytes()
    {
        using TempDirectory temp = new();
        NativePrivateApplyStoreFence fence;
        string pending = Path.Combine(temp.Path, "." + FileName + "." + Operation.ToString("N") + ".tmp");
        byte[] evidence = "synthetic interrupted AtomicJsonFile publication"u8.ToArray();
        using (ProfileLease lease = ProfileLease.Acquire(temp.Path))
        {
            FavoriteStore store = new(temp.Path, lease);
            PrivateStoreBatch batch = store.PrepareAdditiveBatch(Operation, PlanHash, [Entry()]);
            store.CommitPreparedBatch(batch);
            fence = batch.Fence;
            File.WriteAllBytes(pending, evidence);
        }
        // The ordinary store payload and marker are exact; the actual hidden
        // AtomicJsonFile temp naming pattern must independently block recovery.
        using ProfileLease restarted = ProfileLease.Acquire(temp.Path);
        AssertReadOnly(restarted, fence, PrivateStoreBatchInspection.Unreadable);
        Assert.Equal(evidence, File.ReadAllBytes(pending));
    }

    [NativePrivateUnixTheory]
    [InlineData(".pending-malformed")] [InlineData(".private-pending-malformed")]
    public void DanglingPendingLinkIsEvidenceWithoutFollowingIt(string suffix)
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        string target = Path.Combine(temp.Path, "absent-synthetic-target");
        File.CreateSymbolicLink(StorePath(temp.Path) + suffix, target);
        AssertReadOnly(lease, MissingFence(), PrivateStoreBatchInspection.Unreadable);
        Assert.False(File.Exists(target));
    }

    [Fact]
    public void UnselectedStoresBackupsAndPendingAreNotReadOrCleaned()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        foreach (string name in new[] { "thread-favorites.json", "thread-favorites.json.bak",
            "post-bookmarks.json", "post-bookmarks.json.bak", "thread-favorites.json.pending-invalid",
            "post-bookmarks.json.private-pending-invalid" })
        {
            string path = Path.Combine(temp.Path, name);
            Directory.CreateDirectory(path); // A read of any selected document would fail.
            File.WriteAllText(Path.Combine(path, "synthetic-private-evidence"), "preserve unselected");
        }
        AssertReadOnly(lease, MissingFence(), PrivateStoreBatchInspection.NoChange);
        FavoriteStore store = new(temp.Path, lease);
        PrivateStoreBatch batch = store.PrepareAdditiveBatch(Operation, PlanHash, [Entry()]);
        store.CommitPreparedBatch(batch);
        AssertReadOnly(lease, batch.Fence, PrivateStoreBatchInspection.ExactAfterWithMarker);
    }

    [NativePrivateUnixTheory]
    [InlineData(false)] [InlineData(true)]
    public void MissingOrReplacedPhysicalLeaseDirectoryIsUnreadableEvenForMatchingBytes(bool replacement)
    {
        using TempDirectory temp = new();
        string profile = Path.Combine(temp.Path, "synthetic-profile");
        using ProfileLease lease = ProfileLease.Acquire(profile);
        File.WriteAllBytes(StorePath(profile), Legacy());
        NativePrivateApplyStoreImage image = Image(Legacy());
        var fence = new NativePrivateApplyStoreFence(NativePrivateApplyStoreKind.MediaFavorites,
            image, image, PrivateStoreBatchFiles.Missing());
        string moved = profile + "-moved";
        Directory.Move(profile, moved);
        if (replacement)
        {
            Directory.CreateDirectory(profile);
            File.WriteAllBytes(StorePath(profile), Legacy());
        }
        string[] before = Inventory(temp.Path);
        Assert.Equal(PrivateStoreBatchInspection.Unreadable, Inspect(lease, fence));
        Assert.Equal(before, Inventory(temp.Path));
        // No restoration required: disposal releases the original moved handle.
    }

    [Fact]
    public void DisposedOrNullLeaseAndInvalidArgumentsAreNotConvertedToUnreadable()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        NativePrivateApplyStoreFence fence = MissingFence();
        string[] before = Inventory(temp.Path);
        Assert.Throws<ArgumentNullException>(() => FavoriteStore.InspectRecoveryFence(null!, fence, Operation, PlanHash));
        Assert.Throws<InvalidDataException>(() => Inspect(lease, null!));
        Assert.Throws<InvalidDataException>(() => FavoriteStore.InspectRecoveryFence(lease, fence, Guid.Empty, PlanHash));
        Assert.Throws<InvalidDataException>(() => FavoriteStore.InspectRecoveryFence(lease, fence, Operation, new('A', 64)));
        Assert.Throws<InvalidDataException>(() => Inspect(lease, new(NativePrivateApplyStoreKind.ThreadFavorites,
            fence.Before, fence.ExpectedAfter, fence.BeforeBackup)));
        Assert.Throws<InvalidDataException>(() => Inspect(lease, new(NativePrivateApplyStoreKind.MediaFavorites,
            fence.Before, fence.ExpectedAfter)));
        Assert.Equal(before, Inventory(temp.Path));
        lease.Dispose();
        Assert.Throws<ObjectDisposedException>(() => Inspect(lease, fence));
        Assert.Equal(before, Inventory(temp.Path));
    }
}
