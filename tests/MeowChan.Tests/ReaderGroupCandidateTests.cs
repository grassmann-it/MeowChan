using MeowChan.Archive;
using MeowChan.Interfaces;
using MeowChan.Persistence;

namespace MeowChan.Tests;

public sealed class ReaderGroupCandidateTests
{
    [Fact]
    public async Task MetadataSearchIsBoundedOrderedAndNeverMatchesOnlyPostText()
    {
        using TempDirectory directory = new(); using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(directory.Path, lease);
        ResourceId first = new("source", "b", "000");
        for (int index = 0; index < 55; index++)
        {
            ResourceId id = first with { Thread = index.ToString("D3") };
            await catalog.CaptureAsync(new(id, "https://example.test/b/thread/" + id.Thread, "Metadata " + index,
                ThreadSourceState.Archived, DateTimeOffset.UtcNow, directory.Path,
                [new CapturedPost("1", 0, PostFieldPresence.Html, Html: "POST_ONLY_NEEDLE")]));
        }
        ResourceId empty = first with { Source = "other", Thread = "000" };
        await catalog.RegisterThreadAsync(new(empty, "https://example.test/b/thread/empty", "Empty watch", directory.Path, false));
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(directory.Path);
        ReaderGroupCandidatePage page = await reader.FindReaderGroupCandidatesAsync("", 50);
        Assert.Equal(50, page.Items.Count); Assert.True(page.HasMore); Assert.Equal(empty, page.Items[0].Id);
        Assert.Empty((await reader.FindReaderGroupCandidatesAsync("POST_ONLY_NEEDLE")).Items);
        Assert.Equal(first, Assert.Single((await reader.FindReaderGroupCandidatesAsync("source/b/000")).Items).Id);
        var resolved = await reader.ResolveReaderGroupMembersAsync([first, empty, first with { Thread = "absent" }]);
        Assert.Equal(new[] { true, false, false }, resolved.Select(item => item.HasLocalPosts));
        Assert.Equal(new[] { first, empty, first with { Thread = "absent" } }, resolved.Select(item => item.Id));
        Assert.False(Directory.Exists(Path.Combine(directory.Path, "media")));
    }
}
