using MeowChan.Archive;
using MeowChan.Persistence;
using Microsoft.Data.Sqlite;

namespace MeowChan.Tests;

public sealed class ArchiveFilenameSearchTests
{
    [Fact]
    public async Task FullIdentityDeletedMetadataAndStablePagingRemainSeparate()
    {
        using TempDirectory directory = new();
        await Seed(directory.Path);
        await using var reader = await ArchiveReader.OpenAsync(directory.Path);
        var all = await reader.SearchFilenamesAsync(new("same"));
        Assert.Equal(4L, all.TotalCount);
        Assert.Equal(new[] { "A/g/1/10/0", "A/g/1/10/1", "A/g/2/10/0", "B/g/1/10/0" },
            all.Items.Select(Key));
        Assert.Equal("revision-original", all.Items[0].AttachmentRevisionKey);
        Assert.Equal("preview", all.Items[1].Variant);
        Assert.True(all.Items[1].AttachmentIsDeleted);
        Assert.True(all.Items[2].PostIsDeleted);
        Assert.All(all.Items, hit => Assert.Equal(7L, hit.CatalogThreadRevision));
        for (int i = 0; i < 4; i++)
        {
            var page = await reader.SearchFilenamesAsync(new("SAME"), i, 1);
            Assert.Equal(all.TotalCount, page.TotalCount);
            Assert.Equal(i, page.Offset);
            Assert.Equal(all.Items[i], Assert.Single(page.Items));
        }
        var beyond = await reader.SearchFilenamesAsync(new("same"), int.MaxValue, 500);
        Assert.Empty(beyond.Items);
        Assert.Equal(4L, beyond.TotalCount);
        // Returned pages are values, not a live view of subsequent catalog writes.
        using var connection = ArchiveSql.OpenConnection(reader.DatabasePath, SqliteOpenMode.ReadWrite);
        using var update = connection.CreateCommand();
        update.CommandText = "UPDATE archive_attachments SET original_name='changed'";
        update.ExecuteNonQuery();
        Assert.Equal(4L, all.TotalCount);
        Assert.Equal("same.jpg", all.Items[0].OriginalName);
        Assert.Empty((await reader.SearchFilenamesAsync(new("same"))).Items);
    }

    [Theory]
    [InlineData("%_", 1)]
    [InlineData("' OR 1=1 --", 1)]
    [InlineData("ÄΩ", 1)]
    [InlineData("é", 1)]
    [InlineData("e\u0301", 0)]
    [InlineData("path-only-needle", 0)]
    [InlineData(".*", 0)]
    [InlineData(" same", 0)]
    [InlineData("", 5)]
    public async Task OriginalNameOnlyLiteralUnicodeAndNoWildcardSemantics(string text, long expected)
    {
        using TempDirectory directory = new();
        await Seed(directory.Path);
        await using var reader = await ArchiveReader.OpenAsync(directory.Path);
        Assert.Equal(expected, (await reader.SearchFilenamesAsync(new(text))).TotalCount);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task LegacyWithoutRevisionColumnRemainsUnknownAndDoesNotMigrate(int version)
    {
        using TempDirectory directory = new();
        await Seed(directory.Path);
        string path = Path.Combine(directory.Path, "archive.db");
        using (var connection = ArchiveSql.OpenConnection(path, SqliteOpenMode.ReadWrite))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"ALTER TABLE archive_attachments DROP COLUMN attachment_revision_key; PRAGMA user_version={version}";
            command.ExecuteNonQuery();
        }
        byte[] before = File.ReadAllBytes(path);
        await using var reader = await ArchiveReader.OpenAsync(directory.Path);
        var page = await reader.SearchFilenamesAsync(new("same"));
        Assert.Equal(4L, page.TotalCount);
        Assert.All(page.Items, hit => Assert.Null(hit.AttachmentRevisionKey));
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task BoundsCancellationAndOfflineMediaPathArePassive()
    {
        using TempDirectory directory = new();
        await Seed(directory.Path);
        // A regular file is deliberately not an accessible media directory.
        string media = Path.Combine(directory.Path, "not-a-directory");
        File.WriteAllText(media, "sentinel");
        string path = Path.Combine(directory.Path, "archive.db");
        byte[] before = File.ReadAllBytes(path);
        await using var reader = await ArchiveReader.OpenAsync(directory.Path);
        Assert.Equal(5L, (await reader.SearchFilenamesAsync(new(""))).TotalCount);
        Assert.Equal("sentinel", File.ReadAllText(media));
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Throws<ArgumentNullException>(() => new ArchiveFilenameSearch(null!));
        Assert.Throws<ArgumentException>(() => new ArchiveFilenameSearch(new string('x', 513)));
        Assert.Throws<ArgumentException>(() => new ArchiveFilenameSearch("a\0b"));
        Assert.Equal(512, new ArchiveFilenameSearch(string.Concat(Enumerable.Repeat("😀", 256))).Text.Length);
        Assert.Throws<ArgumentException>(() => new ArchiveFilenameSearch(string.Concat(Enumerable.Repeat("😀", 257))));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => reader.SearchFilenamesAsync(new(""), -1));
        foreach (int limit in new[] { 0, 501 })
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => reader.SearchFilenamesAsync(new(""), limit: limit));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            reader.SearchFilenamesAsync(new("same"), cancellationToken: cancelled.Token));
    }

    private static string Key(ArchiveFilenameHit hit)
        => $"{hit.Thread.Source}/{hit.Thread.Board}/{hit.Thread.Thread}/{hit.Post}/{hit.Position}";

    private static async Task Seed(string profile)
    {
        using ProfileLease lease = ProfileLease.Acquire(profile);
        await using (var catalog = await ArchiveCatalog.OpenAsync(profile, lease)) { }
        using var connection = ArchiveSql.OpenConnection(Path.Combine(profile, "archive.db"), SqliteOpenMode.ReadWrite);
        using var transaction = connection.BeginTransaction();
        foreach (var (source, thread) in new[] { ("B", "1"), ("A", "2"), ("A", "1") })
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO archive_threads(source,board,thread,url,title,media_directory,source_state,capture_enabled,first_seen_at,revision)
                VALUES($source,'g',$thread,'https://example.org/thread/1','',$media,2,0,'2026-11-05T00:00:00.0000000+00:00',7);
                INSERT INTO archive_posts(source,board,thread,post,ordinal,fields,is_deleted)
                VALUES($source,'g',$thread,'10',0,0,$deleted);
                INSERT INTO archive_attachments(source,board,thread,post,position,variant,original_name,planned_path,is_deleted,attachment_revision_key)
                VALUES($source,'g',$thread,'10',0,'original','same.jpg',$path,0,'revision-original');
                """;
            command.Parameters.AddWithValue("$source", source);
            command.Parameters.AddWithValue("$thread", thread);
            command.Parameters.AddWithValue("$media", Path.Combine(profile, "not-a-directory"));
            command.Parameters.AddWithValue("$path", Path.Combine(profile, "path-only-needle.jpg"));
            command.Parameters.AddWithValue("$deleted", thread == "2" ? 1 : 0);
            command.ExecuteNonQuery();
        }
        using var extra = connection.CreateCommand();
        extra.Transaction = transaction;
        extra.CommandText = """
            INSERT INTO archive_attachments(source,board,thread,post,position,variant,original_name,is_deleted)
            VALUES('A','g','1','10',1,'preview','same.png',1),
                  ('A','g','1','10',2,'original',$name,0),
                  ('A','g','1','10',3,'original',NULL,0);
            """;
        extra.Parameters.AddWithValue("$name", "äω-é%_' OR 1=1 --.jpg");
        extra.ExecuteNonQuery();
        transaction.Commit();
    }
}
