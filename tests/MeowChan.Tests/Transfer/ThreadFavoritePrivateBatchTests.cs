using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MeowChan.Favorites;
using MeowChan.Persistence;

namespace MeowChan.Tests.Transfer;

public sealed class ThreadFavoritePrivateBatchTests
{
    private static ResourceId Thread(string id = "100") => new("synthetic", "b", id);
    private static readonly Guid Operation = Guid.Parse("fe11861d-44d1-40db-b005-bb1ce9df3937");
    private static readonly string PlanHash = new('a', 64);
    private const string FileName = "thread-favorites.json";

    [Fact]
    public void EmptyAndEqualBatchesDoNotWriteBumpRevisionOrInventAMarker()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        ThreadFavoriteStore store = new(temp.Path, lease);
        var empty = store.PrepareAdditiveBatch(Operation, PlanHash, []);
        Assert.False(empty.HasChanges);
        Assert.Equal(PrivateStoreBatchInspection.NoChange, store.CommitPreparedBatch(empty));
        Assert.False(File.Exists(Path.Combine(temp.Path, FileName)));
        store.Set(Thread(), true);
        byte[] before = File.ReadAllBytes(Path.Combine(temp.Path, FileName));
        var same = store.PrepareAdditiveBatch(Operation, PlanHash, [Thread()]);
        Assert.Equal(0, same.Additions);
        Assert.Equal(PrivateStoreBatchInspection.NoChange, store.InspectPreparedBatch(same));
        Assert.Equal(PrivateStoreBatchInspection.NoChange, store.CommitPreparedBatch(same));
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(temp.Path, FileName)));
    }

    [Fact]
    public void PreparationIsDeterministicAndOnlyPublicationUpgradesLegacySchema()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        ThreadFavoriteStore store = new(temp.Path, lease);
        store.Set(Thread("old"), true);
        string path = Path.Combine(temp.Path, FileName);
        byte[] before = File.ReadAllBytes(path);
        var first = store.PrepareAdditiveBatch(Operation, PlanHash, [Thread("z"), Thread("a")]);
        var second = store.PrepareAdditiveBatch(Operation, PlanHash, [Thread("a"), Thread("z")]);
        Assert.Equal(first.CopyAfterBytes(), second.CopyAfterBytes());
        Assert.Equal(first.Fence.ExpectedAfter.Sha256, second.Fence.ExpectedAfter.Sha256);
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Equal(PrivateStoreBatchInspection.ExactBefore, store.InspectPreparedBatch(first));
        Assert.Equal(PrivateStoreBatchInspection.ExactAfterWithMarker, store.CommitPreparedBatch(first));
        Assert.Equal(before, File.ReadAllBytes(path + ".bak"));
        byte[] after = File.ReadAllBytes(path);
        using JsonDocument json = JsonDocument.Parse(after);
        Assert.Equal(2, json.RootElement.GetProperty("Version").GetInt32());
        Assert.Equal(Operation, json.RootElement.GetProperty("InstanceId").GetGuid());
        Assert.Equal(Operation, json.RootElement.GetProperty("OperationMarker").GetProperty("OperationId").GetGuid());
        Assert.Equal(PlanHash, json.RootElement.GetProperty("OperationMarker").GetProperty("ContentPlanSha256").GetString());
        Assert.Equal(2, store.ReadSnapshot().Revision);
        Assert.Equal(new[] { Thread("old"), Thread("a"), Thread("z") }, store.ReadSnapshot().Threads);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(after)).ToLowerInvariant(), first.Fence.ExpectedAfter.Sha256);
        Assert.Equal(PrivateStoreBatchInspection.ExactAfterWithMarker, store.CommitPreparedBatch(first));
        Assert.Equal(after, File.ReadAllBytes(path));
        Assert.Equal(before, File.ReadAllBytes(path + ".bak"));
    }

    [Fact]
    public void NewPrimaryPublishesValuesAndEvidenceTogetherAndDefensiveCopyCannotChangeIt()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        ThreadFavoriteStore store = new(temp.Path, lease);
        var batch = store.PrepareAdditiveBatch(Operation, PlanHash, [Thread()]);
        byte[] copy = batch.CopyAfterBytes();
        byte[] original = copy.ToArray();
        copy.AsSpan().Fill(0);
        Assert.Equal(original, batch.CopyAfterBytes());
        Assert.False(batch.Fence.Before.Exists);
        Assert.Equal(1, batch.Fence.ExpectedAfter.Revision);
        Assert.Equal(PrivateStoreBatchInspection.ExactAfterWithMarker, store.CommitPreparedBatch(batch));
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(temp.Path, FileName)));
        Assert.False(File.Exists(Path.Combine(temp.Path, FileName) + ".bak"));
    }

    [Fact]
    public void PersistedMarkerBlocksOrdinaryWriterAndNewBatchAfterRestart()
    {
        using TempDirectory temp = new();
        string path = Path.Combine(temp.Path, FileName);
        byte[] after;
        using (ProfileLease lease = ProfileLease.Acquire(temp.Path))
        {
            ThreadFavoriteStore store = new(temp.Path, lease);
            store.CommitPreparedBatch(store.PrepareAdditiveBatch(Operation, PlanHash, [Thread()]));
            after = File.ReadAllBytes(path);
            Assert.Throws<InvalidDataException>(() => store.Set(Thread(), false));
            Assert.Throws<InvalidDataException>(() => store.Set(Thread(), true));
        }
        using ProfileLease reopened = ProfileLease.Acquire(temp.Path);
        ThreadFavoriteStore restarted = new(temp.Path, reopened);
        Assert.Single(restarted.ReadSnapshot().Threads);
        Assert.Throws<InvalidDataException>(() => restarted.Set(Thread("new"), true));
        Assert.Throws<InvalidDataException>(() => restarted.PrepareAdditiveBatch(Guid.NewGuid(), PlanHash, []));
        Assert.Equal(after, File.ReadAllBytes(path));
        Assert.False(File.Exists(path + ".bak"));
    }

    [Fact]
    public void DifferentStoreInstanceAndOtherProfileCannotUsePreparedBatch()
    {
        using TempDirectory a = new(); using TempDirectory b = new();
        using ProfileLease leaseA = ProfileLease.Acquire(a.Path); using ProfileLease leaseB = ProfileLease.Acquire(b.Path);
        ThreadFavoriteStore store = new(a.Path, leaseA), otherObject = new(a.Path, leaseA), otherProfile = new(b.Path, leaseB);
        var batch = store.PrepareAdditiveBatch(Operation, PlanHash, [Thread()]);
        foreach (var target in new[] { otherObject, otherProfile })
        {
            Assert.Throws<InvalidDataException>(() => target.InspectPreparedBatch(batch));
            Assert.Throws<InvalidDataException>(() => target.CommitPreparedBatch(batch));
        }
        Assert.False(File.Exists(Path.Combine(a.Path, FileName)));
        Assert.False(File.Exists(Path.Combine(b.Path, FileName)));
    }

    [Fact]
    public void InterveningOrdinaryWriteMakesPreparedBytesStaleWithoutClobber()
    {
        using TempDirectory temp = new(); using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        ThreadFavoriteStore store = new(temp.Path, lease);
        var batch = store.PrepareAdditiveBatch(Operation, PlanHash, [Thread()]);
        store.Set(Thread("other"), true);
        byte[] current = File.ReadAllBytes(Path.Combine(temp.Path, FileName));
        Assert.Equal(PrivateStoreBatchInspection.Drifted, store.InspectPreparedBatch(batch));
        Assert.Throws<InvalidDataException>(() => store.CommitPreparedBatch(batch));
        Assert.Equal(current, File.ReadAllBytes(Path.Combine(temp.Path, FileName)));
    }

    [Fact]
    public void BackupDriftAndMissingPrimaryWithBackupAreNotSafeBeforeOrAfter()
    {
        using TempDirectory temp = new(); using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        ThreadFavoriteStore store = new(temp.Path, lease);
        store.Set(Thread("old"), true);
        string path = Path.Combine(temp.Path, FileName);
        var batch = store.PrepareAdditiveBatch(Operation, PlanHash, [Thread()]);
        File.WriteAllBytes(path + ".bak", File.ReadAllBytes(path));
        Assert.Equal(PrivateStoreBatchInspection.Drifted, store.InspectPreparedBatch(batch));
        Assert.Throws<InvalidDataException>(() => store.CommitPreparedBatch(batch));
        File.Delete(path);
        Assert.Equal(PrivateStoreBatchInspection.Unreadable, store.InspectPreparedBatch(batch));
        Assert.Throws<InvalidDataException>(() => store.PrepareAdditiveBatch(Operation, PlanHash, []));
        Assert.False(File.Exists(path));
    }

    [Theory]
    [InlineData("{\"extra\":1}")]
    [InlineData("null")]
    [InlineData("{}")]
    public void AfterBytesWithoutValidMatchingMarkerAreNeverReportedAsPublished(string marker)
    {
        using TempDirectory temp = new(); using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        ThreadFavoriteStore store = new(temp.Path, lease);
        var batch = store.PrepareAdditiveBatch(Operation, PlanHash, [Thread()]);
        using JsonDocument prepared = JsonDocument.Parse(batch.CopyAfterBytes());
        var fields = prepared.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
        using JsonDocument invalid = JsonDocument.Parse(marker);
        fields["OperationMarker"] = invalid.RootElement.Clone();
        string path = Path.Combine(temp.Path, FileName);
        File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(fields));
        byte[] before = File.ReadAllBytes(path);
        Assert.NotEqual(PrivateStoreBatchInspection.ExactAfterWithMarker, store.InspectPreparedBatch(batch));
        Assert.Throws<InvalidDataException>(() => store.CommitPreparedBatch(batch));
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void CancellationDuplicateAndRevisionOverflowHaveNoPublication()
    {
        using TempDirectory temp = new(); using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        ThreadFavoriteStore store = new(temp.Path, lease);
        CancellationToken cancelled = new(true);
        Assert.Throws<OperationCanceledException>(() => store.PrepareAdditiveBatch(Operation, PlanHash, [Thread()], cancelled));
        Assert.Throws<InvalidDataException>(() => store.PrepareAdditiveBatch(Operation, PlanHash, [Thread(), Thread()]));
        var batch = store.PrepareAdditiveBatch(Operation, PlanHash, [Thread()]);
        Assert.Throws<OperationCanceledException>(() => store.CommitPreparedBatch(batch, cancelled));
        string path = Path.Combine(temp.Path, FileName);
        Assert.False(File.Exists(path));
        File.WriteAllText(path, "{\"Version\":1,\"Revision\":9223372036854775807,\"Threads\":[]}");
        byte[] before = File.ReadAllBytes(path);
        Assert.Throws<OverflowException>(() => store.PrepareAdditiveBatch(Operation, PlanHash, [Thread()]));
        Assert.Equal(before, File.ReadAllBytes(path));
    }
}
