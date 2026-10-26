using System.Text.Json;
using System.Text.Json.Nodes;
using MeowChan.Persistence;
using Microsoft.Data.Sqlite;

namespace MeowChan.Tests;

public sealed class MediaVariantWorkStateTests
{
    private const string Url = "https://stub.test/g/thread/1";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData("commit")]
    [InlineData("load")]
    public void HistoricalRawMd5DescriptorsRetainCommitAndLoadCompatibility(string operation)
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        WorkStateStore store = new(directory.Path, lease);
        store.CommitIntent(new WorkResource { Url = Url, Generation = 1, OutputRoot = directory.Path,
            Intent = WorkIntent.Watching, IsThread = true });
        MediaWork initial = new() { ResourceUrl = Url, Generation = 1, MediaIdentifier = "historical",
            DestinationPath = Path.Combine(directory.Path, "historical.bin"), DownloadUrl = "https://stub.test/historical.bin",
            Status = MediaWorkStatus.Waiting, Attempts = 2, LastAttemptAt = DateTimeOffset.UtcNow.AddMinutes(-2),
            DueAt = DateTimeOffset.UtcNow.AddMinutes(2) };
        MediaWork raw = initial with { ExpectedMd5 = "historical-opaque-checksum" };
        if (operation == "commit") Assert.True(store.CommitMedia(raw));
        else
        {
            Assert.True(store.CommitMedia(initial));
            using SqliteConnection db = Open(directory.Path);
            using SqliteCommand oldPayload = db.CreateCommand();
            oldPayload.CommandText = "UPDATE work_media SET payload=$payload";
            oldPayload.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(raw, Json));
            oldPayload.ExecuteNonQuery();
        }
        Assert.Equal(raw, Assert.Single(new WorkStateStore(directory.Path, lease).LoadMedia(Url, 1)));
    }

    [Fact]
    public void Sha1AndRawProviderSizeHintSurviveDurableRestartAndRejectInvalidMetadata()
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        WorkStateStore store = new(directory.Path, lease);
        store.CommitIntent(new WorkResource { Url = Url, Generation = 1, OutputRoot = directory.Path,
            Intent = WorkIntent.Watching, IsThread = true });
        MediaWork media = new() { ResourceUrl = Url, Generation = 1, MediaIdentifier = "m",
            DestinationPath = Path.Combine(directory.Path, "m.bin"), DownloadUrl = "https://stub.test/m.bin",
            ExpectedSha1 = Convert.ToBase64String(new byte[20]), ProviderSizeHint = new("24.50", "Кб") };
        Assert.True(store.CommitMedia(media));
        Assert.Equal(media, Assert.Single(new WorkStateStore(directory.Path, lease).LoadMedia(Url, 1)));
        Assert.Throws<InvalidDataException>(() => store.CommitMedia(media with { ExpectedSha1 = Convert.ToBase64String(new byte[16]) }));
        Assert.Throws<InvalidDataException>(() => store.CommitMedia(media with { ProviderSizeHint = new("-1", "Кб") }));
    }

    [Fact]
    public void LegacyPrimaryKeyUpgradeIsAtomicIdempotentAndPreservesOriginalPayloadAndDeadlines()
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        DateTimeOffset ended = DateTimeOffset.UtcNow.AddHours(-2);
        WorkResource owner = new() { Url = Url, Generation = 1, OutputRoot = directory.Path,
            Intent = WorkIntent.Watching, IsThread = true, EndObservedAt = ended, Source = ThreadSourceStateValue.NotFound };
        MediaWork original = new() { ResourceUrl = Url, Generation = 1, MediaIdentifier = "x--preview",
            DestinationPath = Path.Combine(directory.Path, "x--preview.jpg"), DownloadUrl = "https://stub.test/x.jpg",
            Status = MediaWorkStatus.Waiting, Attempts = 2, ConsecutiveNotFound = 2, HttpStatus = 404,
            LastAttemptAt = DateTimeOffset.UtcNow.AddMinutes(-2), DueAt = DateTimeOffset.UtcNow.AddMinutes(28) };
        JsonObject legacy = JsonSerializer.SerializeToNode(original, Json)!.AsObject();
        legacy.Remove("variant");
        string originalPayload = legacy.ToJsonString();
        using (SqliteConnection db = Open(directory.Path))
        using (SqliteCommand command = db.CreateCommand())
        {
            command.CommandText = "CREATE TABLE work_resources(url TEXT PRIMARY KEY,payload TEXT NOT NULL); " +
                "CREATE TABLE work_media(resource_url TEXT NOT NULL,generation INTEGER NOT NULL,media_id TEXT NOT NULL,target TEXT NOT NULL,payload TEXT NOT NULL,PRIMARY KEY(resource_url,generation,media_id,target)); " +
                "INSERT INTO work_resources VALUES($url,$owner); INSERT INTO work_media VALUES($url,1,$id,$target,$media);";
            command.Parameters.AddWithValue("$url", Url);
            command.Parameters.AddWithValue("$owner", JsonSerializer.Serialize(owner, Json));
            command.Parameters.AddWithValue("$id", original.MediaIdentifier);
            command.Parameters.AddWithValue("$target", original.DestinationPath);
            command.Parameters.AddWithValue("$media", originalPayload);
            command.ExecuteNonQuery();
        }
        WorkStateStore first = new(directory.Path, lease);
        Assert.Equal(original, Assert.Single(first.LoadMedia(Url, 1)));
        Assert.Equal(ended, Assert.Single(first.LoadResources()).EndObservedAt);
        WorkStateStore second = new(directory.Path, lease);
        Assert.Equal(original, Assert.Single(second.LoadMedia(Url, 1)));
        using (SqliteConnection db = Open(directory.Path))
        using (SqliteCommand command = db.CreateCommand())
        {
            command.CommandText = "SELECT payload FROM work_media";
            Assert.Equal(originalPayload, command.ExecuteScalar());
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name='work_media_legacy'";
            Assert.Equal(0L, command.ExecuteScalar());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RawMediaIdAndVariantAreSeparateDurableKeyPartsIncludingFormerFlatteningCollision(bool memory)
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        IWorkStateStore store = memory ? new MemoryWorkStateStore() : new WorkStateStore(directory.Path, lease);
        store.CommitIntent(new WorkResource { Url = Url, Generation = 1, OutputRoot = directory.Path,
            Intent = WorkIntent.Watching, IsThread = true });
        MediaWork first = new() { ResourceUrl = Url, Generation = 1, MediaIdentifier = "x--preview", Variant = "original",
            DestinationPath = Path.Combine(directory.Path, "same-target.bin"), DownloadUrl = "https://stub.test/one.bin" };
        MediaWork second = first with { MediaIdentifier = "x", Variant = "preview", DownloadUrl = "https://stub.test/two.bin" };
        MediaWork third = first with { Variant = "preview", DownloadUrl = "https://stub.test/three.bin" };
        Assert.True(store.CommitMedia(first)); Assert.True(store.CommitMedia(second)); Assert.True(store.CommitMedia(third));
        IReadOnlyList<MediaWork> restored = (memory ? store : new WorkStateStore(directory.Path, lease)).LoadMedia(Url, 1);
        Assert.Equal(3, restored.Count);
        Assert.Contains(first, restored); Assert.Contains(second, restored); Assert.Contains(third, restored);
        Assert.True(store.CommitMedia(second with { Status = MediaWorkStatus.Succeeded }));
        Assert.Equal(MediaWorkStatus.Ready, store.LoadMedia(Url, 1).Single(m => m.MediaIdentifier == first.MediaIdentifier && m.Variant == "original").Status);
    }

    [Fact]
    public void CorruptVariantCannotRetargetAnExistingDurableJob()
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        WorkStateStore store = new(directory.Path, lease);
        store.CommitIntent(new WorkResource { Url = Url, Generation = 1, OutputRoot = directory.Path,
            Intent = WorkIntent.Watching, IsThread = true });
        MediaWork media = new() { ResourceUrl = Url, Generation = 1, MediaIdentifier = "x", Variant = "preview",
            DestinationPath = Path.Combine(directory.Path, "x.bin"), DownloadUrl = "https://stub.test/x.bin" };
        store.CommitMedia(media);
        using SqliteConnection db = Open(directory.Path);
        using SqliteCommand command = db.CreateCommand();
        command.CommandText = "UPDATE work_media SET payload=$payload";
        command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(media with { Variant = "original" }, Json));
        command.ExecuteNonQuery();
        Assert.Throws<InvalidDataException>(() => store.LoadMedia(Url, 1));
        Assert.Throws<InvalidDataException>(() => store.CommitMedia(media with { Variant = "" }));
    }

    private static SqliteConnection Open(string directory)
    {
        SqliteConnection connection = new(new SqliteConnectionStringBuilder
            { DataSource = Path.Combine(directory, "work-state.sqlite"), Pooling = false }.ToString());
        connection.Open(); return connection;
    }
}
