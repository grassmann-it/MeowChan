using System.Text.Json;
using MeowChan.Favorites;
using MeowChan.Persistence;
using MeowChan.Transfer;

namespace MeowChan.Tests.Transfer;

public sealed class PrivateStoreBatchBoundaryTests
{
    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("{\"OperationId\":\"11111111-2222-3333-4444-555555555555\",\"ContentPlanSha256\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"StoreKind\":0,\"StoreKind\":0}")]
    [InlineData("{\"OperationId\":\"11111111-2222-3333-4444-555555555555\",\"ContentPlanSha256\":\"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA\",\"StoreKind\":0}")]
    [InlineData("{\"OperationId\":\"00000000-0000-0000-0000-000000000000\",\"ContentPlanSha256\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"StoreKind\":0}")]
    [InlineData("{\"OperationId\":\"11111111-2222-3333-4444-555555555555\",\"ContentPlanSha256\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"StoreKind\":2}")]
    public void MarkerParserRejectsMalformedOrWrongStoreEvidenceWithoutEcho(string raw)
    {
        using JsonDocument doc = JsonDocument.Parse(raw);
        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            PrivateStoreOperationMarker.Parse(doc.RootElement, NativePrivateApplyStoreKind.ThreadFavorites));
        Assert.Equal("The private store batch is invalid.", error.Message);
        Assert.Null(error.InnerException);
    }

    [Fact]
    public void PublicStoreReferenceCannotForgePrivatePreparedOwnerToken()
    {
        using TempDirectory temp = new(); using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        ThreadFavoriteStore store = new(temp.Path, lease);
        var genuine = store.PrepareAdditiveBatch(Guid.NewGuid(), new('a', 64), [new("synthetic", "b", "1")]);
        var forged = new PrivateStoreBatch(store, genuine.Marker, genuine.Fence, genuine.CopyAfterBytes(), genuine.Additions);
        Assert.Throws<InvalidDataException>(() => store.InspectPreparedBatch(forged));
        Assert.Throws<InvalidDataException>(() => store.CommitPreparedBatch(forged));
        Assert.False(File.Exists(Path.Combine(temp.Path, "thread-favorites.json")));
    }

    [Theory]
    [InlineData(1)] [InlineData(10)] [InlineData(100)]
    public void SerializerRejectsOutputBeyondItsLimitRatherThanAllocatingAWholeOversizeDocument(int cap)
    {
        Assert.Throws<InvalidDataException>(() =>
            PrivateStoreBatchFiles.SerializeBounded(new { Synthetic = new string('x', 2000) }, cap));
        byte[] output = PrivateStoreBatchFiles.SerializeBounded(new { Synthetic = "x" }, 100);
        Assert.True(output.Length <= 100);
        using JsonDocument parsed = JsonDocument.Parse(output);
        Assert.Equal("x", parsed.RootElement.GetProperty("Synthetic").GetString());
    }
    [Fact]
    public void BackupMarkerBlocksOrdinarySameValueCallsEvenWithUnmarkedPrimary()
    {
        using TempDirectory temp = new(); using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        ThreadFavoriteStore threads = new(temp.Path, lease);
        var threadBatch = threads.PrepareAdditiveBatch(Guid.NewGuid(), new('a', 64), [new("synthetic", "b", "1")]);
        threads.CommitPreparedBatch(threadBatch);
        string threadPath = Path.Combine(temp.Path, "thread-favorites.json");
        File.Copy(threadPath, threadPath + ".bak");
        File.WriteAllText(threadPath, "{\"Version\":1,\"Revision\":0,\"Threads\":[]}");
        Assert.Throws<InvalidDataException>(() => threads.Set(new("synthetic", "b", "1"), false));

        FavoriteStore media = new(temp.Path, lease);
        FavoriteEntry entry = new(new(new("synthetic", "b", "1"), "m", "original", new('b', 64)), 1, "synthetic.jpg");
        media.CommitPreparedBatch(media.PrepareAdditiveBatch(Guid.NewGuid(), new('a', 64), [entry]));
        string mediaPath = Path.Combine(temp.Path, "media-favorites.json");
        File.Copy(mediaPath, mediaPath + ".bak");
        File.WriteAllText(mediaPath, "{\"Version\":1,\"Entries\":[]}");
        Assert.Throws<InvalidDataException>(() => media.SetFavorite(entry, false));
        Assert.Throws<InvalidDataException>(() => media.RemoveThreads([]));
    }
}
