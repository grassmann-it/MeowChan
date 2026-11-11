using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Favorites;
using MeowChan.Storage;
using MeowChan.Tags;

namespace MeowChan.Gallery;

/// <summary>
/// Identity of a tile across rebuilds: the bytes (hash and length) for saved files (the shown occurrence may change
/// with the filter, the bytes do not), the entry itself for a captured but never saved attachment.
/// </summary>
internal readonly record struct MediaLibraryTileKey(string? Bytes, MediaLibraryEntry? Entry)
{
    internal static MediaLibraryTileKey Of(MediaLibraryTile tile)
    {
        ArgumentNullException.ThrowIfNull(tile);
        return Of(tile.Entry);
    }

    /// <summary>The key of the tile an occurrence belongs to: any occurrence of equal bytes gives the key of their one tile.</summary>
    internal static MediaLibraryTileKey Of(MediaLibraryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return MediaLibraryGrouping.ByteKey(entry) is { } key ? new(key, null) : new(null, entry);
    }
}

/// <summary>Pure rules for acting on a selection of tiles. No IO; the page freezes the tiles before it calls these.</summary>
internal static class MediaLibrarySelection
{
    /// <summary>
    /// Positions in <paramref name="tiles"/> of the tiles that were selected before the list changed. A selected
    /// tile that is no longer shown is dropped, so an action can never reach a tile the user cannot see.
    /// </summary>
    internal static IReadOnlyList<int> Restore(IReadOnlyCollection<MediaLibraryTileKey> selected, IReadOnlyList<MediaLibraryTile> tiles)
    {
        ArgumentNullException.ThrowIfNull(selected);
        ArgumentNullException.ThrowIfNull(tiles);
        if (selected.Count == 0) return Array.Empty<int>();
        HashSet<MediaLibraryTileKey> wanted = selected.ToHashSet();
        return Enumerable.Range(0, tiles.Count).Where(index => wanted.Contains(MediaLibraryTileKey.Of(tiles[index]))).ToArray();
    }

    /// <summary>The tiles at the given positions in page order; positions outside the list are ignored.</summary>
    internal static IReadOnlyList<MediaLibraryTile> Pick(IReadOnlyList<MediaLibraryTile> tiles, IEnumerable<int> positions)
    {
        ArgumentNullException.ThrowIfNull(tiles);
        ArgumentNullException.ThrowIfNull(positions);
        return positions.Where(index => index >= 0 && index < tiles.Count).Distinct().Order().Select(index => tiles[index]).ToArray();
    }

    /// <summary>
    /// Gallery entries of the selection, one per tile: the occurrence the tile shows. Tiles without a saved file
    /// have nothing to display and are counted in <c>NotSaved</c> instead of being dropped silently.
    /// </summary>
    internal static (IReadOnlyList<MediaLibraryEntry> Entries, int NotSaved) GalleryEntries(IEnumerable<MediaLibraryTile> tiles)
    {
        ArgumentNullException.ThrowIfNull(tiles);
        MediaLibraryTile[] all = tiles.ToArray();
        MediaLibraryEntry[] present = all.Select(tile => tile.Entry).Where(entry => entry.Presence == MediaLibraryPresence.Present).ToArray();
        return (present, all.Length - present.Length);
    }
}

/// <summary>Why one occurrence cannot take a media tag.</summary>
internal enum MediaTagBlock { None, NotSaved, NoPostVersion }

/// <summary>One occurrence offered to the tag dialog and the exact tag target it would be written to (null: not taggable).</summary>
internal sealed record MediaTagCandidate(MediaLibraryEntry Entry, PrivateMediaTagTarget? Target)
{
    internal MediaTagBlock Block => Target != null ? MediaTagBlock.None
        : Entry.Presence != MediaLibraryPresence.Present ? MediaTagBlock.NotSaved : MediaTagBlock.NoPostVersion;
}

/// <summary>
/// Which media tag targets a "tag the selection" action touches. Tags stay per occurrence: a grouped tile
/// offers every occurrence of its bytes, the caller decides which of them take part, and thread tags are never involved.
/// </summary>
internal static class MediaLibraryTagging
{
    /// <summary>Every occurrence of every selected tile in tile order, each once.</summary>
    internal static IReadOnlyList<MediaTagCandidate> Candidates(IEnumerable<MediaLibraryTile> tiles)
    {
        ArgumentNullException.ThrowIfNull(tiles);
        return tiles.SelectMany(tile => tile.Occurrences).Distinct()
            .Select(entry => new MediaTagCandidate(entry, MediaLibraryFilterEngine.TagTarget(entry))).ToArray();
    }

    /// <summary>Targets of the chosen candidates that do not carry the tag yet.</summary>
    internal static IReadOnlyList<PrivateMediaTagTarget> TargetsToAdd(PrivateTagStoreSnapshot snapshot, TagId tag,
        IEnumerable<MediaTagCandidate> chosen)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(chosen);
        HashSet<PrivateMediaTagTarget> has = Carrying(snapshot, tag);
        return Targets(chosen).Where(target => !has.Contains(target)).ToArray();
    }

    /// <summary>Targets of the chosen candidates that carry the tag; the others are left alone.</summary>
    internal static IReadOnlyList<PrivateMediaTagTarget> TargetsToRemove(PrivateTagStoreSnapshot snapshot, TagId tag,
        IEnumerable<MediaTagCandidate> chosen)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(chosen);
        HashSet<PrivateMediaTagTarget> has = Carrying(snapshot, tag);
        return Targets(chosen).Where(has.Contains).ToArray();
    }

    private static HashSet<PrivateMediaTagTarget> Carrying(PrivateTagStoreSnapshot snapshot, TagId tag) =>
        snapshot.MediaAssignments.Where(item => (TagId)item.TagId == tag).Select(item => item.Target).ToHashSet();

    private static IEnumerable<PrivateMediaTagTarget> Targets(IEnumerable<MediaTagCandidate> chosen) =>
        chosen.Select(candidate => candidate.Target).OfType<PrivateMediaTagTarget>().Distinct();
}

/// <summary>Originals to export for a selection, and how many tiles could not be offered.</summary>
internal sealed record MediaLibraryExportPlan(IReadOnlyList<FavoriteExportAsset> Assets, int Unavailable);

/// <summary>
/// What an export of tiles would copy by the index alone: tiles with an original to offer, their total bytes, and
/// the tiles that have none. No disk is read, so the page can ask before it starts anything large.
/// </summary>
internal readonly record struct MediaLibraryExportSize(int Files, long Bytes, int NotOffered)
{
    /// <summary>Above this many files "export all" asks first.</summary>
    internal const int ConfirmAboveFiles = 500;
    /// <summary>Above this many bytes (2 GiB) "export all" asks first.</summary>
    internal const long ConfirmAboveBytes = 2L * 1024 * 1024 * 1024;

    /// <summary>True when more than <paramref name="maxFiles"/> files or more than <paramref name="maxBytes"/> bytes would be copied.</summary>
    internal bool Exceeds(int maxFiles = ConfirmAboveFiles, long maxBytes = ConfirmAboveBytes) => Files > maxFiles || Bytes > maxBytes;
}

internal static class MediaLibraryExport
{
    // The index facts BuildAsync needs before it opens a manifest; the same test decides count and plan, so they cannot drift.
    private static bool HasOriginal(MediaLibraryEntry entry) => entry.Presence == MediaLibraryPresence.Present && entry.Directory != null
        && entry.AssetId != null && entry.MediaId != null && entry.Variant == "original";

    /// <summary>Count and bytes of the originals the tiles would offer, from the index only.</summary>
    internal static MediaLibraryExportSize Measure(IEnumerable<MediaLibraryTile> tiles)
    {
        ArgumentNullException.ThrowIfNull(tiles);
        int files = 0, notOffered = 0;
        long bytes = 0;
        foreach (MediaLibraryTile tile in tiles)
        {
            if (!HasOriginal(tile.Entry)) { notOffered++; continue; }
            files++;
            bytes += tile.Entry.Length ?? 0;
        }
        return new MediaLibraryExportSize(files, bytes, notOffered);
    }

    /// <summary>
    /// One original per tile: the occurrence the tile shows, read from its manifest. A tile that is not saved,
    /// has no original variant, or whose bytes no longer match the index counts as unavailable instead of
    /// being exported from somewhere else. A file that is gone from the disk is reported by the exporter.
    /// </summary>
    internal static async Task<MediaLibraryExportPlan> BuildAsync(IEnumerable<MediaLibraryTile> tiles,
        Func<string, CancellationToken, Task<MediaAssetSnapshot>> openManifest, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tiles);
        ArgumentNullException.ThrowIfNull(openManifest);
        List<FavoriteExportAsset> assets = new();
        int unavailable = 0;
        Dictionary<string, MediaAssetSnapshot?> manifests = new(StringComparer.Ordinal);
        foreach (MediaLibraryTile tile in tiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            MediaLibraryEntry entry = tile.Entry;
            if (!HasOriginal(entry)) { unavailable++; continue; }
            if (!manifests.TryGetValue(entry.Directory, out MediaAssetSnapshot? manifest))
            {
                try { manifest = await openManifest(entry.Directory, cancellationToken).ConfigureAwait(false); }
                catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException or System.IO.InvalidDataException) { manifest = null; }
                manifests[entry.Directory] = manifest;
            }
            MediaAssetReference? asset = manifest?.FindById(entry.AssetId);
            if (manifest == null || asset == null || !string.Equals(asset.Sha256, entry.Sha256, StringComparison.OrdinalIgnoreCase)
                || asset.MediaIdentifier != entry.MediaId || asset.Variant != entry.Variant) { unavailable++; continue; }
            assets.Add(FavoriteExportAsset.From(entry.Thread, manifest, asset));
        }
        return new MediaLibraryExportPlan(assets, unavailable);
    }
}
