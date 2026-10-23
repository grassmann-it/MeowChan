using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MeowChan.Archive;
using MeowChan.Persistence;
using Microsoft.Data.Sqlite;
using Xunit.Abstractions;

namespace MeowChan.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ArchiveSearchScaleCollection
{
    public const string Name = "Archive search scale evidence";
}

/// <summary>Real product queries on a disposable metadata-only catalog; timings are observations, never assertions.</summary>
[Collection(ArchiveSearchScaleCollection.Name)]
public sealed class ArchiveSearchScaleEvidenceTests(ITestOutputHelper output)
{
    private const string BaselineDigest = "d3166edc53b376360425c3949ef98d5f508d4155e9fad84fdaa25f718d11d40d";
    private const int ThreadsPerPair = 2000;
    private static readonly string[] Sources = ["A", "B", "C"];
    private static readonly string[] Boards = ["x", "y", .. Enumerable.Range(2, 18).Select(i => "b" + i.ToString("D2", CultureInfo.InvariantCulture))];
    private readonly List<object> _evidence = [];

    [Fact]
    [Trait("Category", "ArchiveScaleEvidence")]
    public async Task RealReaderOn120000MetadataRowsPreservesPairGlobalAndTwentyBoardSemantics()
    {
        using TempDirectory directory = new();
        string mediaDirectory = Path.Combine(directory.Path, "media-must-not-be-opened");
        string databasePath = Path.Combine(directory.Path, "archive.db");
        Stopwatch setup = Stopwatch.StartNew();
        using (ProfileLease lease = ProfileLease.Acquire(directory.Path))
        {
            await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(directory.Path, lease);
        }
        using (SqliteConnection connection = Open(databasePath))
        {
            using SqliteTransaction transaction = connection.BeginTransaction();
            using SqliteCommand insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                WITH RECURSIVE numbers(n) AS (SELECT 0 UNION ALL SELECT n+1 FROM numbers WHERE n<1999),
                sources(source) AS (VALUES('A'),('B'),('C')),
                boards(board) AS (SELECT CASE n WHEN 0 THEN 'x' WHEN 1 THEN 'y' ELSE printf('b%02d',n) END FROM numbers WHERE n<20)
                INSERT INTO archive_threads(source,board,thread,url,title,media_directory,source_state,capture_enabled,first_seen_at,last_captured_at,revision,search_text)
                SELECT source,board,printf('%04d',n),'https://fixture.invalid/'||source||'/'||board||'/'||n,
                  'local metadata only',$media,0,1,'2026-10-01T00:00:00.0000000+00:00',NULL,0,
                  source||char(10)||board||char(10)||printf('%04d',n)||char(10)||'local metadata only'
                FROM sources CROSS JOIN boards CROSS JOIN numbers;
                """;
            insert.Parameters.AddWithValue("$media", mediaDirectory);
            Assert.Equal(120000, insert.ExecuteNonQuery());
            transaction.Commit();
            using SqliteCommand inventory = connection.CreateCommand();
            inventory.CommandText = "SELECT (SELECT count(*) FROM archive_threads),(SELECT count(*) FROM archive_posts),(SELECT count(*) FROM archive_attachments),(SELECT count(*) FROM archive_media_directories),sqlite_version()";
            using SqliteDataReader row = inventory.ExecuteReader();
            Assert.True(row.Read());
            Assert.Equal(120000, row.GetInt32(0));
            Assert.Equal(0, row.GetInt32(1)); Assert.Equal(0, row.GetInt32(2)); Assert.Equal(0, row.GetInt32(3));
            Log(new { kind = "fixture", baseline = "FULL12", baselineDigest = BaselineDigest, rows = 120000,
                sources = Sources, boards = Boards, threadsPerPair = ThreadsPerPair, posts = 0, attachments = 0,
                mediaDirectories = 0, sqlite = row.GetString(4), setupMilliseconds = setup.Elapsed.TotalMilliseconds,
                runtime = RuntimeInformation.FrameworkDescription, os = RuntimeInformation.OSDescription,
                architecture = RuntimeInformation.ProcessArchitecture.ToString(), processors = Environment.ProcessorCount,
                note = "Schema created by ArchiveCatalog; deterministic metadata inserted in one transaction. No provider, media or network call." });
        }
        string before = FileHash(databasePath);
        Log(new { kind = "source-manifest", files = SourceManifest(), libraryAssemblySha256 = FileHash(typeof(ArchiveReader).Assembly.Location),
            testAssemblySha256 = FileHash(typeof(ArchiveSearchScaleEvidenceTests).Assembly.Location), databaseBytes = new FileInfo(databasePath).Length,
            databaseSha256 = before, observation = "Single sequential run, warm OS/SQLite caches possible; process-wide managed allocations are approximate, not retained memory or peak RSS." });
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(directory.Path);
        Dictionary<string, ResourceId[]> results = new(StringComparer.Ordinal);
        await Run("pair-list", new(sources: ["A", "B"], boards: [new("A", "x"), new("B", "y")], sort: ArchiveThreadSort.OldestFirst),
            (source, board) => source == "A" && board == "x" || source == "B" && board == "y", 4000, summaries: true);
        await Run("independent-source-board-sets", new(sources: ["A", "B"], boards: [new(null, "x"), new(null, "y")], sort: ArchiveThreadSort.OldestFirst),
            (source, board) => (source == "A" || source == "B") && (board == "x" || board == "y"), 8000);
        // A hierarchical UI adapter would emit these same exact pairs. This tests that contract, not a UI prototype.
        await Run("hierarchical-pair-contract", new(boards: [new("A", "x"), new("B", "y")], sort: ArchiveThreadSort.OldestFirst),
            (source, board) => source == "A" && board == "x" || source == "B" && board == "y", 4000);
        await Run("global-x", new(boards: [new(null, "x")], sort: ArchiveThreadSort.OldestFirst),
            (_, board) => board == "x", 6000);
        await Run("twenty-global-boards", new(boards: Boards.Select(board => new ArchiveBoardConstraint(null, board)), sort: ArchiveThreadSort.OldestFirst),
            (_, _) => true, 120000, summaries: true);
        await Run("remove-source-A-keep-pairs", new(sources: ["B"], boards: [new("A", "x"), new("B", "y")], sort: ArchiveThreadSort.OldestFirst),
            (source, board) => source == "B" && board == "y", 2000);
        Assert.Equal(results["pair-list"], results["hierarchical-pair-contract"]);
        ResourceId[] crossProductExtras = results["independent-source-board-sets"].Except(results["pair-list"]).ToArray();
        Assert.Equal(4000, crossProductExtras.Length);
        Assert.All(crossProductExtras, id => Assert.True(id.Source == "A" && id.Board == "y" || id.Source == "B" && id.Board == "x"));
        Assert.Equal(before, FileHash(databasePath));
        Assert.False(Directory.Exists(mediaDirectory));
        Log(new { kind = "conclusion", status = "PASS", fixtureRows = 120000, pairAndHierarchyEqual = true,
            independentSetsAdditionalRows = crossProductExtras.Length, additionalPairs = new[] { "A/y", "B/x" },
            databaseUnchanged = true, mediaDirectoryCreated = false,
            scope = "ArchiveReader/SQL semantics, elapsed time and materialization only. No UI speed, usability or screenreader conclusion." });

        async Task Run(string name, ArchiveSearchSpec spec, Func<string, string, bool> predicate, int count, bool summaries = false)
        {
            ResourceId[] expected = Expected(predicate);
            Assert.Equal(count, expected.Length);
            ArchiveSearchRequest request = ArchiveSearchRequest.Create(spec);
            Log(new { kind = "request", scenario = name, definition = ArchiveSearchSpecJson.Serialize(spec), expectedCount = count,
                expectedIdentitiesSha256 = IdentityHash(expected) });
            // There is no separate public count API: this measures the normal count plus one-row page.
            ArchiveThreadPage countPage = await Measure(name, "count-plus-one-row", () => reader.SearchThreadsBySpecAsync(request, 0, 1), value => value.Items.Count, value => value.TotalCount);
            Assert.Equal(count, countPage.TotalCount);
            Assert.Equal(expected.Take(1), countPage.Items.Select(item => item.Id));
            foreach (int offset in new[] { 50, count - 50 })
            {
                ArchiveThreadPage page = await Measure(name, "page-50-offset-" + offset, () => reader.SearchThreadsBySpecAsync(request, offset, 50), value => value.Items.Count, value => value.TotalCount);
                Assert.Equal(count, page.TotalCount);
                Assert.Equal(expected.Skip(offset).Take(50), page.Items.Select(item => item.Id));
                Assert.All(page.Items, item => Assert.Equal(0, item.PostCount));
            }
            IReadOnlyList<ResourceId> ids = await Measure(name, "all-result-identities", () => reader.SelectThreadIdsBySpecAsync(request), value => value.Count, value => value.Count);
            Assert.Equal(expected, ids);
            Assert.Equal(count, ids.Distinct().Count());
            results.Add(name, ids.ToArray());
            Log(new { kind = "identities", scenario = name, actualCount = ids.Count, actualIdentitiesSha256 = IdentityHash(ids), exactIdentityAndOrderMatch = true });
            if (summaries)
            {
                IReadOnlyList<ArchiveThreadSummary> all = await Measure(name, "all-result-summaries", () => reader.SelectThreadSummariesBySpecAsync(request), value => value.Count, value => value.Count);
                Assert.Equal(expected, all.Select(item => item.Id));
                Assert.All(all, item => Assert.Equal(0, item.PostCount));
            }
        }
    }

    private async Task<T> Measure<T>(string scenario, string operation, Func<Task<T>> action, Func<T, int> materialized, Func<T, int> total)
    {
        long allocated = GC.GetTotalAllocatedBytes(precise: true);
        Stopwatch elapsed = Stopwatch.StartNew();
        T value = await action();
        elapsed.Stop();
        long allocationDelta = GC.GetTotalAllocatedBytes(precise: true) - allocated;
        Log(new { kind = "measurement", scenario, operation, elapsedMilliseconds = elapsed.Elapsed.TotalMilliseconds,
            processManagedAllocatedBytes = allocationDelta, returnedRecords = materialized(value), totalMatches = total(value),
            materialization = operation == "all-result-identities"
                ? "Returned ResourceId records from a single ordered identity-only query. No intermediate summaries or post/media content."
                : "Returned compact ArchiveThreadSummary records, including identity and post count; no post/media content." });
        return value;
    }

    private void Log(object evidence)
    {
        _evidence.Add(evidence);
        output.WriteLine("URSA14_EVIDENCE " + JsonSerializer.Serialize(evidence));
        string? target = Environment.GetEnvironmentVariable("MEOWCHAN_URSA14_EVIDENCE_DIRECTORY");
        if (string.IsNullOrWhiteSpace(target)) return;
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "archive-search-scale.json"), JsonSerializer.Serialize(_evidence, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static ResourceId[] Expected(Func<string, string, bool> predicate) => Sources
        .SelectMany(source => Boards.Where(board => predicate(source, board)).SelectMany(board => Enumerable.Range(0, ThreadsPerPair)
            .Select(index => new ResourceId(source, board, index.ToString("D4", CultureInfo.InvariantCulture)))))
        .OrderBy(id => id.Source, StringComparer.Ordinal).ThenBy(id => id.Board, StringComparer.Ordinal).ThenBy(id => id.Thread, StringComparer.Ordinal).ToArray();

    private static string IdentityHash(IEnumerable<ResourceId> ids) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        string.Join("\n", ids.Select(id => id.Source + "/" + id.Board + "/" + id.Thread))))).ToLowerInvariant();

    private static string FileHash(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static SortedDictionary<string, string> SourceManifest()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "libmeowchan", "Archive", "ArchiveReader.cs"))) root = root.Parent;
        Assert.NotNull(root);
        string[] files = ["libmeowchan/Archive/ArchiveReader.cs", "libmeowchan/Archive/ArchiveSql.cs", "libmeowchan/Archive/ArchiveSql.Search.cs",
            "libmeowchan/Archive/ArchiveSearchCompiler.cs", "libmeowchan/Archive/ArchiveSearchSpec.cs", "libmeowchan/Archive/ArchiveSearchSpecJson.cs",
            "libmeowchan/Archive/ArchiveDateRange.cs", "libmeowchan/Archive/ArchiveCatalog.cs", "libmeowchan/Archive/ArchiveSql.SearchSchema.cs",
            "tests/MeowChan.Tests/ArchiveSearchScaleEvidenceTests.cs", "tests/MeowChan.Tests/MeowChan.Tests.csproj"];
        return new SortedDictionary<string, string>(files.ToDictionary(file => file, file => FileHash(Path.Combine(root!.FullName, file))), StringComparer.Ordinal);
    }

    private static SqliteConnection Open(string path)
    {
        SqliteConnection connection = new(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }
}
