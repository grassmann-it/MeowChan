using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MeowChan.Archive;
using MeowChan.Storage;

namespace MeowChan.Gallery;

public sealed record GalleryAssetKey(ResourceId Thread, string Directory, string AssetId);
public sealed record GalleryEntryKey(GalleryAssetKey Asset, string? PostId, int? AttachmentPosition);

/// <summary>A frozen reference to recorded bytes and, where known, the latest captured post.</summary>
public sealed record GalleryEntry(GalleryAssetKey Key, MediaAssetSnapshot Manifest,
    MediaAssetReference Asset, CapturedPost? Post, CapturedAttachment? Attachment,
    bool Historical, bool Unassigned)
{
    public SavedMediaKind Kind => SavedMediaTypes.Classify(Asset.OriginalFileName ?? Asset.Path);
    public GalleryEntryKey EntryKey => new(Key, Post?.PostId, Attachment?.Position);
    public string ThreadUrl { get; init; } = string.Empty;
}

public sealed record GalleryThreadResult(IReadOnlyList<GalleryEntry> Entries, IReadOnlyList<string> Warnings);

/// <summary>Builds one thread's image order from passive archive and manifest snapshots.</summary>
public static class GallerySelection
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif" };
    private static readonly HashSet<string> NonImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".mp4", ".webm", ".mov", ".mkv", ".avi", ".mp3", ".wav", ".ogg", ".flac", ".pdf", ".zip" };

    public static GalleryThreadResult Build(ArchivedThreadView thread,
        IEnumerable<MediaAssetSnapshot> manifests, bool allSavedVersions = false, bool includeOtherMedia = false)
    {
        ArgumentNullException.ThrowIfNull(thread);
        ArgumentNullException.ThrowIfNull(manifests);
        MediaAssetSnapshot[] snapshots = manifests.ToArray();
        List<(MediaAssetSnapshot Manifest, MediaAssetReference Asset)> images = new();
        List<string> warnings = new();
        foreach (MediaAssetSnapshot snapshot in snapshots)
        {
            if (snapshot.Warning != null) warnings.Add(snapshot.Directory + ": " + snapshot.Warning);
            for (int offset = 0; offset < snapshot.Count; offset += 500)
                images.AddRange(snapshot.GetPage(offset, 500).Where(asset => includeOtherMedia || IsImage(asset))
                    .Select(asset => (snapshot, asset)));
        }
        List<GalleryEntry> entries = new();
        HashSet<GalleryAssetKey> represented = new();
        foreach (CapturedPost post in thread.GetPosts(0, Math.Min(500, Math.Max(1, thread.PostCount))))
            AppendPost(post);
        // The public post page contract is bounded, but the thread view is immutable. Walk every page.
        for (int offset = 500; offset < thread.PostCount; offset += 500)
            foreach (CapturedPost post in thread.GetPosts(offset, 500)) AppendPost(post);
        foreach ((MediaAssetSnapshot manifest, MediaAssetReference asset) in images
            .Where(pair => pair.Asset.IsCurrent || allSavedVersions)
            .OrderBy(pair => pair.Asset.MediaIdentifier, StringComparer.Ordinal)
            .ThenBy(pair => pair.Asset.RequestedFileName, StringComparer.Ordinal)
            .ThenBy(pair => pair.Asset.AssetId, StringComparer.Ordinal))
        {
            GalleryAssetKey key = new(thread.Summary.Id, manifest.Directory, asset.AssetId);
            if (represented.Add(key)) entries.Add(new GalleryEntry(key, manifest, asset, null, null,
                !asset.IsCurrent, true) { ThreadUrl = thread.Summary.Url });
        }
        return new GalleryThreadResult(Array.AsReadOnly(entries.ToArray()), Array.AsReadOnly(warnings.ToArray()));

        void AppendPost(CapturedPost post)
        {
            foreach (CapturedAttachment attachment in (post.Attachments ?? Array.Empty<CapturedAttachment>())
                .OrderBy(value => value.Position))
            {
                if (attachment.IsDeleted || attachment.MediaId == null) continue;
                (MediaAssetSnapshot Manifest, MediaAssetReference Asset)? match = images.LastOrDefault(pair =>
                    pair.Asset.MediaIdentifier == attachment.MediaId && pair.Asset.Variant == attachment.Variant
                    && (attachment.ExpectedMd5 == null || string.Equals(pair.Asset.Md5, attachment.ExpectedMd5, StringComparison.OrdinalIgnoreCase))
                    && (attachment.ExpectedSize == null || pair.Asset.Length == attachment.ExpectedSize));
                if (match == null || match.Value.Asset == null) continue;
                GalleryAssetKey key = new(thread.Summary.Id, match.Value.Manifest.Directory, match.Value.Asset.AssetId);
                represented.Add(key);
                // The same asset may appear in multiple posts; retain each post association in thread order.
                entries.Add(new GalleryEntry(key, match.Value.Manifest, match.Value.Asset, post, attachment,
                    !match.Value.Asset.IsCurrent, false) { ThreadUrl = thread.Summary.Url });
            }
        }
    }

    private static bool IsImage(MediaAssetReference asset)
    {
        // Content sniffing is performed by the bounded decoder after verification. A recognized
        // image suffix avoids presenting known videos/audio as failed images in navigation.
        string extension = Path.GetExtension(asset.OriginalFileName ?? asset.Path);
        return ImageExtensions.Contains(extension) || !NonImageExtensions.Contains(extension);
    }
}
