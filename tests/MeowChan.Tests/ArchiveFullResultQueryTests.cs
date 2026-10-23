using System.Globalization;
using MeowChan.Archive;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using Microsoft.Data.Sqlite;

namespace MeowChan.Tests;

public sealed class ArchiveFullResultQueryTests
{
    [Fact]
    public async Task FullResultsMatchCountAndEveryPageAcrossAllSortsAndFilterPaths()
    {
        using TempDirectory directory = new();
        await Seed(directory.Path);
        using (ProfileLease lease = ProfileLease.Acquire(directory.Path))
        {
            await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(directory.Path, lease);
            foreach (string id in new[] { "0000", "0004" })
                await catalog.CaptureAsync(new CapturedThreadSnapshot(new ResourceId("A", "x", id), "https://fixture.invalid/" + id,
                    "captured needle", ThreadSourceState.Archived, new DateTimeOffset(2026, 10, 4, 0, 0, 0, TimeSpan.Zero), directory.Path,
                    [new CapturedPost(id, 0, PostFieldPresence.All, Html: "postneedle")]));
        }
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(directory.Path);
        foreach (ArchiveThreadSort sort in Enum.GetValues<ArchiveThreadSort>())
        {
            ArchiveSearchSpec[] specs = [new(sort: sort), new("n", sort: sort), new("needle", sort: sort), new("postneedle", sort: sort),
                new(sources: ["A", "B"], boards: [new("A", "x"), new("B", "y")], sort: sort),
                new(sourceStates: [ThreadSourceState.Active, ThreadSourceState.Archived], captureEnabled: false, sort: sort),
                new(hasPosts: true, sort: sort), new(sources: ["B"], hasPosts: false, sort: sort,
                    dateRange: new(new(2026, 10, 2), new(2026, 10, 2), "UTC")),
                new(sources: ["A"], boards: [new("B", "x")], sort: sort), new(workFilter: ArchiveWorkFilter.Intervention, sort: sort)];
            foreach (ArchiveSearchSpec spec in specs)
            {
                ArchiveSearchRequest request = ArchiveSearchRequest.Create(spec);
                List<ArchiveThreadSummary> paged = [];
                ArchiveThreadPage page = await reader.SearchThreadsBySpecAsync(request, limit: 500);
                int total = page.TotalCount;
                paged.AddRange(page.Items);
                while (paged.Count < total)
                {
                    page = await reader.SearchThreadsBySpecAsync(request, paged.Count, 500);
                    Assert.Equal(total, page.TotalCount);
                    Assert.NotEmpty(page.Items);
                    paged.AddRange(page.Items);
                }
                Assert.Equal(total, paged.Count);
                IReadOnlyList<ResourceId> ids = await reader.SelectThreadIdsBySpecAsync(request);
                IReadOnlyList<ArchiveThreadSummary> summaries = await reader.SelectThreadSummariesBySpecAsync(request);
                Assert.Equal(paged.Select(item => item.Id), ids);
                Assert.Equal(paged, summaries);
                Assert.Equal(total, ids.Distinct().Count());
                if (spec.Text == "postneedle") Assert.Equal(2, total);
            }
        }
        Assert.Equal(1205, (await reader.SelectThreadIdsBySpecAsync(ArchiveSearchRequest.Create(new()))).Count);
        using CancellationTokenSource cancelled = new();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.SelectThreadIdsBySpecAsync(ArchiveSearchRequest.Create(new()), cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.SelectThreadSummariesBySpecAsync(ArchiveSearchRequest.Create(new()), cancelled.Token));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CancellationDuringActualFullQueryDoesNotPublishAPartialResult(bool idsOnly)
    {
        using TempDirectory directory = new();
        await Seed(directory.Path);
        using SqliteConnection connection = ArchiveSql.OpenConnection(Path.Combine(directory.Path, "archive.db"), SqliteOpenMode.ReadOnly);
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: true);
        using CancellationTokenSource cancellation = new();
        int evaluatedTitles = 0;
        // Deterministically cancel from inside real SQLite execution rather than relying on
        // machine speed, timers, task scheduling or an invented already-cancelled query.
        connection.CreateFunction<string?, string>("meowchan_text", title =>
        {
            if (++evaluatedTitles == 10) cancellation.Cancel();
            return title ?? "";
        });
        ArchiveSearchRequest request = ArchiveSearchRequest.Create(new(sort: ArchiveThreadSort.Title));
        Assert.Throws<OperationCanceledException>(() =>
        {
            if (idsOnly) ArchiveSql.SearchIds(connection, transaction, request, cancellation.Token);
            else ArchiveSql.SearchSummaries(connection, transaction, request, cancellation.Token);
        });
        Assert.True(evaluatedTitles >= 10);
        Assert.True(cancellation.IsCancellationRequested);
        // The cancelled local call has not disposed or mutated the caller's read snapshot.
        Assert.Equal(1205, ArchiveSql.SearchPage(connection, transaction, ArchiveSearchRequest.Create(new()), 0, 1, CancellationToken.None).TotalCount);
    }

    [Fact]
    public void IdentityProjectionAvoidsSummaryMaterializationAndFullSummaryEvaluatesEachRowOnce()
    {
        using SqliteConnection connection = new("Data Source=:memory:");
        connection.Open();
        int projectedTitles = 0;
        connection.CreateFunction<string, string>("observed_title", title => { projectedTitles++; return title; });
        using (SqliteCommand seed = connection.CreateCommand())
        {
            // An instrumented SQL view counts summary projection work, without timing thresholds.
            // Empty search text and oldest-first order do not require title as a filter or sort key.
            seed.CommandText = """
                PRAGMA user_version=1;
                CREATE TABLE metadata(source TEXT,board TEXT,thread TEXT,first_seen_at TEXT);
                WITH RECURSIVE n(i) AS (SELECT 0 UNION ALL SELECT i+1 FROM n WHERE i<1204)
                INSERT INTO metadata SELECT 'A','x',printf('%04d',i),'2026-10-01T00:00:00.0000000+00:00' FROM n;
                CREATE VIEW archive_threads AS SELECT source,board,thread,'https://fixture.invalid/'||thread AS url,
                  observed_title(thread) AS title,'' AS media_directory,0 AS source_state,1 AS capture_enabled,
                  first_seen_at,NULL AS last_captured_at,0 AS revision FROM metadata;
                CREATE TABLE archive_posts(source TEXT,board TEXT,thread TEXT);
                """;
            seed.ExecuteNonQuery();
        }
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: true);
        ArchiveSearchRequest request = ArchiveSearchRequest.Create(new(sort: ArchiveThreadSort.OldestFirst));
        IReadOnlyList<ResourceId> ids = ArchiveSql.SearchIds(connection, transaction, request, CancellationToken.None);
        Assert.Equal(1205, ids.Count);
        Assert.Equal(0, projectedTitles);
        IReadOnlyList<ArchiveThreadSummary> summaries = ArchiveSql.SearchSummaries(connection, transaction, request, CancellationToken.None);
        Assert.Equal(ids, summaries.Select(item => item.Id));
        Assert.Equal(1205, projectedTitles);
    }

    private static async Task Seed(string directory)
    {
        using (ProfileLease lease = ProfileLease.Acquire(directory))
        {
            await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(directory, lease);
        }
        using SqliteConnection connection = new(new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "archive.db"), Pooling = false }.ToString());
        connection.Open();
        using SqliteCommand seed = connection.CreateCommand();
        seed.CommandText = """
            WITH RECURSIVE n(i) AS (SELECT 0 UNION ALL SELECT i+1 FROM n WHERE i<1204)
            INSERT INTO archive_threads(source,board,thread,url,title,media_directory,source_state,capture_enabled,first_seen_at,last_captured_at,revision,search_text)
            SELECT CASE i%2 WHEN 0 THEN 'A' ELSE 'B' END,CASE WHEN i%4<2 THEN 'x' ELSE 'y' END,
                printf('%04d',i),'https://fixture.invalid/'||i,CASE i%5 WHEN 0 THEN 'needle' ELSE 'other' END,
                '',i%3,i%2,printf('2026-10-%02dT00:00:00.0000000+00:00',i%3+1),NULL,0,
                printf('%04d',i)||char(10)||CASE i%5 WHEN 0 THEN 'needle' ELSE 'other' END FROM n;
            """;
        Assert.Equal(1205, seed.ExecuteNonQuery());
    }
}
