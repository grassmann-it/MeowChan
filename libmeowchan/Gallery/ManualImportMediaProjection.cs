using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Import;
using MeowChan.Storage;

namespace MeowChan.Gallery;

/// <summary>
/// Index entries and gallery entries of manually imported files (contract manual-import-v1 §7). Derived from the import
/// catalog and the batch manifests only. Each copied item becomes one loose, locally taggable entry under its batch's
/// borrowed identity; a file or record that cannot be read becomes a missing entry, never an invented present one.
/// Linked duplicates have no entry of their own: their bytes are already shown by the occurrences that own them.
/// </summary>
internal static class ManualImportMediaProjection
{
    internal static async Task<(IReadOnlyList<MediaLibraryEntry> Entries, int Unreadable)> EntriesAsync(ManualImportCatalogSnapshot catalog,
        Func<string, CancellationToken, Task<MediaAssetSnapshot>> openManifest, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(openManifest);
        List<MediaLibraryEntry> entries = new();
        int unreadable = 0;
        foreach (ManualImportBatch batch in catalog.Batches.OrderBy(batch => batch.CreatedAtUtc).ThenBy(batch => batch.Id))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ManualImportItem[] copied = batch.Items.Where(item => item.State == ManualImportItemState.Committed).ToArray();
            if (copied.Length == 0) continue;
            MediaAssetSnapshot? manifest = null;
            try { manifest = await openManifest(batch.Directory, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException) { unreadable++; }
            if (manifest is { Status: MediaManifestStatus.Corrupt }) unreadable++;
            foreach (ManualImportItem item in copied)
                entries.Add(EntryOf(batch, item, manifest is { Status: MediaManifestStatus.Available } && Matching(manifest, item) is { } asset ? asset : null));
        }
        return (entries, unreadable);
    }

    /// <summary>The manifest record of exactly this item: same asset id, requested name, media id, variant, length and digest.</summary>
    internal static MediaAssetReference? Matching(MediaAssetSnapshot manifest, ManualImportItem item) =>
        item.AssetId != null && manifest.FindById(item.AssetId) is { } asset && asset.RequestedFileName == item.RequestedFileName
        && asset.MediaIdentifier == item.MediaIdentifier && asset.Variant == ManualImportIdentity.Variant
        && asset.Length == item.Length && string.Equals(asset.Sha256, item.Sha256, StringComparison.OrdinalIgnoreCase) ? asset : null;

    private static MediaLibraryEntry EntryOf(ManualImportBatch batch, ManualImportItem item, MediaAssetReference? asset)
    {
        ResourceId thread = batch.Thread;
        DateTimeOffset last = batch.CompletedAtUtc ?? batch.CreatedAtUtc;
        string itemId = ManualImportIdentity.Format(item.Id);
        if (asset == null)
            return new MediaLibraryEntry(thread, batch.Name, 0, batch.CreatedAtUtc, last, null, null, null, null, null,
                null, null, null, null, item.MediaIdentifier, ManualImportIdentity.Variant, item.Name, item.Kind,
                MediaLibraryPresence.Missing, true, false) { ImportItemId = itemId };
        return new MediaLibraryEntry(thread, batch.Name, 0, batch.CreatedAtUtc, last, null, null, null, null, null,
            batch.Directory, asset.AssetId, asset.Sha256.ToLowerInvariant(), asset.Length, asset.MediaIdentifier, asset.Variant,
            item.Name, item.Kind, MediaLibraryPresence.Present, Unassigned: true, Historical: false,
            // Imported files are loose local files: their exact manifest slot is their tag locator.
            LocalSlot: asset.RequestedFileName) { ImportItemId = itemId };
    }
}

/// <summary>
/// Turns an index entry of an imported file back into the ordinary gallery entry: the copied file in its batch manifest,
/// as a loose file without a post. An entry whose catalog record, manifest record or bytes no longer agree with the index
/// is left out rather than guessed.
/// </summary>
internal static class ManualImportMediaGallery
{
    internal static async Task<GalleryEntry?> ResolveAsync(MediaLibraryEntry entry, ManualImportCatalogSnapshot? catalog,
        Func<string, CancellationToken, Task<MediaAssetSnapshot>> openManifest, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(openManifest);
        if (entry.ImportItemId is not { } itemText || catalog == null || entry.Presence != MediaLibraryPresence.Present
            || entry.Directory == null || entry.AssetId == null
            || !ManualImportIdentity.TryGetBatchId(entry.Thread, out Guid batchId) || !ManualImportIdentity.TryParseId(itemText, out Guid itemId))
            return null;
        ManualImportBatch? batch = catalog.Find(batchId);
        ManualImportItem? item = batch?.Find(itemId);
        if (batch == null || item is not { State: ManualImportItemState.Committed } || item.AssetId != entry.AssetId
            || !string.Equals(batch.Directory, entry.Directory, StringComparison.Ordinal)) return null;
        MediaAssetSnapshot manifest;
        try { manifest = await openManifest(entry.Directory, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException) { return null; }
        if (manifest.Status != MediaManifestStatus.Available || ManualImportMediaProjection.Matching(manifest, item) is not { } asset
            || !string.Equals(asset.Sha256, entry.Sha256, StringComparison.OrdinalIgnoreCase) || asset.Length != entry.Length) return null;
        return new GalleryEntry(new GalleryAssetKey(entry.Thread, manifest.Directory, asset.AssetId), manifest, asset, null, null,
            !asset.IsCurrent, true) { LocalTagEligible = true };
    }
}
