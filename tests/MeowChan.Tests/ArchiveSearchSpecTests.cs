using System.Globalization;
using System.Text.Json;
using MeowChan.Archive;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using Microsoft.Data.Sqlite;

namespace MeowChan.Tests;

public sealed class ArchiveSearchSpecTests
{
    [Fact]
    public void CanonicalDefinitionCopiesSetsAndRoundTripsAllFields()
    {
        string[] sources = [" B ", "A", "A", "a"];
        ArchiveBoardConstraint[] boards = [new("B", "y"), new("A", "x"), new(null, "x")];
        ArchiveSearchSpec spec = new("literal * OR ", sources, boards, [ThreadSourceState.Archived, ThreadSourceState.Active],
            true, false, ArchiveThreadSort.Title, ArchiveWorkFilter.Followup,
            new ArchiveDateRange(new DateOnly(2026, 3, 29), null, "Europe/Berlin"));
        sources[0] = "changed";
        boards[0] = new(null, "changed");
        Assert.Equal(new[] { "A", "B", "a" }, spec.Sources);
        string json = ArchiveSearchSpecJson.Serialize(spec);
        Assert.Contains("\"schemaVersion\":1", json);
        Assert.Contains("\"sort\":\"title\"", json);
        Assert.Contains("\"workFilter\":\"followup\"", json);
        Assert.Equal(spec, ArchiveSearchSpecJson.Deserialize(json));
        Assert.Equal(json, ArchiveSearchSpecJson.Serialize(ArchiveSearchSpecJson.Deserialize(json)));
        Assert.Null(new ArchiveSearchSpec(dateRange: new ArchiveDateRange(null, null, "UTC")).DateRange);
    }

    [Fact]
    public void InvalidDefinitionsNeverBecomeBroaderQueries()
    {
        Assert.Throws<ArgumentException>(() => new ArchiveSearchSpec(sources: [" "]));
        Assert.Throws<ArgumentException>(() => new ArchiveSearchSpec(boards: [new("A", "\0")]));
        Assert.Throws<ArgumentException>(() => new ArchiveSearchSpec(sources: Enumerable.Range(0, 129).Select(i => i.ToString())));
        Assert.Equal(128, new ArchiveSearchSpec(sources: Enumerable.Range(0, 128).Select(i => i.ToString())).Sources.Count);
        Assert.Throws<ArgumentException>(() => new ArchiveSearchSpec(boards: Enumerable.Range(0, 129).Select(i => new ArchiveBoardConstraint(null, i.ToString()))));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ArchiveSearchSpec(sort: (ArchiveThreadSort)90));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ArchiveSearchSpec(workFilter: (ArchiveWorkFilter)90));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ArchiveSearchSpec(sourceStates: [(ThreadSourceState)90]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ArchiveSearchSpec(schemaVersion: 2));
        Assert.Throws<ArgumentException>(() => new ArchiveDateRange(new(2026, 4, 2), new(2026, 4, 1), "UTC"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ArchiveDateRange(new(1, 1, 1), null, "UTC"));
        Assert.Throws<ArgumentException>(() => new ArchiveDateRange(new(2026, 1, 1), null, "Unknown/Zone"));
        string json = ArchiveSearchSpecJson.Serialize(new ArchiveSearchSpec());
        Assert.Throws<JsonException>(() => ArchiveSearchSpecJson.Deserialize(json.Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"futureFilter\":true")));
        Assert.Throws<JsonException>(() => ArchiveSearchSpecJson.Deserialize(json.Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"schemaVersion\":1")));
        Assert.Throws<JsonException>(() => ArchiveSearchSpecJson.Deserialize(json.Replace("latest-capture", "future-sort")));
        for (int i = 0; i <= 5; i++) Assert.Equal(i, ArchiveSearchSpec.WorkFilterToIndex(ArchiveSearchSpec.WorkFilterFromIndex(i)));
    }

    [Theory]
    [InlineData("Europe/Berlin", 2026, 3, 29, "2026-03-28T23:00:00+00:00", 23)]
    [InlineData("Europe/Berlin", 2026, 10, 25, "2026-10-24T22:00:00+00:00", 25)]
    [InlineData("UTC", 2024, 2, 29, "2024-02-29T00:00:00+00:00", 24)]
    [InlineData("Pacific/Apia", 2011, 12, 30, "2011-12-30T10:00:00+00:00", 0)]
    [InlineData("America/Sao_Paulo", 2018, 11, 4, "2018-11-04T03:00:00+00:00", 23)]
    [InlineData("America/Havana", 2020, 11, 1, "2020-11-01T04:00:00+00:00", 25)]
    public void CalendarBoundariesRespectGapsRepeatedMidnightAndSkippedDays(string zone, int year, int month, int day, string start, int hours)
    {
        DateOnly date = new(year, month, day);
        ResolvedDateWindow window = ArchiveDateResolver.Resolve(new ArchiveDateRange(date, date, zone))!;
        Assert.Equal(DateTimeOffset.Parse(start, CultureInfo.InvariantCulture), window.StartInclusive);
        Assert.Equal(TimeSpan.FromHours(hours), window.EndExclusive - window.StartInclusive);
    }

    [Fact]
    public void OpenBoundariesAndSupportedYearEdgesResolveWithoutOverflow()
    {
        Assert.Null(ArchiveDateResolver.Resolve(new ArchiveDateRange(null, null, "UTC")));
        Assert.Null(ArchiveDateResolver.Resolve(new ArchiveDateRange(null, new(9998, 12, 31), "Pacific/Kiritimati"))!.StartInclusive);
        Assert.Null(ArchiveDateResolver.Resolve(new ArchiveDateRange(new(2, 1, 1), null, "America/New_York"))!.EndExclusive);
        string json = ArchiveSearchSpecJson.Serialize(new ArchiveSearchSpec(dateRange: new ArchiveDateRange(new(2024, 2, 29), null, "UTC")));
        Assert.Throws<JsonException>(() => ArchiveSearchSpecJson.Deserialize(json.Replace("2024-02-29", "2023-02-29")));
        Assert.Throws<ArgumentException>(() => ArchiveSearchSpecJson.Deserialize(json.Replace("first-seen", "last-captured")));
    }

    [Fact]
    public async Task PairListsNeverBecomeCrossProductsAndLegacyQueriesRemainEquivalent()
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(directory.Path, lease);
        foreach (string source in new[] { "A", "B" })
        foreach (string board in new[] { "x", "y" })
            await catalog.RegisterThreadAsync(new ArchiveThreadRegistration(new ResourceId(source, board, "1"),
                "https://example.test/" + source + "/" + board, source + board, directory.Path, true));
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(directory.Path);
        ArchiveSearchSpec spec = new(sources: ["A", "B"], boards: [new("A", "x"), new("B", "y")]);
        Assert.Equal(new[] { "Ax", "By" }, (await reader.SelectThreadIdsBySpecAsync(ArchiveSearchRequest.Create(spec))).Select(id => id.Source + id.Board).Order());
        Assert.Equal(2, (await reader.SearchThreadsBySpecAsync(ArchiveSearchRequest.Create(new(boards: [new(null, "x")])))).TotalCount);
        Assert.Equal(0, (await reader.SearchThreadsBySpecAsync(ArchiveSearchRequest.Create(new(sources: ["A"], boards: [new("B", "y")])))).TotalCount);
        Assert.Equal(0, (await reader.SearchThreadsBySpecAsync(ArchiveSearchRequest.Create(new(sources: ["unknown"])))) .TotalCount);
        foreach (ArchiveThreadSort sort in Enum.GetValues<ArchiveThreadSort>())
        foreach (ArchiveThreadQuery legacy in new[] { new ArchiveThreadQuery(Sort: sort), new("A", Source: "A", Board: "x", HasPosts: false, CaptureEnabled: true, Sort: sort), new(Board: "x", Sort: sort), new(SourceState: ThreadSourceState.Active, Sort: sort) })
            Assert.Equal(await reader.SelectThreadIdsAsync(legacy), await reader.SelectThreadIdsBySpecAsync(ArchiveSearchRequest.Create(ArchiveSearchSpec.FromLegacy(legacy))));
    }

    [Fact]
    public async Task CountPagesSummariesAndExportIdsShareTickExactDatePredicateAndIgnoreWork()
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(directory.Path, lease);
        for (int i = 0; i < 123; i++)
            await catalog.RegisterThreadAsync(new ArchiveThreadRegistration(new ResourceId("A", "x", i.ToString("D3")),
                "https://example.test/" + i, "needle", directory.Path, true));
        DateTimeOffset start = new(2026, 3, 28, 23, 0, 0, TimeSpan.Zero);
        DateTimeOffset end = start.AddHours(23);
        using (SqliteConnection connection = new($"Data Source={catalog.DatabasePath};Pooling=False"))
        {
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "UPDATE archive_threads SET first_seen_at=$date";
            command.Parameters.AddWithValue("$date", start.ToString("O", CultureInfo.InvariantCulture));
            command.ExecuteNonQuery();
            command.CommandText = "UPDATE archive_threads SET first_seen_at=$date WHERE thread=$thread";
            command.Parameters.AddWithValue("$thread", "000");
            foreach ((string id, DateTimeOffset date) in new[] { ("000", start.AddTicks(-1)), ("001", end), ("002", end.AddTicks(-1)), ("003", start.ToOffset(TimeSpan.FromHours(5))) })
            {
                command.Parameters["$thread"].Value = id;
                command.Parameters["$date"].Value = date.ToString("O", CultureInfo.InvariantCulture);
                command.ExecuteNonQuery();
            }
        }
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(directory.Path);
        ArchiveSearchRequest request = ArchiveSearchRequest.Create(new ArchiveSearchSpec("needle", ["A"], [new("A", "x")],
            hasPosts: false, workFilter: ArchiveWorkFilter.Intervention, dateRange: new(new(2026, 3, 29), new(2026, 3, 29), "Europe/Berlin")));
        List<ResourceId> pages = [];
        for (int offset = 0; offset < 121; offset += 50)
        {
            ArchiveThreadPage page = await reader.SearchThreadsBySpecAsync(request, offset, 50);
            Assert.Equal(121, page.TotalCount);
            pages.AddRange(page.Items.Select(item => item.Id));
        }
        Assert.Equal(121, pages.Distinct().Count());
        Assert.Equal(pages, await reader.SelectThreadIdsBySpecAsync(request));
        Assert.Equal(pages, (await reader.SelectThreadSummariesBySpecAsync(request)).Select(item => item.Id));
        Assert.Contains(pages, id => id.Thread == "002");
        Assert.Contains(pages, id => id.Thread == "003");
        Assert.DoesNotContain(pages, id => id.Thread is "000" or "001");
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.SearchThreadsBySpecAsync(request, cancellationToken: cancellation.Token));
    }
    [Fact]
    public async Task PassiveLegacyDateSearchPreservesFileAndCorruptionFailsVisibly()
    {
        using TempDirectory directory = new();
        string path = Path.Combine(directory.Path, "archive.db");
        using (SqliteConnection connection = new($"Data Source={path};Pooling=False"))
        {
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            string resource = typeof(ArchiveSearchSpecTests).Assembly.GetManifestResourceNames().Single(name => name.EndsWith("archive-schema-1.sql", StringComparison.Ordinal));
            using StreamReader schema = new(typeof(ArchiveSearchSpecTests).Assembly.GetManifestResourceStream(resource)!);
            command.CommandText = schema.ReadToEnd();
            command.ExecuteNonQuery();
            command.CommandText = "INSERT INTO archive_threads VALUES('A','x','1','https://example.test/1','legacy',$directory,0,1,$date,NULL,0)";
            command.Parameters.AddWithValue("$directory", directory.Path);
            command.Parameters.AddWithValue("$date", new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero).ToString("O", CultureInfo.InvariantCulture));
            command.ExecuteNonQuery();
        }
        byte[] before = await File.ReadAllBytesAsync(path);
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(directory.Path);
        ArchiveSearchRequest request = ArchiveSearchRequest.Create(new(dateRange: new(new(2026, 10, 10), new(2026, 10, 10), "UTC")));
        Assert.Single((await reader.SearchThreadsBySpecAsync(request)).Items);
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
        using (SqliteConnection connection = new($"Data Source={path};Pooling=False"))
        {
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "UPDATE archive_threads SET first_seen_at='broken-date'";
            command.ExecuteNonQuery();
        }
        await Assert.ThrowsAnyAsync<Exception>(() => reader.SearchThreadsBySpecAsync(request));
    }

    [Fact]
    public async Task CaptureAndReregistrationPreserveFirstLocalRegistrationBasis()
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(directory.Path, lease);
        ResourceId id = new("A", "x", "1");
        ArchiveThreadRegistration registration = new(id, "https://example.test/1", "title", directory.Path, true);
        await catalog.RegisterThreadAsync(registration);
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(directory.Path);
        DateTimeOffset firstSeen = (await reader.ReadThreadAsync(id))!.Summary.FirstSeenAt;
        await catalog.CaptureAsync(new CapturedThreadSnapshot(id, "https://example.test/1", "later", ThreadSourceState.Archived,
            firstSeen.AddDays(10), directory.Path, [new CapturedPost("1", 0, PostFieldPresence.All, PostedAt: firstSeen.AddDays(-10), Html: "body")]));
        await catalog.RegisterThreadAsync(registration);
        ArchivedThreadView view = (await reader.ReadThreadAsync(id))!;
        Assert.Equal(firstSeen, view.Summary.FirstSeenAt);
        Assert.Equal(firstSeen.AddDays(10), view.Summary.LastCapturedAt);
        DateOnly registered = DateOnly.FromDateTime(firstSeen.UtcDateTime);
        Assert.Single((await reader.SearchThreadsBySpecAsync(ArchiveSearchRequest.Create(new(dateRange: new(registered, registered, "UTC"))))).Items);
        DateOnly captured = registered.AddDays(10);
        Assert.Empty((await reader.SearchThreadsBySpecAsync(ArchiveSearchRequest.Create(new(dateRange: new(captured, captured, "UTC"))))).Items);
    }

}
