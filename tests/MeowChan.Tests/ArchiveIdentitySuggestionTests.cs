using Microsoft.Data.Sqlite;
using MeowChan.Archive;
using MeowChan.Persistence;

namespace MeowChan.Tests;

public sealed class ArchiveIdentitySuggestionTests
{
    [Fact]
    public async Task IdentitiesAreBoundedExactDistinctOrderedAndReadOnly()
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        await using var catalog = await ArchiveCatalog.OpenAsync(directory.Path, lease);
        foreach (var (source, board, thread) in new[] {
            ("z-removed", "same", "1"), ("A", "same", "2"), ("A", "same", "3"),
            ("A", "β' quote", "4"), ("a", "other", "5") })
            await catalog.RegisterThreadAsync(new(new ResourceId(source, board, thread),
                $"https://example.org/{thread}", "ignored title", directory.Path, false));
        await using var reader = await ArchiveReader.OpenAsync(directory.Path);
        var sources = await reader.ReadSourceIdentitiesAsync(2);
        Assert.Equal(new[] { "A", "a" }, sources.Items);
        Assert.True(sources.HasMore);
        var all = await reader.ReadSourceIdentitiesAsync();
        Assert.Equal(new[] { "A", "a", "z-removed" }, all.Items);
        Assert.False(all.HasMore);
        var pairs = await reader.ReadBoardIdentitiesAsync(limit: 2);
        Assert.Equal(new[] { new ArchiveBoardIdentity("A", "same"), new ArchiveBoardIdentity("A", "β' quote") }, pairs.Items);
        Assert.True(pairs.HasMore);
        var scoped = await reader.ReadBoardIdentitiesAsync("A", 2);
        Assert.Equal(pairs.Items, scoped.Items);
        Assert.False(scoped.HasMore);
        Assert.Empty((await reader.ReadBoardIdentitiesAsync("A' OR 1=1 --")).Items);
        Assert.Single((await reader.ReadBoardIdentitiesAsync("a")).Items);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => reader.ReadSourceIdentitiesAsync(257));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => reader.ReadBoardIdentitiesAsync(limit: 2049));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.ReadSourceIdentitiesAsync(cancellationToken: cancelled.Token));
    }

    [Fact]
    public async Task LargeCatalogUsesHardBackendLimitsIncludingScopedOverflow()
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        await using (var catalog = await ArchiveCatalog.OpenAsync(directory.Path, lease)) { }
        using (var connection = new SqliteConnection($"Data Source={Path.Combine(directory.Path, "archive.db")};Pooling=False"))
        {
            connection.Open(); using var transaction = connection.BeginTransaction();
            using var insert = connection.CreateCommand(); insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO archive_threads(source,board,thread,url,title,media_directory,source_state,capture_enabled,first_seen_at,revision)
                VALUES($source,$board,'1','https://example.org/thread/1','',$media,0,0,'2026-10-23T00:00:00.0000000+00:00',1)
                """;
            var source = insert.Parameters.Add("$source", SqliteType.Text);
            var board = insert.Parameters.Add("$board", SqliteType.Text);
            insert.Parameters.AddWithValue("$media", directory.Path);
            for (int i = 0; i < 2400; i++)
            {
                source.Value = i < 2100 ? "A" : $"source{i}";
                board.Value = $"board{i:D5}";
                insert.ExecuteNonQuery();
            }
            transaction.Commit();
        }
        await using var reader = await ArchiveReader.OpenAsync(directory.Path);
        var sources = await reader.ReadSourceIdentitiesAsync();
        var boards = await reader.ReadBoardIdentitiesAsync();
        var scoped = await reader.ReadBoardIdentitiesAsync("A", 256);
        Assert.Equal(256, sources.Items.Count); Assert.True(sources.HasMore);
        Assert.Equal(2048, boards.Items.Count); Assert.True(boards.HasMore);
        Assert.Equal(256, scoped.Items.Count); Assert.True(scoped.HasMore);
        Assert.All(scoped.Items, item => Assert.Equal("A", item.Source));
        Assert.Equal("board00000", scoped.Items[0].Board);
    }

    [Fact]
    public async Task EmptyAndLegacyCatalogRemainPassive()
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        await using (var catalog = await ArchiveCatalog.OpenAsync(directory.Path, lease)) { }
        // Schema 1 has the same identity columns. No suggestion migration is needed.
        using (var connection = new SqliteConnection($"Data Source={Path.Combine(directory.Path, "archive.db")};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version=1"; command.ExecuteNonQuery();
        }
        await using var reader = await ArchiveReader.OpenAsync(directory.Path);
        Assert.Empty((await reader.ReadSourceIdentitiesAsync()).Items);
        Assert.False((await reader.ReadBoardIdentitiesAsync()).HasMore);
        using var check = new SqliteConnection($"Data Source={reader.DatabasePath};Mode=ReadOnly;Pooling=False");
        check.Open(); using var version = check.CreateCommand(); version.CommandText = "PRAGMA user_version";
        Assert.Equal(1L, version.ExecuteScalar());
    }
}
