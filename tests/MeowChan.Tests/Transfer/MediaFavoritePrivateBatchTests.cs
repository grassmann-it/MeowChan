using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowChan.Favorites;
using MeowChan.Persistence;
using MeowChan.Transfer;

namespace MeowChan.Tests.Transfer;

// Synthetic profile fixtures only. These exercise an internal store primitive,
// not transfer admission, media resolution, journal recovery or consent.
public sealed class MediaFavoritePrivateBatchTests
{
    private static readonly Guid Operation = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly string PlanHash = new('a', 64);
    private static FavoriteEntry Entry(string media = "1", long length = 12, string? name = "synthetic.jpg")
        => new(new(new("synthetic-source", "synthetic-board", "100"), media, "original", new('b', 64)), length, name);
    private static string FilePath(TempDirectory temp) => Path.Combine(temp.Path, "media-favorites.json");
    private static byte[] Legacy(params FavoriteEntry[] entries) => JsonSerializer.SerializeToUtf8Bytes(new { Version = 1, Entries = entries });
    private static PrivateStoreBatch Prepare(FavoriteStore store, params FavoriteEntry[] entries)
        => store.PrepareAdditiveBatch(Operation, PlanHash, entries);

    [Fact]
    public void MissingPrepareIsReadOnlyAndCommitPublishesVersionThreeWithExactMarker()
    {
        using TempDirectory temp = new(); using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        FavoriteStore store = new(temp.Path, lease);
        string path = FilePath(temp);
        PrivateStoreBatch batch = Prepare(store, Entry());
        Assert.False(File.Exists(path)); Assert.False(File.Exists(path + ".bak"));
        Assert.False(batch.Fence.Before.Exists); Assert.False(batch.Fence.BeforeBackup!.Exists);
        Assert.Equal(1, batch.Additions); Assert.True(batch.HasChanges);
        Assert.Equal(PrivateStoreBatchInspection.ExactBefore, store.InspectPreparedBatch(batch));
        Assert.Equal(PrivateStoreBatchInspection.ExactAfterWithMarker, store.CommitPreparedBatch(batch));
        byte[] committed = File.ReadAllBytes(path);
        Assert.Equal(batch.CopyAfterBytes(), committed);
        Assert.Equal(committed.LongLength, batch.Fence.ExpectedAfter.ByteLength);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(committed)).ToLowerInvariant(), batch.Fence.ExpectedAfter.Sha256);
        using JsonDocument json = JsonDocument.Parse(committed);
        JsonElement root = json.RootElement;
        Assert.Equal(3, root.GetProperty("Version").GetInt32());
        Assert.Equal(Operation, root.GetProperty("InstanceId").GetGuid());
        Assert.Equal(1, root.GetProperty("Revision").GetInt64());
        JsonElement marker = root.GetProperty("OperationMarker");
        Assert.Equal(Operation, marker.GetProperty("OperationId").GetGuid());
        Assert.Equal(PlanHash, marker.GetProperty("ContentPlanSha256").GetString());
        Assert.Equal((int)NativePrivateApplyStoreKind.MediaFavorites, marker.GetProperty("StoreKind").GetInt32());
        Assert.Equal(new[] { "OperationId", "ContentPlanSha256", "StoreKind" }, marker.EnumerateObject().Select(p => p.Name));
        Assert.DoesNotContain("RequestSha256", Encoding.UTF8.GetString(committed), StringComparison.Ordinal);
        Assert.Equal(Entry(), Assert.Single(store.List()));
        FavoriteStore.ValidateDocumentBytes(committed);
        Assert.False(File.Exists(path + ".bak"));
        Assert.Equal(PrivateStoreBatchInspection.ExactAfterWithMarker, store.CommitPreparedBatch(batch));
        Assert.Equal(committed, File.ReadAllBytes(path)); Assert.False(File.Exists(path + ".bak"));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void EmptyAndExistingOnlyAreNoWriteNoRevisionNoMarker(bool existing)
    {
        using TempDirectory temp = new(); using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        string path = FilePath(temp);
        byte[]? before = existing ? Legacy(Entry()) : null;
        if (before is not null) File.WriteAllBytes(path, before);
        FavoriteStore store = new(temp.Path, lease);
        PrivateStoreBatch batch = existing ? Prepare(store, Entry(length: 999, name: "different incoming.jpg")) : Prepare(store);
        Assert.False(batch.HasChanges); Assert.Equal(0, batch.Additions); Assert.Empty(batch.CopyAfterBytes());
        Assert.True(PrivateStoreBatchFiles.Same(batch.Fence.Before, batch.Fence.ExpectedAfter));
        Assert.Equal(PrivateStoreBatchInspection.NoChange, store.InspectPreparedBatch(batch));
        Assert.Equal(PrivateStoreBatchInspection.NoChange, store.CommitPreparedBatch(batch));
        if (before is not null)
        {
            Assert.Equal(before, File.ReadAllBytes(path)); Assert.Equal(Entry(), Assert.Single(store.List()));
        }
        else Assert.False(File.Exists(path));
        Assert.False(File.Exists(path + ".bak"));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void AdditiveUnionPreservesExistingMetadataInstanceAndExactRotatedBackup(bool versionTwo)
    {
        using TempDirectory temp = new(); using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        FavoriteStore store = new(temp.Path, lease); string path = FilePath(temp);
        if (versionTwo) store.SetFavorite(Entry(), true);
        else File.WriteAllBytes(path, Legacy(Entry()));
        byte[] before = File.ReadAllBytes(path);
        FavoriteStoreSnapshot snapshot = store.ReadTransferSnapshot();
        var incoming = new List<FavoriteEntry> { Entry("z"), Entry(length: 999, name: "do not overwrite.jpg"), Entry("a") };
        PrivateStoreBatch batch = store.PrepareAdditiveBatch(Operation, PlanHash, incoming);
        incoming.Clear(); // The minted bytes do not alias the caller's list.
        Assert.Equal(2, batch.Additions); Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Equal(snapshot.Revision + 1, batch.Fence.ExpectedAfter.Revision);
        Assert.Equal(snapshot.InstanceId ?? Operation, batch.Fence.ExpectedAfter.Instance);
        byte[] copy = batch.CopyAfterBytes(); copy[0] = 0;
        Assert.NotEqual(copy, batch.CopyAfterBytes());
        Assert.Equal(PrivateStoreBatchInspection.ExactAfterWithMarker, store.CommitPreparedBatch(batch));
        Assert.Equal(before, File.ReadAllBytes(path + ".bak"));
        Assert.Equal(new[] { Entry(), Entry("a"), Entry("z") }, store.List());
        Assert.Equal(PrivateStoreBatchInspection.ExactAfterWithMarker, store.InspectPreparedBatch(batch));
    }

    [Fact]
    public void CanonicalAppendIsOrdinalPermutationAndCultureIndependent()
    {
        using TempDirectory temp = new(); using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        FavoriteStore store = new(temp.Path, lease);
        FavoriteEntry[] input = [Entry("I"), Entry("i"), Entry("ı"), Entry("İ"), Entry("2"), Entry("10")];
        byte[] expected = Prepare(store, input).CopyAfterBytes();
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            Assert.Equal(expected, Prepare(store, input.Reverse().ToArray()).CopyAfterBytes());
        }
        finally { CultureInfo.CurrentCulture = previous; }
        Assert.False(File.Exists(FilePath(temp)));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void DuplicateKeysFailEvenWhenExistingAndErrorsCarryNoPrivateValues(bool existing)
    {
        using TempDirectory temp = new(); using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        FavoriteStore store = new(temp.Path, lease); string path = FilePath(temp);
        if (existing) File.WriteAllBytes(path, Legacy(Entry()));
        InvalidDataException error = Assert.Throws<InvalidDataException>(() => Prepare(store,
            Entry(name: "private-value.jpg"), Entry(length: 42)));
        Assert.Null(error.InnerException); Assert.DoesNotContain("private-value", error.ToString());
        Assert.DoesNotContain(temp.Path, error.ToString());
        Assert.False(File.Exists(path + ".bak"));
        if (existing) Assert.Equal(Legacy(Entry()), File.ReadAllBytes(path)); else Assert.False(File.Exists(path));
    }

    [Fact]
    public void InvalidIdentityLengthUnicodeAndMarkerFailBeforeAnyWrite()
    {
        using TempDirectory temp = new(); using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        FavoriteStore store = new(temp.Path, lease);
        foreach (FavoriteEntry invalid in new FavoriteEntry[] {
            null!, Entry(length: -1), Entry() with { Key = null! },
            Entry() with { Key = Entry().Key with { Thread = null! } },
            Entry() with { Key = Entry().Key with { Sha256 = new('B', 64) } },
            Entry() with { Key = Entry().Key with { Variant = "" } },
            Entry(name: "private-\uD800"), Entry("private-\uDC00") })
        {
            InvalidDataException error = Assert.Throws<InvalidDataException>(() => Prepare(store, invalid));
            Assert.Null(error.InnerException); Assert.DoesNotContain("private-", error.ToString());
        }
        Assert.Throws<InvalidDataException>(() => store.PrepareAdditiveBatch(Guid.Empty, PlanHash, [Entry()]));
        Assert.Throws<InvalidDataException>(() => store.PrepareAdditiveBatch(Operation, new('A', 64), [Entry()]));
        Assert.Throws<InvalidDataException>(() => store.PrepareAdditiveBatch(Operation, PlanHash, null!));
        Assert.False(File.Exists(FilePath(temp)));
    }

    [Fact]
    public void CumulativeValueBudgetAndEscapedOutputBudgetAreCheckedBeforePublication()
    {
        using TempDirectory temp = new(); using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        FavoriteStore store = new(temp.Path, lease);
        string half = new('a', FavoriteStore.MaximumBytes / 2);
        Assert.Throws<InvalidDataException>(() => Prepare(store, Entry("1", name: half), Entry("2", name: half)));
        Assert.Throws<InvalidDataException>(() => Prepare(store, Entry(name: new('a', FavoriteStore.MaximumBytes + 1))));
        // Raw values fit, but JSON escaping does not. The bounded serializer must fail.
        Assert.Throws<InvalidDataException>(() => Prepare(store, Entry(name: new('\n', FavoriteStore.MaximumBytes / 2))));
        Assert.False(File.Exists(FilePath(temp))); Assert.False(File.Exists(FilePath(temp) + ".bak"));
    }

    [Fact]
    public void InputCountAndRevisionLimitsArePrepublicationAndExistingMaxRevisionIsNoop()
    {
        using TempDirectory temp = new(); using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        FavoriteStore store = new(temp.Path, lease); string path = FilePath(temp);
        FavoriteEntry[] tooMany = Enumerable.Repeat(Entry(), FavoriteStore.MaximumEntries + 1).ToArray();
        Assert.Throws<InvalidDataException>(() => Prepare(store, tooMany));
        Assert.False(File.Exists(path)); Assert.False(File.Exists(path + ".bak"));
        byte[] maxRevision = JsonSerializer.SerializeToUtf8Bytes(new { Version = 2, Entries = new[] { Entry() }, InstanceId = Operation, Revision = long.MaxValue });
        File.WriteAllBytes(path, maxRevision);
        Assert.False(Prepare(store, Entry()).HasChanges);
        Assert.Throws<InvalidDataException>(() => Prepare(store, Entry("extra")));
        Assert.Equal(maxRevision, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ExactBeforeRequiresUnchangedPrimaryAndBackupBytes(bool backup)
    {
        using TempDirectory temp = new(); using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        FavoriteStore store = new(temp.Path, lease); string path = FilePath(temp);
        File.WriteAllBytes(path, Legacy(Entry()));
        File.WriteAllBytes(path + ".bak", Legacy());
        PrivateStoreBatch batch = Prepare(store, Entry("new"));
        string altered = path + (backup ? ".bak" : "");
        File.AppendAllText(altered, " "); // Same parsed values are NOT the same byte fence.
        byte[] changed = File.ReadAllBytes(altered);
        Assert.Equal(PrivateStoreBatchInspection.Drifted, store.InspectPreparedBatch(batch));
        Assert.Throws<InvalidDataException>(() => store.CommitPreparedBatch(batch));
        Assert.Equal(changed, File.ReadAllBytes(altered));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void MissingPrimaryWithBackupAndMalformedCandidatesAreUnreadable(bool malformed)
    {
        using TempDirectory temp = new(); using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        FavoriteStore store = new(temp.Path, lease); string path = FilePath(temp);
        PrivateStoreBatch batch = Prepare(store, Entry());
        File.WriteAllBytes(path + ".bak", malformed ? Encoding.UTF8.GetBytes("private-malformed") : Legacy());
        Assert.Equal(PrivateStoreBatchInspection.Unreadable, store.InspectPreparedBatch(batch));
        Assert.Throws<InvalidDataException>(() => store.CommitPreparedBatch(batch));
        Assert.Throws<InvalidDataException>(() => Prepare(store, Entry()));
        Assert.False(File.Exists(path));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void AfterReadbackAlsoRequiresExactRotatedBackup(bool malformed)
    {
        using TempDirectory temp = new(); using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        FavoriteStore store = new(temp.Path, lease); string path = FilePath(temp);
        File.WriteAllBytes(path, Legacy(Entry()));
        PrivateStoreBatch batch = Prepare(store, Entry("new"));
        store.CommitPreparedBatch(batch); byte[] after = File.ReadAllBytes(path);
        File.WriteAllBytes(path + ".bak", malformed ? Encoding.UTF8.GetBytes("{") : Legacy());
        Assert.Equal(malformed ? PrivateStoreBatchInspection.Unreadable : PrivateStoreBatchInspection.Drifted,
            store.InspectPreparedBatch(batch));
        Assert.Throws<InvalidDataException>(() => store.CommitPreparedBatch(batch));
        Assert.Equal(after, File.ReadAllBytes(path));
    }

    [Fact]
    public void MarkedPrimaryBlocksAllOrdinaryNoopAndCleanupWritersAlsoAfterRestart()
    {
        using TempDirectory temp = new(); string path = FilePath(temp);
        byte[] marked;
        using (ProfileLease lease = ProfileLease.Acquire(temp.Path))
        {
            FavoriteStore store = new(temp.Path, lease);
            store.CommitPreparedBatch(Prepare(store, Entry())); marked = File.ReadAllBytes(path);
        }
        using ProfileLease restarted = ProfileLease.Acquire(temp.Path);
        FavoriteStore next = new(temp.Path, restarted);
        Assert.Equal(Entry(), Assert.Single(next.List()));
        Assert.Throws<InvalidDataException>(() => next.SetFavorite(Entry(), true));
        Assert.Throws<InvalidDataException>(() => next.SetFavorite(Entry("missing"), false));
        Assert.Throws<InvalidDataException>(() => next.SetFavorite(Entry(), false));
        Assert.Throws<InvalidDataException>(() => next.RemoveThreads([]));
        Assert.Throws<InvalidDataException>(() => next.RemoveThreads([Entry().Key.Thread]));
        Assert.Throws<InvalidDataException>(() => Prepare(next, Entry()));
        Assert.Equal(marked, File.ReadAllBytes(path)); Assert.False(File.Exists(path + ".bak"));
    }

    [Fact]
    public void BatchIsStoreInstanceBoundAndCannotBeForgedUsingPublicStoreObject()
    {
        using TempDirectory temp = new(); using TempDirectory other = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path); using ProfileLease otherLease = ProfileLease.Acquire(other.Path);
        FavoriteStore store = new(temp.Path, lease);
        PrivateStoreBatch batch = Prepare(store, Entry());
        foreach (FavoriteStore wrong in new[] { new FavoriteStore(temp.Path, lease), new FavoriteStore(other.Path, otherLease) })
        {
            Assert.Throws<InvalidDataException>(() => wrong.InspectPreparedBatch(batch));
            Assert.Throws<InvalidDataException>(() => wrong.CommitPreparedBatch(batch));
        }
        var forged = new PrivateStoreBatch(store, batch.Marker, batch.Fence, batch.CopyAfterBytes(), batch.Additions);
        Assert.Throws<InvalidDataException>(() => store.CommitPreparedBatch(forged));
        Assert.Throws<InvalidDataException>(() => store.InspectPreparedBatch(forged));
        Assert.Throws<InvalidDataException>(() => store.InspectPreparedBatch(null!));
        Assert.False(File.Exists(FilePath(temp)));
    }

    [Fact]
    public void CancelledPreparationAndCommitLeaveBeforeBytesAndAllowRetry()
    {
        using TempDirectory temp = new(); using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        FavoriteStore store = new(temp.Path, lease); string path = FilePath(temp);
        File.WriteAllBytes(path, Legacy(Entry())); byte[] before = File.ReadAllBytes(path);
        CancellationToken cancelled = new(true);
        Assert.Throws<OperationCanceledException>(() => store.PrepareAdditiveBatch(Operation, PlanHash, [Entry("new")], cancelled));
        PrivateStoreBatch batch = Prepare(store, Entry("new"));
        Assert.Throws<OperationCanceledException>(() => store.CommitPreparedBatch(batch, cancelled));
        Assert.Equal(before, File.ReadAllBytes(path)); Assert.False(File.Exists(path + ".bak"));
        Assert.Equal(PrivateStoreBatchInspection.ExactAfterWithMarker, store.CommitPreparedBatch(batch));
        Assert.Empty(Directory.GetFiles(temp.Path, ".media-favorites.json.*.tmp"));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void SameEntriesWithoutExactAfterMarkerAreNeverPublicationProof(bool missingMarker)
    {
        using TempDirectory temp = new(); using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        FavoriteStore store = new(temp.Path, lease); string path = FilePath(temp);
        PrivateStoreBatch batch = Prepare(store, Entry());
        JsonObject after = JsonNode.Parse(batch.CopyAfterBytes())!.AsObject();
        if (missingMarker) after.Remove("OperationMarker");
        else after["OperationMarker"]!["OperationId"] = Guid.NewGuid().ToString("D");
        File.WriteAllText(path, after.ToJsonString()); byte[] changed = File.ReadAllBytes(path);
        Assert.Equal(missingMarker ? PrivateStoreBatchInspection.Unreadable : PrivateStoreBatchInspection.Drifted,
            store.InspectPreparedBatch(batch));
        Assert.Throws<InvalidDataException>(() => store.CommitPreparedBatch(batch));
        Assert.Equal(changed, File.ReadAllBytes(path)); Assert.False(File.Exists(path + ".bak"));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void NonregularPrimaryAndBackupNeverBecomeEmpty(bool backup)
    {
        using TempDirectory temp = new(); using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        FavoriteStore store = new(temp.Path, lease); string path = FilePath(temp);
        PrivateStoreBatch batch = Prepare(store, Entry());
        Directory.CreateDirectory(path + (backup ? ".bak" : ""));
        Assert.Equal(PrivateStoreBatchInspection.Unreadable, store.InspectPreparedBatch(batch));
        Assert.Throws<InvalidDataException>(() => store.CommitPreparedBatch(batch));
        Assert.Throws<InvalidDataException>(() => Prepare(store, Entry()));
    }

    [Fact]
    public void DisposedLeaseRejectsPreparationInspectionAndCommit()
    {
        using TempDirectory temp = new(); ProfileLease lease = ProfileLease.Acquire(temp.Path);
        FavoriteStore store = new(temp.Path, lease); PrivateStoreBatch batch = Prepare(store, Entry());
        lease.Dispose();
        Assert.Throws<ObjectDisposedException>(() => Prepare(store, Entry()));
        Assert.Throws<ObjectDisposedException>(() => store.InspectPreparedBatch(batch));
        Assert.Throws<ObjectDisposedException>(() => store.CommitPreparedBatch(batch));
        Assert.False(File.Exists(FilePath(temp)));
    }

    public static IEnumerable<object[]> BadMarkedDocuments()
    {
        JsonObject valid = new()
        {
            ["Version"] = 3, ["Entries"] = new JsonArray(), ["InstanceId"] = Operation.ToString("D"), ["Revision"] = 1,
            ["OperationMarker"] = new JsonObject { ["OperationId"] = Operation.ToString("D"), ["ContentPlanSha256"] = PlanHash,
                ["StoreKind"] = (int)NativePrivateApplyStoreKind.MediaFavorites }
        };
        foreach (string field in new[] { "OperationMarker", "InstanceId", "Revision" })
        {
            var missing = (JsonObject)valid.DeepClone(); missing.Remove(field); yield return [missing.ToJsonString()];
            var nil = (JsonObject)valid.DeepClone(); nil[field] = null; yield return [nil.ToJsonString()];
        }
        foreach (string field in new[] { "OperationId", "ContentPlanSha256", "StoreKind" })
        {
            var missing = (JsonObject)valid.DeepClone(); missing["OperationMarker"]!.AsObject().Remove(field); yield return [missing.ToJsonString()];
        }
        foreach (int kind in new[] { 0, 2, 99 })
        {
            var wrong = (JsonObject)valid.DeepClone(); wrong["OperationMarker"]!["StoreKind"] = kind; yield return [wrong.ToJsonString()];
        }
        var emptyOperation = (JsonObject)valid.DeepClone(); emptyOperation["OperationMarker"]!["OperationId"] = Guid.Empty.ToString(); yield return [emptyOperation.ToJsonString()];
        var badHash = (JsonObject)valid.DeepClone(); badHash["OperationMarker"]!["ContentPlanSha256"] = new string('A', 64); yield return [badHash.ToJsonString()];
        var extra = (JsonObject)valid.DeepClone(); extra["OperationMarker"]!["RequestSha256"] = PlanHash; yield return [extra.ToJsonString()];
        foreach (int version in new[] { 1, 2 })
        {
            var wrong = (JsonObject)valid.DeepClone(); wrong["Version"] = version; yield return [wrong.ToJsonString()];
        }
        string raw = valid.ToJsonString();
        yield return [raw.Replace("\"StoreKind\":1", "\"StoreKind\":1,\"StoreKind\":1", StringComparison.Ordinal)];
        yield return [raw.Replace("\"OperationMarker\":", "\"OperationMarker\":null,\"OperationMarker\":", StringComparison.Ordinal)];
    }

    [Theory]
    [MemberData(nameof(BadMarkedDocuments))]
    public void StrictSchemaThreeRejectsMalformedMissingDuplicateAndWrongStoreMarkers(string raw)
    {
        using TempDirectory temp = new(); using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        FavoriteStore store = new(temp.Path, lease); string path = FilePath(temp);
        File.WriteAllText(path, raw); byte[] before = File.ReadAllBytes(path);
        Assert.Throws<InvalidDataException>(() => FavoriteStore.ValidateDocumentBytes(before));
        Assert.Throws<InvalidDataException>(() => store.List());
        Assert.Throws<InvalidDataException>(() => store.SetFavorite(Entry(), true));
        Assert.Throws<InvalidDataException>(() => Prepare(store, Entry()));
        Assert.Equal(before, File.ReadAllBytes(path)); Assert.False(File.Exists(path + ".bak"));
    }

    [Fact]
    public void UnmarkedOrdinaryVersionTwoBytesKeepTheirEstablishedShape()
    {
        using TempDirectory temp = new(); using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        FavoriteStore store = new(temp.Path, lease); string path = FilePath(temp);
        Assert.True(store.SetFavorite(Entry(), true));
        byte[] bytes = File.ReadAllBytes(path);
        using JsonDocument json = JsonDocument.Parse(bytes);
        Assert.Equal(new[] { "Version", "Entries", "InstanceId", "Revision" }, json.RootElement.EnumerateObject().Select(p => p.Name));
        Guid instance = json.RootElement.GetProperty("InstanceId").GetGuid();
        Assert.Equal(JsonSerializer.SerializeToUtf8Bytes(new { Version = 2, Entries = new[] { Entry() }, InstanceId = instance, Revision = 1L }), bytes);
        Assert.False(store.SetFavorite(Entry(), true)); Assert.Equal(bytes, File.ReadAllBytes(path));
    }
}
