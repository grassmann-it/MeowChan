using MeowChan.Archive;
using MeowChan.Interfaces;
using MeowChan.Persistence;

namespace MeowChan.Tests;

public sealed class ArchiveSearchIdentityTests
{
    [Fact]
    public void AuthoritativeIdentityRoundTripsExactlyWhileNewInputIsTrimmed()
    {
        ArchiveSearchSpec exact = ArchiveSearchSpec.FromLegacy(new(Source: " A ", Board: " x "));
        Assert.Equal(new[] { " A " }, exact.Sources);
        Assert.Equal(new ArchiveBoardConstraint(" A ", " x "), Assert.Single(exact.Boards));
        string json = ArchiveSearchSpecJson.Serialize(exact);
        Assert.Equal(exact, ArchiveSearchSpecJson.Deserialize(json));
        Assert.Equal(json, ArchiveSearchSpecJson.Serialize(ArchiveSearchSpecJson.Deserialize(json)));
        ArchiveSearchSpec input = new(sources: [" A "], boards: [new(" A ", " x ")]);
        Assert.Equal(new[] { "A" }, input.Sources);
        Assert.Equal(new ArchiveBoardConstraint("A", "x"), Assert.Single(input.Boards));

        string multiple = ArchiveSearchSpecJson.Serialize(new ArchiveSearchSpec(sources: ["A", "B"], boards: [new("A", "x"), new("B", "y")]));
        multiple = multiple.Replace("\"B\"", "\" A \"", StringComparison.Ordinal).Replace("\"y\"", "\" x \"", StringComparison.Ordinal);
        ArchiveSearchSpec restored = ArchiveSearchSpecJson.Deserialize(multiple);
        Assert.Equal(new[] { " A ", "A" }, restored.Sources);
        Assert.Contains(new ArchiveBoardConstraint(" A ", " x "), restored.Boards);
        Assert.Contains(new ArchiveBoardConstraint("A", "x"), restored.Boards);
        Assert.Equal(restored, ArchiveSearchSpecJson.Deserialize(ArchiveSearchSpecJson.Serialize(restored)));
        Assert.Throws<ArgumentException>(() => ArchiveSearchSpecJson.Deserialize(json.Replace(" A ", " ", StringComparison.Ordinal)));

    }

    [Theory]
    [InlineData(ArchiveThreadSort.LatestCapture)]
    [InlineData(ArchiveThreadSort.OldestFirst)]
    [InlineData(ArchiveThreadSort.Title)]
    public async Task LegacyAndPersistedQueriesPreserveStoredIdentityThroughPageSelectionAndExport(ArchiveThreadSort sort)
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(directory.Path, lease);
        ResourceId[] stored = [new("A", "x", "1"), new(" A ", "x", "1"), new("A", " x ", "1"), new(" A ", " x ", "1")];
        for (int i = 0; i < stored.Length; i++)
            await catalog.CaptureAsync(new CapturedThreadSnapshot(stored[i], "https://example.test/" + i, "thread " + i,
                ThreadSourceState.Archived, DateTimeOffset.UtcNow, directory.Path,
                [new CapturedPost("1", 0, PostFieldPresence.Html, Html: "identity " + i)]));
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(directory.Path);
        foreach (ArchiveThreadQuery legacy in new[]
        {
            new ArchiveThreadQuery(Source: " A ", Sort: sort),
            new ArchiveThreadQuery(Board: " x ", Sort: sort),
            new ArchiveThreadQuery(Source: " A ", Board: " x ", Sort: sort)
        })
        {
            ResourceId[] expected = stored.Where(id => (legacy.Source == null || id.Source == legacy.Source)
                && (legacy.Board == null || id.Board == legacy.Board)).ToArray();
            ArchiveSearchSpec adapted = ArchiveSearchSpec.FromLegacy(legacy);
            ArchiveSearchRequest request = ArchiveSearchRequest.Create(ArchiveSearchSpecJson.Deserialize(ArchiveSearchSpecJson.Serialize(adapted)));
            IReadOnlyList<ResourceId> ids = await reader.SelectThreadIdsAsync(legacy);
            Assert.Equal(expected.Length, ids.Count);
            Assert.True(expected.ToHashSet().SetEquals(ids));
            Assert.Equal(ids, await reader.SelectThreadIdsBySpecAsync(request));
            Assert.Equal(ids, (await reader.SelectThreadSummariesAsync(legacy)).Select(item => item.Id));
            Assert.Equal(ids, (await reader.SelectThreadSummariesBySpecAsync(request)).Select(item => item.Id));
            List<ResourceId> pages = [];
            for (int offset = 0; offset < ids.Count; offset++)
            {
                ArchiveThreadPage page = await reader.SearchThreadsBySpecAsync(request, offset, 1);
                ArchiveThreadPage legacyPage = await reader.SearchThreadsAsync(legacy, offset, 1);
                Assert.Equal(expected.Length, page.TotalCount);
                Assert.Equal(expected.Length, legacyPage.TotalCount);
                Assert.Equal(page.Items.Select(item => item.Id), legacyPage.Items.Select(item => item.Id));
                pages.Add(Assert.Single(page.Items).Id);
            }
            Assert.Equal(ids, pages);
        }
        ArchiveSearchRequest exportRequest = ArchiveSearchRequest.Create(ArchiveSearchSpec.FromLegacy(new(Source: " A ", Board: " x ", Sort: sort)));
        IReadOnlyList<ResourceId> exportIds = await reader.SelectThreadIdsBySpecAsync(exportRequest);
        ArchiveCollectionHtmlExportResult export = await ArchiveCollectionHtmlExporter.ExportAsync(reader, exportIds, Path.Combine(directory.Path, "export"));
        ArchiveCollectionExportInspection inspected = await ArchiveCollectionHtmlExporter.InspectAsync(export.Directory, exportIds);
        Assert.Equal(stored[3], Assert.Single(inspected.Metadata.Members).Thread);
        Assert.Equal(1, export.ThreadCount);
        Assert.Equal(1, export.PostCount);
    }
}
