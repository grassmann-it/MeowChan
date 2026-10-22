using MeowChan.Archive;
using MeowChan.Interfaces;
using MeowChan.Persistence;

namespace MeowChan.DesktopTests;

public sealed class ArchiveResultsExportTests
{
    [Fact]
    public async Task ResolvesAllPagesInAppliedQueryOrderAndFreezesWorkMembership()
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(directory.Path, lease);
        List<ThreadWorkSnapshot> work = new();
        for (int index = 0; index < 302; index++)
        {
            ResourceId id = new(index == 300 ? "other" : "fixture", index == 301 ? "b" : "a", index.ToString());
            string url = "https://fixture.test/" + index;
            await catalog.CaptureAsync(new CapturedThreadSnapshot(id, url, "match " + index.ToString("D3"),
                ThreadSourceState.Archived, DateTimeOffset.UtcNow, directory.Path,
                new[] { new CapturedPost(id.Thread!, 0, PostFieldPresence.All, Html: "needle") }));
            work.Add(new ThreadWorkSnapshot(url, index % 2 == 0 ? WorkIntent.Completed : WorkIntent.Suppressed,
                ThreadSourceStateValue.Archived, null, false, Array.Empty<MediaWork>()));
        }
        ArchiveThreadQuery query = new("needle", Source: "fixture", Board: "a", HasPosts: true, Sort: ArchiveThreadSort.Title);
        IReadOnlySet<string>? completed = ArchivePage.CaptureExportWorkUrls(4, work);
        work.Clear(); // Later session changes must not change the captured eligibility.
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(directory.Path);
        ResourceId[] all = await ArchivePage.ResolveExportResultIdsAsync(reader, query, null, CancellationToken.None);
        ResourceId[] filtered = await ArchivePage.ResolveExportResultIdsAsync(reader, query, completed, CancellationToken.None);
        Assert.Equal(300, all.Length);
        Assert.Equal(150, filtered.Length);
        Assert.Equal(Enumerable.Range(0, 300).Select(index => index.ToString()), all.Select(id => id.Thread));
        Assert.Equal(Enumerable.Range(0, 150).Select(index => (index * 2).ToString()), filtered.Select(id => id.Thread));
        Assert.Empty(await ArchivePage.ResolveExportResultIdsAsync(reader, query with { Text = "absent" }, null, CancellationToken.None));
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ArchivePage.ResolveExportResultIdsAsync(reader, query, completed, cancellation.Token));
    }

    [Fact]
    public void NoWorkFilterIncludesThreadsWithoutAWorkSnapshot()
    {
        Assert.Null(ArchivePage.CaptureExportWorkUrls(0, Array.Empty<ThreadWorkSnapshot>()));
        Assert.Empty(ArchivePage.CaptureExportWorkUrls(4, Array.Empty<ThreadWorkSnapshot>())!);
    }
}
