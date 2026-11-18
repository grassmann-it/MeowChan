using MeowChan.Downloader;
using MeowChan.Persistence;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace MeowChan.Tests;

public sealed class WorkStateUnreadableRowTests
{
    private const string First = "https://stub.test/g/thread/123";
    private const string Broken = "https://stub.test/g/thread/124";
    private const string Last = "https://stub.test/g/thread/125";

    [Theory]
    [InlineData("json")]
    [InlineData("intent")]
    [InlineData("key")]
    public async Task UnreadableResourceRowIsReportedWhileOtherWatchesRestore(string corruption)
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        WorkStateStore work = new(directory.Path, lease);
        foreach (string url in new[] { First, Broken, Last })
            work.CommitIntent(new WorkResource { Url = url, OutputRoot = directory.Path,
                Generation = 3, Intent = WorkIntent.Watching, IsThread = true });
        WorkResource broken = work.LoadResources().Single(resource => resource.Url == Broken);
        string payload = corruption switch
        {
            "json" => "{\"url\":",
            "intent" => Serialize(broken with { Intent = (WorkIntent)99 }),
            _ => Serialize(broken with { Url = "https://stub.test/g/thread/999" })
        };
        SetPayload(directory.Path, "UPDATE work_resources SET payload=$payload WHERE url=$url", Broken, payload);
        SessionStore sessions = new(directory.Path, lease);
        await sessions.SaveAsync(new SessionSnapshot { ThreadUrls = [First, Broken, Last] });

        using StubImageBoard provider = new();
        DownloadQueue queue = new(1, 1L << 30);
        queue.SetPaused(true);
        await using Registry registry = new([provider], queue, directory.Path,
            TimeSpan.FromMinutes(1), 60, sessions, workStore: work);
        await registry.StartAsync(lease);

        Assert.Equal([First, Last], registry.Threads.Select(thread => thread.ThreadUri.AbsoluteUri).Order().ToArray());
        Assert.Contains(registry.GetSnapshot().Warnings, warning => warning.Contains(Broken, StringComparison.Ordinal));
        // Nothing is deleted or rewritten: the row, its generation history and the projection remain.
        Assert.Equal(payload, ReadPayload(directory.Path, Broken));
        Assert.Contains(Broken, (await sessions.LoadAsync()).Snapshot.ThreadUrls);
        // Authority readers such as archive cleanup still refuse to act on an unreadable journal.
        Assert.ThrowsAny<Exception>(() => work.LoadResources());
        // A later admission cannot start over at an old generation and inherit stale media rows.
        Assert.ThrowsAny<Exception>(() => work.CommitIntent(broken with { Generation = 1 }));
        Assert.Equal(payload, ReadPayload(directory.Path, Broken));
    }

    [Fact]
    public async Task UnreadableMediaRowIsReportedWhileOtherWatchesRestore()
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        WorkStateStore work = new(directory.Path, lease);
        foreach (string url in new[] { First, Broken, Last })
        {
            work.CommitIntent(new WorkResource { Url = url, OutputRoot = directory.Path,
                Generation = 1, Intent = WorkIntent.Watching, IsThread = true });
            work.CommitMedia(new MediaWork { ResourceUrl = url, Generation = 1, MediaIdentifier = "media",
                DestinationPath = Path.Combine(directory.Path, url[^3..] + ".bin"), DownloadUrl = "https://stub.test/" + url[^3..] + ".bin",
                Status = MediaWorkStatus.Succeeded });
        }
        SetPayload(directory.Path, "UPDATE work_media SET payload=$payload WHERE resource_url=$url", Broken, "{\"status\":");

        using StubImageBoard provider = new();
        DownloadQueue queue = new(1, 1L << 30);
        queue.SetPaused(true);
        await using Registry registry = new([provider], queue, directory.Path,
            TimeSpan.FromMinutes(1), 60, workStore: work);
        await registry.StartAsync(lease);

        string[] restored = registry.Threads.Select(thread => thread.ThreadUri.AbsoluteUri).ToArray();
        Assert.Contains(First, restored);
        Assert.Contains(Last, restored);
        Assert.DoesNotContain(Broken, restored);
        Assert.Contains(registry.GetSnapshot().Warnings, warning => warning.Contains(Broken, StringComparison.Ordinal));
        Assert.Equal([First, Last], registry.GetSnapshot().Threads.Where(thread => thread.Work != null)
            .Select(thread => thread.Url).Order().ToArray());
        Assert.Throws<JsonException>(() => work.LoadMedia(Broken, 1));
    }

    [Fact]
    public void ExistingJournalGainsSchemaVersionWithoutDataChange()
    {
        using TempDirectory directory = new();
        using (ProfileLease seed = ProfileLease.Acquire(directory.Path))
        {
            WorkStateStore store = new(directory.Path, seed);
            store.CommitIntent(new WorkResource { Url = First, OutputRoot = directory.Path,
                Generation = 2, Intent = WorkIntent.Completed, IsThread = true });
            store.CommitMedia(new MediaWork { ResourceUrl = First, Generation = 2, MediaIdentifier = "media",
                DestinationPath = Path.Combine(directory.Path, "media.bin"), DownloadUrl = "https://stub.test/media.bin" });
        }
        // Journals written before the marker existed carry user_version 0.
        Execute(directory.Path, "PRAGMA user_version=0");
        string[] before = Rows(directory.Path);

        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        _ = new WorkStateStore(directory.Path, lease);

        Assert.Equal(1L, Scalar(directory.Path, "PRAGMA user_version"));
        Assert.Equal(before, Rows(directory.Path));
        _ = new WorkStateStore(directory.Path, lease);
        Assert.Equal(1L, Scalar(directory.Path, "PRAGMA user_version"));
    }

    [Fact]
    public void NewerJournalSchemaFailsWithoutChangingTheFile()
    {
        using TempDirectory directory = new();
        string path = Path.Combine(directory.Path, "work-state.sqlite");
        using (ProfileLease seed = ProfileLease.Acquire(directory.Path))
            new WorkStateStore(directory.Path, seed).CommitIntent(new WorkResource { Url = First,
                OutputRoot = directory.Path, Generation = 1, Intent = WorkIntent.Watching, IsThread = true });
        Execute(directory.Path, "PRAGMA user_version=2");
        byte[] bytes = File.ReadAllBytes(path);

        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        Assert.Throws<InvalidDataException>(() => new WorkStateStore(directory.Path, lease));

        Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    private static SqliteConnection Connect(string directory)
    {
        SqliteConnection db = new(new SqliteConnectionStringBuilder
            { DataSource = Path.Combine(directory, "work-state.sqlite"), Pooling = false }.ToString());
        db.Open();
        return db;
    }

    private static void SetPayload(string directory, string sql, string url, string payload)
    {
        using SqliteConnection db = Connect(directory);
        using SqliteCommand command = db.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$url", url);
        command.Parameters.AddWithValue("$payload", payload);
        Assert.Equal(1, command.ExecuteNonQuery());
    }

    private static string ReadPayload(string directory, string url)
    {
        using SqliteConnection db = Connect(directory);
        using SqliteCommand command = db.CreateCommand();
        command.CommandText = "SELECT payload FROM work_resources WHERE url=$url";
        command.Parameters.AddWithValue("$url", url);
        return (string)command.ExecuteScalar()!;
    }

    private static void Execute(string directory, string sql)
    {
        using SqliteConnection db = Connect(directory);
        using SqliteCommand command = db.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static long Scalar(string directory, string sql)
    {
        using SqliteConnection db = Connect(directory);
        using SqliteCommand command = db.CreateCommand();
        command.CommandText = sql;
        return (long)command.ExecuteScalar()!;
    }

    private static string[] Rows(string directory)
    {
        using SqliteConnection db = Connect(directory);
        using SqliteCommand command = db.CreateCommand();
        command.CommandText = "SELECT 'r|'||url||'|'||payload FROM work_resources UNION ALL " +
            "SELECT 'm|'||resource_url||'|'||generation||'|'||media_id||'|'||variant||'|'||target||'|'||payload FROM work_media " +
            "UNION ALL SELECT 's|'||type||'|'||name||'|'||COALESCE(sql,'') FROM sqlite_master ORDER BY 1";
        using SqliteDataReader reader = command.ExecuteReader();
        List<string> rows = new();
        while (reader.Read()) rows.Add(reader.GetString(0));
        return rows.ToArray();
    }
}
