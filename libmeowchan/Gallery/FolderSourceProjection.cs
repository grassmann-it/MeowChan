using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.FolderSources;
using MeowChan.Storage;

namespace MeowChan.Gallery;

/// <summary>
/// Index entries of the files of read-only folder sources (PLAN-POWER-TAGGEN §2.2), derived from the shadow catalog only:
/// no file is opened. Each active item is one loose, locally taggable entry under its root's borrowed identity, in its virtual
/// media folder; an unhashed item is present without a digest (it can be shown, and is hashed when it is first opened or
/// written to). A missing item is a missing entry, never an invented present one.
/// </summary>
internal static class FolderSourceProjection
{
    internal static IReadOnlyList<MediaLibraryEntry> EntriesOf(FolderSourceCatalogSnapshot catalog, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        List<MediaLibraryEntry> entries = new(catalog.Items.Count);
        foreach (FolderSourceItem item in catalog.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (catalog.Root(item.RootId) is not { } root) continue;
            entries.Add(EntryOf(catalog.VirtualRoot, root, item));
        }
        return entries.AsReadOnly();
    }

    internal static MediaLibraryEntry EntryOf(string virtualRoot, FolderSourceRoot root, FolderSourceItem item)
    {
        string itemId = FolderSourceIdentity.Format(item.Id);
        if (item.State == FolderSourceItemState.Missing)
            return new MediaLibraryEntry(root.Thread, root.Name, 0, item.FirstSeenUtc, item.Modified, null, null, null, null, null,
                null, null, null, null, item.MediaIdentifier, FolderSourceIdentity.Variant, item.Name, item.Kind,
                MediaLibraryPresence.Missing, true, false) { FolderItemId = itemId };
        string directory = Path.Combine(virtualRoot, FolderSourceIdentity.Format(root.Id), itemId);
        return new MediaLibraryEntry(root.Thread, root.Name, 0, item.FirstSeenUtc, item.Modified, null, null, null, null, null,
            directory, item.AssetId, item.Sha256, item.Length, item.MediaIdentifier, FolderSourceIdentity.Variant, item.Name, item.Kind,
            MediaLibraryPresence.Present, Unassigned: true, Historical: false,
            // A folder file is a loose local file: its virtual slot is its tag locator.
            LocalSlot: item.RequestedFileName) { FolderItemId = itemId };
    }
}

/// <summary>
/// Turns an index entry of a folder source file back into the ordinary gallery entry: the user's file in its virtual manifest,
/// as a loose file without a post. Opening the manifest hashes an unhashed file first; an entry whose catalog row or bytes no
/// longer agree with the index is left out rather than guessed.
/// </summary>
internal static class FolderSourceMediaGallery
{
    internal static async Task<GalleryEntry?> ResolveAsync(MediaLibraryEntry entry,
        Func<string, CancellationToken, Task<MediaAssetSnapshot>> openManifest, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(openManifest);
        if (entry.FolderItemId == null || entry.Presence != MediaLibraryPresence.Present || entry.Directory == null || entry.AssetId == null
            || !FolderSourceIdentity.IsFolderThread(entry.Thread)) return null;
        MediaAssetSnapshot manifest;
        try { manifest = await openManifest(entry.Directory, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException) { return null; }
        if (manifest.Status != MediaManifestStatus.Available || manifest.FindById(entry.AssetId) is not { } asset
            || asset.Length != entry.Length || asset.MediaIdentifier != entry.MediaId || asset.RequestedFileName != entry.LocalSlot
            || entry.Sha256 != null && !string.Equals(asset.Sha256, entry.Sha256, StringComparison.OrdinalIgnoreCase)) return null;
        return new GalleryEntry(new GalleryAssetKey(entry.Thread, manifest.Directory, asset.AssetId), manifest, asset, null, null,
            false, true) { LocalTagEligible = true };
    }

    /// <summary>The entry with the digest its gallery entry proved (an unhashed entry learns it when it is opened).</summary>
    internal static MediaLibraryEntry WithDigest(MediaLibraryEntry entry, GalleryEntry resolved) =>
        entry.FolderItemId != null && entry.Sha256 == null ? entry with { Sha256 = resolved.Asset.Sha256 } : entry;
}
