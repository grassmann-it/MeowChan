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
    public ResolvedMediaAttachment? AttachmentResolution { get; init; }

    /// <summary>
    /// True when this entry is a loose view of its file (unassigned, or associated with a post only by the legacy
    /// metadata match) and no entry of the same build shows the same manifest asset bound to a post version. Only
    /// such entries can carry a local (occurrence) media tag; a file with a bound view stays in the bound namespace.
    /// Set by <see cref="GallerySelection.Build"/>; an entry made elsewhere is never locally taggable.
    /// </summary>
    internal bool LocalTagEligible { get; init; }
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
        // A file shown bound to a post version anywhere in this thread belongs to the bound tag namespace;
        // its other (legacy) views must not open a second, local namespace for the same manifest asset.
        // A damaged manifest hides bindings (the resolver refuses), so then nothing is offered as loose.
        HashSet<GalleryAssetKey> bound = entries.Where(entry => entry.AttachmentResolution is { IsLegacy: false })
            .Select(entry => entry.Key).ToHashSet();
        bool reliable = snapshots.All(snapshot => snapshot.Status != MediaManifestStatus.Corrupt);
        for (int index = 0; reliable && index < entries.Count; index++)
            if (entries[index].AttachmentResolution is null or { IsLegacy: true } && !bound.Contains(entries[index].Key))
                entries[index] = entries[index] with { LocalTagEligible = true };

        // The archive menu opens a whole thread, so its first gallery item must be the first
        // post's first saved medium. Keep that invariant even when manifests arrive in a
        // different order or a legacy binding was appended after the loose-file pass.
        // Unassigned files remain useful, but come after post-bound entries.
        entries.Sort((left, right) =>
        {
            int leftPost = left.Post == null ? int.MaxValue : thread.FindPostIndex(left.Post.PostId);
            int rightPost = right.Post == null ? int.MaxValue : thread.FindPostIndex(right.Post.PostId);
            int comparison = leftPost.CompareTo(rightPost);
            if (comparison != 0) return comparison;
            comparison = (left.Attachment?.Position ?? int.MaxValue).CompareTo(right.Attachment?.Position ?? int.MaxValue);
            if (comparison != 0) return comparison;
            comparison = string.Compare(left.Asset.AssetId, right.Asset.AssetId, StringComparison.Ordinal);
            return comparison != 0 ? comparison : string.Compare(left.Key.Directory, right.Key.Directory, StringComparison.Ordinal);
        });
        return new GalleryThreadResult(Array.AsReadOnly(entries.ToArray()), Array.AsReadOnly(warnings.ToArray()));

        void AppendPost(CapturedPost post)
        {
            foreach (CapturedAttachment attachment in (post.Attachments ?? Array.Empty<CapturedAttachment>())
                .OrderBy(value => value.Position))
            {
                if (attachment.IsDeleted || attachment.MediaId == null) continue;
                ResolvedMediaAttachment? match = MediaAttachmentResolver.Resolve(snapshots, thread.Summary.Id, post.PostId, attachment);
                if (match == null || !images.Any(pair => ReferenceEquals(pair.Manifest, match.Snapshot) && pair.Asset.AssetId == match.Asset.AssetId)) continue;
                GalleryAssetKey key = new(thread.Summary.Id, match.Snapshot.Directory, match.Asset.AssetId);
                represented.Add(key);
                // The same bytes may have several exact post bindings; retain each binding's provenance.
                entries.Add(new GalleryEntry(key, match.Snapshot, match.Asset, post, attachment,
                    !match.Asset.IsCurrent, false) { ThreadUrl = thread.Summary.Url, AttachmentResolution = match });
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
