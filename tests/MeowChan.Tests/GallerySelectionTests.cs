using System.Security.Cryptography;
using System.Text.Json;
using MeowChan.Archive;
using MeowChan.Gallery;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Storage;

namespace MeowChan.Tests;

public sealed class GallerySelectionTests
{
    [Fact]
    public async Task SearchSelectionCrossesThreadPagesAndFreezesOrderWhileThreadSequenceCrossesPostPages()
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(directory.Path, lease);
        ResourceId first = new("4chan", "g", "1000");
        // More than both the 50-post UI page and the 500-post public reader page.
        CapturedPost[] posts = Enumerable.Range(0, 502).Select(index => new CapturedPost(
            (1000 + index).ToString(), index, PostFieldPresence.All, Html: index == 0 ? "needle" : "other post",
            Attachments: new[] { new CapturedAttachment(0, "shared"), new CapturedAttachment(1, "shared") })).ToArray();
        for (int index = 0; index < 52; index++)
        {
            ResourceId id = new("4chan", "g", (1000 + index).ToString());
            await catalog.CaptureAsync(new CapturedThreadSnapshot(id, $"https://boards.4chan.org/g/thread/{id.Thread}",
                "Same title", ThreadSourceState.Archived, DateTimeOffset.UtcNow, directory.Path,
                index == 0 ? posts : new[] { new CapturedPost(id.Thread!, 0, PostFieldPresence.All, Html: "needle") }));
        }
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(directory.Path);
        ArchiveThreadQuery query = new("needle", Sort: ArchiveThreadSort.Title);
        IReadOnlyList<ResourceId> frozen = await reader.SelectThreadIdsAsync(query);
        Assert.Equal(52, frozen.Count);
        Assert.Equal(frozen, await reader.SelectThreadIdsAsync(query));
        Assert.Equal(52, frozen.Distinct().Count());
        await catalog.CaptureAsync(new CapturedThreadSnapshot(new ResourceId("4chan", "g", "9999"),
            "https://boards.4chan.org/g/thread/9999", "Same title", ThreadSourceState.Archived,
            DateTimeOffset.UtcNow, directory.Path, new[] { new CapturedPost("9999", 0, PostFieldPresence.All, Html: "needle") }));
        Assert.Equal(52, frozen.Count);
        Assert.Equal(53, (await reader.SelectThreadIdsAsync(query)).Count);
        MediaAssetSnapshot manifest = await CreateManifestAsync(directory.Path);
        ArchivedThreadView thread = (await reader.ReadThreadAsync(first))!;
        GalleryThreadResult sequence = GallerySelection.Build(thread, new[] { manifest });
        Assert.Equal(1004, sequence.Entries.Count);
        Assert.Equal("1050", sequence.Entries[100].Post!.PostId);
        Assert.Equal("1501", sequence.Entries[^1].Post!.PostId);
        Assert.Equal(1, sequence.Entries[^1].Attachment!.Position);
        Assert.Equal(1004, sequence.Entries.Select(entry => entry.EntryKey).Distinct().Count());
        Assert.Single(sequence.Entries.Select(entry => entry.Key).Distinct());
        // A second thread with the same asset filename and ID remains a different identity.
        ArchivedThreadView other = (await reader.ReadThreadAsync(frozen.First(id => id != first)))!;
        GalleryEntry otherEntry = Assert.Single(GallerySelection.Build(other, new[] { manifest }).Entries);
        Assert.NotEqual(sequence.Entries[0].Key, otherEntry.Key);
        await catalog.CaptureAsync(new CapturedThreadSnapshot(first, thread.Summary.Url, "Same title",
            ThreadSourceState.Archived, DateTimeOffset.UtcNow, directory.Path,
            new[] { new CapturedPost("2000", 502, PostFieldPresence.All, Attachments: new[] { new CapturedAttachment(0, "shared") }) }));
        Assert.Equal(1004, sequence.Entries.Count);
        Assert.Equal(1005, GallerySelection.Build((await reader.ReadThreadAsync(first))!, new[] { manifest }).Entries.Count);
    }

    private static async Task<MediaAssetSnapshot> CreateManifestAsync(string directory)
    {
        byte[] bytes = [1, 2, 3];
        await File.WriteAllBytesAsync(Path.Combine(directory, "shared.png"), bytes);
        await File.WriteAllTextAsync(Path.Combine(directory, ".meowchan-manifest.json"), JsonSerializer.Serialize(new
        {
            Version = 1,
            Entries = new Dictionary<string, object>
            {
                ["shared.png"] = new { MediaIdentifier = "shared", SourceUrl = "https://offline.invalid/shared.png",
                    FileName = "shared.png", Length = bytes.Length, Sha256 = Convert.ToHexString(SHA256.HashData(bytes)),
                    Md5 = Convert.ToBase64String(MD5.HashData(bytes)) }
            }
        }));
        return await MediaAssetReader.OpenAsync(directory);
    }
}
