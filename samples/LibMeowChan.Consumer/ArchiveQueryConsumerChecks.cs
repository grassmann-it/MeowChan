using System.Text.Json;
using MeowChan;
using MeowChan.Archive;
using MeowChan.Interfaces;

namespace LibMeowChan.Consumer;

/// <summary>Compiled against the actual NuGet package, without InternalsVisibleTo or a source project reference.</summary>
internal static class ArchiveQueryConsumerChecks
{
    public static async Task VerifyAsync(ArchiveReader reader, ResourceId id, string profile, string outputDirectory)
    {
        ArchiveThreadSummary summary = await reader.ReadThreadSummaryAsync(id)
            ?? throw new InvalidOperationException("The query fixture is missing.");
        ArchiveThreadQuery legacy = new("PACKAGE-ONLY", id.Source, id.Board, ThreadSourceState.Archived, true, true,
            ArchiveThreadSort.LatestCapture);
        var (text, source, board, state, capture, hasPosts, sort) = legacy;
        Require(text == "PACKAGE-ONLY" && source == id.Source && board == id.Board && state == ThreadSourceState.Archived
            && capture == true && hasPosts == true && sort == ArchiveThreadSort.LatestCapture, "Legacy positional/deconstruction contract changed.");
        ArchiveSearchSpec exactIdentity = ArchiveSearchSpec.FromLegacy(new(Source: " demo ", Board: " board "));
        Require(exactIdentity.Sources.Single() == " demo " && exactIdentity.Boards.Single() == new ArchiveBoardConstraint(" demo ", " board ")
            && exactIdentity.Equals(ArchiveSearchSpecJson.Deserialize(ArchiveSearchSpecJson.Serialize(exactIdentity))),
            "Authoritative legacy identities changed during adaptation or persistence.");
        Require(typeof(ArchiveSearchSpec).GetMethod("WorkFilterFromIndex") == null && typeof(ArchiveSearchSpec).GetMethod("WorkFilterToIndex") == null,
            "Desktop index adapters leaked into the public library API.");
        ArchiveThreadQuery titledLegacy = legacy with { Sort = ArchiveThreadSort.Title };
        ArchiveSearchSpec adapted = ArchiveSearchSpec.FromLegacy(titledLegacy);
        ArchiveSearchRequest adaptedRequest = ArchiveSearchRequest.Create(adapted);
        Require((await reader.SelectThreadIdsAsync(titledLegacy)).SequenceEqual(await reader.SelectThreadIdsBySpecAsync(adaptedRequest)),
            "Legacy and adapted package requests differ.");
        Require((await reader.SelectThreadSummariesAsync(titledLegacy)).SequenceEqual(await reader.SelectThreadSummariesBySpecAsync(adaptedRequest)),
            "Legacy and adapted package summaries differ.");

        DateOnly day = DateOnly.FromDateTime(summary.FirstSeenAt.UtcDateTime);
        List<string> sources = [id.Source, "absent-source"];
        List<ArchiveBoardConstraint> boards = [new(id.Source, id.Board), new("absent-source", "absent-board")];
        ArchiveDateRange range = new(day, day, "UTC");
        ArchiveSearchSpec spec = new("PACKAGE-ONLY", sources, boards, [ThreadSourceState.Active, ThreadSourceState.Archived],
            captureEnabled: true, hasPosts: true, sort: ArchiveThreadSort.Title, workFilter: ArchiveWorkFilter.All, dateRange: range);
        sources.Clear(); boards.Clear();
        Require(spec.Sources.Count == 2 && spec.Boards.Count == 2, "The package definition retained mutable input collections.");
        string definition = ArchiveSearchSpecJson.Serialize(spec);
        ArchiveSearchSpec restored = ArchiveSearchSpecJson.Deserialize(definition);
        Require(spec.Equals(restored) && definition == ArchiveSearchSpecJson.Serialize(restored), "The complete v1 query did not round-trip.");
        ArchiveSearchRequest request = ArchiveSearchRequest.Create(restored);
        ResolvedDateWindow window = request.DateWindow ?? throw new InvalidOperationException("A date window is missing.");
        DateTimeOffset start = new(day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
        Require(window.StartInclusive == start && window.EndExclusive == start.AddDays(1) && window.TimeZoneId == "UTC", "UTC calendar bounds differ.");
        Require(ArchiveDateResolver.Resolve(range) == window && ArchiveDateResolver.Resolve(null) == null, "Public date resolution differs.");
        Require(ArchiveDateResolver.Resolve(new ArchiveDateRange(new(2026, 3, 29), new(2026, 3, 29), "Europe/Berlin"))
            is { StartInclusive: { } dstStart, EndExclusive: { } dstEnd } && dstEnd - dstStart == TimeSpan.FromHours(23),
            "The packaged date resolver lost the Berlin short day.");

        ArchiveThreadPage page = await reader.SearchThreadsBySpecAsync(request, offset: 0, limit: 1);
        IReadOnlyList<ResourceId> frozenIds = await reader.SelectThreadIdsBySpecAsync(request);
        IReadOnlyList<ArchiveThreadSummary> summaries = await reader.SelectThreadSummariesBySpecAsync(request);
        Require(page.TotalCount == 1 && page.Items.Single().Id == id && frozenIds.SequenceEqual([id]) && summaries.Single().Id == id,
            "Page, full IDs and summaries do not share the complete package query.");
        Require(ReferenceEquals(request.DateWindow, window), "Reading re-resolved the request's frozen date window.");
        Require((await reader.SearchThreadsBySpecAsync(ArchiveSearchRequest.Create(new(sources: ["absent-source"])))).TotalCount == 0,
            "An unknown source broadened the package query.");
        // Work membership is a caller concern; a local archive query does not claim session-state filtering.
        Require((await reader.SelectThreadIdsBySpecAsync(ArchiveSearchRequest.Create(new(sources: [id.Source], workFilter: ArchiveWorkFilter.Watching))))
            .SequenceEqual(frozenIds), "The archive invented a SQL work-state filter.");

        // Calling null on the legacy API must still compile without overload ambiguity and fail validation.
        Expect<ArgumentNullException>(() => { _ = reader.SearchThreadsAsync(null!); });
        Expect<ArgumentNullException>(() => { _ = reader.SearchThreadsBySpecAsync(null!); });
        Expect<ArgumentOutOfRangeException>(() => { _ = reader.SearchThreadsBySpecAsync(request, limit: 0); });
        Expect<ArgumentOutOfRangeException>(() => _ = new ArchiveSearchSpec(schemaVersion: 2));
        Expect<ArgumentException>(() => _ = new ArchiveDateRange(day, day, "Unknown/Not-A-Time-Zone"));
        Expect<JsonException>(() => ArchiveSearchSpecJson.Deserialize(definition.Replace("\"sort\":\"title\"", "\"sort\":\"future-sort\"", StringComparison.Ordinal)));
        using (CancellationTokenSource cancelled = new())
        {
            cancelled.Cancel();
            await ExpectAsync<OperationCanceledException>(() => reader.SearchThreadsBySpecAsync(request, cancellationToken: cancelled.Token));
            await ExpectAsync<OperationCanceledException>(() => reader.SelectThreadIdsBySpecAsync(request, cancelled.Token));
            await ExpectAsync<OperationCanceledException>(() => reader.SelectThreadSummariesBySpecAsync(request, cancelled.Token));
        }
        ArchiveReader disposable = await ArchiveReader.OpenAsync(profile);
        await disposable.DisposeAsync();
        await disposable.DisposeAsync();
        await ExpectAsync<ObjectDisposedException>(() => disposable.SearchThreadsBySpecAsync(request));
        await ExpectAsync<ObjectDisposedException>(() => disposable.SelectThreadIdsBySpecAsync(request));
        await ExpectAsync<ObjectDisposedException>(() => disposable.SelectThreadSummariesBySpecAsync(request));

        // The target list stays frozen even after another search; the public collection exporter accepts it directly.
        ArchiveCollectionHtmlExportResult exported = await ArchiveCollectionHtmlExporter.ExportAsync(reader, frozenIds,
            Path.Combine(outputDirectory, "query-collection-export"));
        Require(exported.ThreadCount == 1 && exported.PostCount == 1 && File.Exists(exported.IndexPath), "The query-to-export package path failed.");
        Console.WriteLine(JsonSerializer.Serialize(new { queryApi = "PASS", legacyDeconstruction = true, schemaVersion = spec.SchemaVersion,
            expectedThreads = 1, actualThreads = frozenIds.Count, dateZone = window.TimeZoneId, startInclusive = window.StartInclusive,
            endExclusive = window.EndExclusive, collectionThreads = exported.ThreadCount, lifecycle = "disposed and cancelled readers reject results",
            libraryAssembly = typeof(ArchiveSearchSpec).Assembly.Location }));
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    private static void Expect<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException("Expected public exception " + typeof(T).Name);
    }
    private static async Task ExpectAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException("Expected public exception " + typeof(T).Name);
    }
}
