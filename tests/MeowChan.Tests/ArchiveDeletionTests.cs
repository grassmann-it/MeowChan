using Microsoft.Data.Sqlite;
using MeowChan.Archive;
using MeowChan.Interfaces;
using MeowChan.Persistence;

namespace MeowChan.Tests;

public sealed class ArchiveDeletionTests
{
    [Fact]
    public async Task PreviewProtectsDirectoriesSharedThroughCanonicalAliases()
    {
        using TempDirectory temp = new TempDirectory();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(temp.Path, lease);
        ResourceId first = new ResourceId("4chan", "g", "301");
        ResourceId kept = new ResourceId("4chan", "g", "302");
        string media = Path.Combine(temp.Path, "media");
        await catalog.CaptureAsync(Snapshot(first, media, "selected", "body"));
        await catalog.CaptureAsync(Snapshot(kept, Path.Combine(media, "..", "media") + Path.DirectorySeparatorChar, "retained", "body"));
        ArchiveDeletionPlan plan = await catalog.PrepareDeletionAsync(new[] { first });
        Assert.Equal(new[] { media }, plan.SharedMediaDirectories);
        Assert.Empty((await catalog.PrepareDeletionAsync(new[] { first, kept })).SharedMediaDirectories);
    }

    [Fact]
    public async Task DeletionRemovesDecisionRowsAndRollsBackWholeSelectionOnFailure()
    {
        using TempDirectory temp = new TempDirectory();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(temp.Path, lease);
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(temp.Path);
        ResourceId first = new ResourceId("4chan", "g", "201");
        ResourceId second = new ResourceId("4chan", "g", "202");
        await catalog.CaptureAsync(Snapshot(first, Path.Combine(temp.Path, "201"), "firstneedle", "firstbody"));
        await catalog.CaptureAsync(Snapshot(second, Path.Combine(temp.Path, "202"), "secondneedle", "secondbody"));
        using (SqliteConnection db = Open(catalog.DatabasePath))
        {
            using SqliteCommand setup = db.CreateCommand();
            setup.CommandText = """
                INSERT INTO media_decisions(source_id,board_id,thread_id,media_id,variant,download_uri,destination_path,selection_json,decided_at_utc)
                VALUES('4chan','g','201','file','original','https://cdn.test/file','file','{}','2026-10-21T00:00:00.0000000+00:00');
                CREATE TRIGGER fail_delete BEFORE DELETE ON archive_threads WHEN old.thread='202'
                BEGIN SELECT RAISE(ABORT,'synthetic deletion failure'); END;
                """;
            setup.ExecuteNonQuery();
        }
        await Assert.ThrowsAsync<SqliteException>(() => catalog.DeleteThreadsAsync(new[] { first, second }));
        Assert.NotNull(await reader.ReadThreadAsync(first));
        Assert.NotNull(await reader.ReadThreadAsync(second));
        using (SqliteConnection db = Open(catalog.DatabasePath))
        {
            Assert.Equal(1L, CountDecision(db, first));
            using SqliteCommand clear = db.CreateCommand();
            clear.CommandText = "DROP TRIGGER fail_delete";
            clear.ExecuteNonQuery();
        }
        await catalog.DeleteThreadsAsync(new[] { first, second });
        using SqliteConnection verify = Open(catalog.DatabasePath);
        Assert.Equal(0L, CountDecision(verify, first));
        Assert.Null(await reader.ReadThreadAsync(first));
        Assert.Null(await reader.ReadThreadAsync(second));
    }

    [Fact]
    public async Task MultiSelectRemovesOnlyChosenThreadsAndTheirIndexedData()
    {
        using TempDirectory temp = new TempDirectory();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(temp.Path, lease);
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(temp.Path);
        ResourceId first = new ResourceId("4chan", "g", "101");
        ResourceId second = new ResourceId("4chan", "g", "102");
        ResourceId kept = new ResourceId("4chan", "g", "103");
        string shared = Path.Combine(temp.Path, "shared");
        string firstOld = Path.Combine(temp.Path, "first-old");
        string secondMedia = Path.Combine(temp.Path, "second");
        await catalog.CaptureAsync(Snapshot(first, shared, "firsttitleneedle", "firstbodyneedle"));
        await catalog.RegisterThreadAsync(new ArchiveThreadRegistration(first, Url(first), "first", firstOld, true));
        await catalog.CaptureAsync(Snapshot(second, secondMedia, "secondtitleneedle", "secondbodyneedle"));
        await catalog.CaptureAsync(Snapshot(kept, shared, "kepttitleneedle",
            $"<a href='{Url(first)}#p1'>&gt;&gt;1</a> keptbodyneedle"));

        ArchiveDeletionPlan plan = await catalog.PrepareDeletionAsync(new[] { first, second, first });
        Assert.Equal(new[] { first, second }, plan.Threads.Select(item => item.Summary.Id));
        Assert.Equal(new[] { firstOld, shared }, plan.Threads[0].MediaDirectories);
        Assert.Equal(new[] { shared }, plan.SharedMediaDirectories);
        Assert.NotEmpty((await reader.GetBacklinksAsync(new ArchivePostId(first, "1"))).Items);

        IReadOnlyList<ArchiveDeletedThread> deleted = await catalog.DeleteThreadsAsync(new[] { first, second, first });
        Assert.Equal(new[] { first, second }, deleted.Select(item => item.Summary.Id));
        Assert.Null(await reader.ReadThreadAsync(first));
        Assert.Null(await reader.ReadThreadAsync(second));
        Assert.NotNull(await reader.ReadThreadAsync(kept));
        Assert.Empty((await reader.SearchThreadsAsync(new ArchiveThreadQuery("firsttitleneedle"))).Items);
        Assert.Empty((await reader.SearchThreadsAsync(new ArchiveThreadQuery("secondbodyneedle"))).Items);
        Assert.Single((await reader.SearchThreadsAsync(new ArchiveThreadQuery("keptbodyneedle"))).Items);
        Assert.Single((await reader.GetBacklinksAsync(new ArchivePostId(first, "1"))).Items);
        Assert.Empty((await reader.GetQuotesAsync(new ArchivePostId(first, "1"))).Items);
        Assert.Empty(await catalog.DeleteThreadsAsync(new[] { first, second }));
        Assert.Empty((await catalog.PrepareDeletionAsync(new[] { first, second })).Threads);

        using SqliteConnection db = Open(catalog.DatabasePath);
        foreach (string table in new[] { "archive_threads", "archive_posts", "archive_attachments", "archive_media_directories" })
            Assert.Equal(0L, Count(db, table, first));
        Assert.Equal(0L, Count(db, "archive_quotes", first));
        Assert.Equal(1L, Count(db, "archive_posts", kept));
        Assert.False(Directory.Exists(firstOld));
        Assert.False(Directory.Exists(shared));
    }

    private static CapturedThreadSnapshot Snapshot(ResourceId id, string media, string title, string html)
        => new CapturedThreadSnapshot(id, Url(id), title, ThreadSourceState.Archived, DateTimeOffset.UtcNow,
            media, new[] { new CapturedPost("1", 0, PostFieldPresence.All, Html: html,
                Attachments: new[] { new CapturedAttachment(0, "file", PlannedPath: Path.Combine(media, "file.png")) }) });

    private static string Url(ResourceId id) => $"https://boards.4chan.org/{id.Board}/thread/{id.Thread}";

    private static SqliteConnection Open(string path)
    {
        SqliteConnection db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        db.Open();
        return db;
    }

    private static long Count(SqliteConnection db, string table, ResourceId id)
    {
        using SqliteCommand command = db.CreateCommand();
        command.CommandText = $"SELECT count(*) FROM {table} WHERE source=$source AND board=$board AND thread=$thread";
        command.Parameters.AddWithValue("$source", id.Source);
        command.Parameters.AddWithValue("$board", id.Board);
        command.Parameters.AddWithValue("$thread", id.Thread!);
        return (long)command.ExecuteScalar()!;
    }

    private static long CountDecision(SqliteConnection db, ResourceId id)
    {
        using SqliteCommand command = db.CreateCommand();
        command.CommandText = "SELECT count(*) FROM media_decisions WHERE source_id=$source AND board_id=$board AND thread_id=$thread";
        command.Parameters.AddWithValue("$source", id.Source);
        command.Parameters.AddWithValue("$board", id.Board);
        command.Parameters.AddWithValue("$thread", id.Thread!);
        return (long)command.ExecuteScalar()!;
    }
}
