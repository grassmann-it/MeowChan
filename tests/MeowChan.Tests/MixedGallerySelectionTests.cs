using System.Security.Cryptography;
using MeowChan.Archive;
using MeowChan.Downloader;
using MeowChan.Gallery;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Storage;

namespace MeowChan.Tests;

public sealed class MixedGallerySelectionTests
{
    private static readonly ResourceId Id = new("stub", "g", "123");
    private const string Url = "https://stub.test/g/thread/123";

    [Fact]
    public async Task MixedSelectionPreservesRepeatedVideoPostAssociationsAndDefaultImageOrder()
    {
        using TempDirectory directory = new();
        using StubImageBoard provider = new();
        ProbeThread owner = new(provider.Board, new Uri(Url));
        MediaStore store = new();
        await SaveAsync(store, provider, owner, directory.Path, "image", "picture.PNG", [1, 2, 3]);
        await SaveAsync(store, provider, owner, directory.Path, "video", "clip.WEBM", [4, 5, 6]);
        await SaveAsync(store, provider, owner, directory.Path, "document", "notes.pdf", [7, 8, 9]);
        await SaveAsync(store, provider, owner, directory.Path, "unknown", "opaque.custom", [10, 11, 12]);
        MediaAssetSnapshot manifest = await MediaAssetReader.OpenAsync(directory.Path);
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(directory.Path, lease);
        await catalog.CaptureAsync(new CapturedThreadSnapshot(Id, Url, "Mixed", ThreadSourceState.Archived,
            DateTimeOffset.UtcNow, directory.Path,
            [new CapturedPost("123", 0, PostFieldPresence.All, Attachments:
                [Attachment("image", 0, [1, 2, 3]), Attachment("video", 1, [4, 5, 6]), Attachment("document", 2, [7, 8, 9])]),
             new CapturedPost("124", 1, PostFieldPresence.All, Attachments:
                [Attachment("video", 0, [4, 5, 6]), Attachment("unknown", 1, [10, 11, 12]), Attachment("video", 2, [4, 5, 6])])]));
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(directory.Path);
        ArchivedThreadView thread = (await reader.ReadThreadAsync(Id))!;
        GalleryThreadResult images = GallerySelection.Build(thread, [manifest]);
        // Unknown suffixes retain the existing image-decoder fallback in the default selection.
        Assert.Equal(new[] { "image", "unknown" }, images.Entries.Select(entry => entry.Asset.MediaIdentifier));
        GalleryThreadResult mixed = GallerySelection.Build(thread, [manifest], includeOtherMedia: true);
        Assert.Equal(new[] { "image", "video", "document", "video", "unknown", "video" },
            mixed.Entries.Select(entry => entry.Asset.MediaIdentifier));
        Assert.Equal(new[] { "123", "123", "123", "124", "124", "124" }, mixed.Entries.Select(entry => entry.Post!.PostId));
        Assert.Equal(new[] { 0, 1, 2, 0, 1, 2 }, mixed.Entries.Select(entry => entry.Attachment!.Position));
        Assert.Equal(images.Entries.Select(entry => entry.EntryKey),
            mixed.Entries.Where(entry => entry.Kind is SavedMediaKind.Image or SavedMediaKind.Unknown).Select(entry => entry.EntryKey));
        GalleryEntry[] videos = mixed.Entries.Where(entry => entry.Kind == SavedMediaKind.Video).ToArray();
        Assert.Equal(3, videos.Length);
        Assert.Single(videos.Select(entry => entry.Key).Distinct());
        Assert.Equal(3, videos.Select(entry => entry.EntryKey).Distinct().Count());
        Assert.All(mixed.Entries, entry =>
        {
            Assert.Same(manifest, entry.Manifest);
            Assert.Equal(entry.Asset.MediaIdentifier, entry.Attachment!.MediaId);
            Assert.Equal(entry.Asset.AssetId, entry.Key.AssetId);
            Assert.Equal(Url, entry.ThreadUrl);
            Assert.False(entry.Historical);
            Assert.False(entry.Unassigned);
        });
        Assert.Equal(SavedMediaKind.Other, mixed.Entries.Single(entry => entry.Asset.MediaIdentifier == "document").Kind);
    }

    [Theory]
    [InlineData("old.webm", "current.pdf", SavedMediaKind.Video, SavedMediaKind.Other)]
    [InlineData("old.pdf", "current.custom", SavedMediaKind.Other, SavedMediaKind.Unknown)]
    [InlineData("old.custom", "current.png", SavedMediaKind.Unknown, SavedMediaKind.Image)]
    public async Task HistoricalAttachmentMatchingUsesRecordedBytesIndependentlyOfRendererKind(
        string oldName, string currentName, SavedMediaKind oldKind, SavedMediaKind currentKind)
    {
        using TempDirectory directory = new();
        using StubImageBoard provider = new();
        ProbeThread owner = new(provider.Board, new Uri(Url));
        MediaStore store = new();
        byte[] oldBytes = [1, 2, 3], currentBytes = [3, 2, 1];
        await SaveAsync(store, provider, owner, directory.Path, "shared", oldName, oldBytes);
        await SaveAsync(store, provider, owner, directory.Path, "shared", currentName, currentBytes);
        MediaAssetSnapshot manifest = await MediaAssetReader.OpenAsync(directory.Path);
        Assert.Equal(2, manifest.Count);
        MediaAssetReference historical = manifest.GetPage().Single(asset => !asset.IsCurrent);
        MediaAssetReference current = manifest.GetPage().Single(asset => asset.IsCurrent);
        Assert.NotEqual(historical.AssetId, current.AssetId);
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(directory.Path, lease);
        await catalog.CaptureAsync(new CapturedThreadSnapshot(Id, Url, "History", ThreadSourceState.Archived,
            DateTimeOffset.UtcNow, directory.Path,
            [new CapturedPost("123", 0, PostFieldPresence.All, Attachments: [Attachment("shared", 2, oldBytes)]),
             new CapturedPost("124", 1, PostFieldPresence.All, Attachments: [Attachment("shared", 1, oldBytes)])]));
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(directory.Path);
        ArchivedThreadView thread = (await reader.ReadThreadAsync(Id))!;
        GalleryThreadResult selection = GallerySelection.Build(thread, [manifest], includeOtherMedia: true);
        GalleryEntry[] assigned = selection.Entries.Where(entry => !entry.Unassigned).ToArray();
        Assert.Equal(2, assigned.Length);
        Assert.All(assigned, entry =>
        {
            Assert.Equal(historical.AssetId, entry.Asset.AssetId);
            Assert.True(entry.Historical);
            Assert.Equal(oldKind, entry.Kind);
        });
        Assert.Equal(new[] { "123", "124" }, assigned.Select(entry => entry.Post!.PostId));
        Assert.Equal(new[] { 2, 1 }, assigned.Select(entry => entry.Attachment!.Position));
        Assert.Equal(2, assigned.Select(entry => entry.EntryKey).Distinct().Count());
        GalleryEntry unassignedCurrent = Assert.Single(selection.Entries, entry => entry.Unassigned);
        Assert.Equal(current.AssetId, unassignedCurrent.Asset.AssetId);
        Assert.Equal(currentKind, unassignedCurrent.Kind);
        Assert.False(unassignedCurrent.Historical);
        GalleryThreadResult allVersions = GallerySelection.Build(thread, [manifest], allSavedVersions: true, includeOtherMedia: true);
        Assert.Equal(selection.Entries.Select(entry => entry.EntryKey), allVersions.Entries.Select(entry => entry.EntryKey));
        await using Stream verified = await manifest.OpenVerifiedReadAsync(historical.AssetId);
        Assert.Equal(oldBytes, await ReadAllAsync(verified));
        await catalog.CaptureAsync(new CapturedThreadSnapshot(Id, Url, "Current", ThreadSourceState.Archived,
            DateTimeOffset.UtcNow, directory.Path,
            [new CapturedPost("123", 0, PostFieldPresence.All, Attachments: [Attachment("shared", 2, currentBytes)]),
             new CapturedPost("124", 1, PostFieldPresence.All, Attachments: [Attachment("shared", 1, currentBytes)])]));
        ArchivedThreadView updated = (await reader.ReadThreadAsync(Id))!;
        GalleryThreadResult currentOnly = GallerySelection.Build(updated, [manifest], includeOtherMedia: true);
        Assert.Equal(2, currentOnly.Entries.Count);
        Assert.All(currentOnly.Entries, entry =>
        {
            Assert.Equal(current.AssetId, entry.Key.AssetId);
            Assert.False(entry.Historical);
            Assert.False(entry.Unassigned);
        });
        GalleryThreadResult currentAndHistory = GallerySelection.Build(updated, [manifest], allSavedVersions: true, includeOtherMedia: true);
        Assert.Equal(3, currentAndHistory.Entries.Count);
        GalleryEntry oldUnassigned = Assert.Single(currentAndHistory.Entries, entry => entry.Unassigned);
        Assert.Equal(historical.AssetId, oldUnassigned.Key.AssetId);
        Assert.Equal(oldKind, oldUnassigned.Kind);
        Assert.True(oldUnassigned.Historical);
        Assert.Null(oldUnassigned.Post);
        Assert.Null(oldUnassigned.Attachment);
        Assert.All(assigned, entry => Assert.Equal(historical.AssetId, entry.Key.AssetId));
    }

    [Theory]
    [InlineData("/saved/PICTURE.JpEg", SavedMediaKind.Image)]
    [InlineData("/saved/animation.gif", SavedMediaKind.Image)]
    [InlineData("/saved/clip.WEBM", SavedMediaKind.Video)]
    [InlineData("/saved/clip.mp4", SavedMediaKind.Video)]
    [InlineData("/saved/audio.ogg", SavedMediaKind.Other)]
    [InlineData("/saved/document.PDF", SavedMediaKind.Other)]
    [InlineData("/saved/archive.zip", SavedMediaKind.Other)]
    [InlineData("/saved/opaque.custom", SavedMediaKind.Unknown)]
    [InlineData("/saved/no-extension", SavedMediaKind.Unknown)]
    public void SavedMediaKindsDescribeRendererChoiceForSavedFileNames(string path, SavedMediaKind expected)
        => Assert.Equal(expected, SavedMediaTypes.Classify(path));

    private static CapturedAttachment Attachment(string id, int position, byte[] bytes) =>
        new(position, id, ExpectedSize: bytes.Length, ExpectedMd5: Convert.ToBase64String(MD5.HashData(bytes)));

    private static async Task SaveAsync(MediaStore store, StubImageBoard provider, ProbeThread owner,
        string directory, string id, string originalName, byte[] bytes)
    {
        // Keep the same requested path across revisions; renderer metadata must not determine identity.
        DownloadItem item = new(new Uri("https://stub.test/" + id), Path.Combine(directory, id + ".bin"),
            provider, owner, id, bytes.Length, Convert.ToBase64String(MD5.HashData(bytes)), originalName);
        await using MemoryStream body = new(bytes);
        await store.CommitAsync(item, body, CancellationToken.None);
    }

    private static async Task<byte[]> ReadAllAsync(Stream stream)
    {
        using MemoryStream result = new();
        await stream.CopyToAsync(result);
        return result.ToArray();
    }
}
