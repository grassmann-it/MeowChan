using System;
using System.Collections.Generic;
using System.Linq;
using MeowChan.Archive;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;

namespace MeowChan.Desktop;

/// <summary>
/// Pure rules for the multi-selection of a thread's Media tab. The tab shows saved files (rows); the media page's tag dialog,
/// export plan and gallery work on index tiles. This hands one to the other so both pages share the same helpers
/// (<see cref="MediaLibrarySelection"/>, <see cref="MediaLibraryTagging"/>, <see cref="MediaLibraryExport"/>) instead of
/// a second implementation. No controls and no IO: the page freezes the rows and the gallery entries before it calls these.
/// </summary>
internal static class ArchiveMediaSelection
{
    // A row is a record with mutable presentation state (rating, favorite callbacks); its identity is the instance the list shows.
    private static readonly IEqualityComparer<ArchiveAssetRow> Identity = new RowIdentity();

    private sealed class RowIdentity : IEqualityComparer<ArchiveAssetRow>
    {
        public bool Equals(ArchiveAssetRow? x, ArchiveAssetRow? y) => ReferenceEquals(x, y);
        public int GetHashCode(ArchiveAssetRow row) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(row);
    }

    /// <summary>The status line shows from two selected files on; one selected file is just the current file.</summary>
    internal static bool ShowsStatus(int count) => count > 1;

    /// <summary>"3 selected" in the interface language; the same words as the media page.</summary>
    internal static string Status(int count) => Texts.T("media.selection.count", count);

    /// <summary>
    /// The selected rows in the order the tab shows them, each once. A selected row that is no longer shown is dropped, so an
    /// action can never reach a file the user cannot see.
    /// </summary>
    internal static IReadOnlyList<ArchiveAssetRow> InShownOrder(IReadOnlyList<ArchiveAssetRow> shown, IEnumerable<ArchiveAssetRow> selected)
    {
        ArgumentNullException.ThrowIfNull(shown);
        ArgumentNullException.ThrowIfNull(selected);
        HashSet<ArchiveAssetRow> wanted = new(selected, Identity);
        return wanted.Count == 0 ? Array.Empty<ArchiveAssetRow>() : shown.Where(wanted.Contains).Distinct(Identity).ToArray();
    }

    /// <summary>True when the menu's row is part of a selection of several files: only then the menu offers the selection actions.</summary>
    internal static bool IsSeveral(IReadOnlyList<ArchiveAssetRow> selection, ArchiveAssetRow? row) =>
        selection.Count > 1 && row != null && selection.Contains(row, Identity);

    /// <summary>
    /// The file single-target actions (open in the gallery, Preview, rating) use when several are selected: the focused one while it
    /// is still selected, otherwise the list's own primary selection, otherwise the first selected file.
    /// </summary>
    internal static ArchiveAssetRow? Lead(IReadOnlyList<ArchiveAssetRow> selection, ArchiveAssetRow? focused, ArchiveAssetRow? primary)
    {
        if (selection.Count == 0) return null;
        if (focused != null && selection.Contains(focused, Identity)) return focused;
        if (primary != null && selection.Contains(primary, Identity)) return primary;
        return selection[0];
    }

    /// <summary>
    /// One media page tile per selected file, in selection order. The tile shows the post-bound occurrence when there is one and
    /// carries every occurrence of the file (a file shared by several posts has several), which is what the tag dialog lists.
    /// </summary>
    internal static IReadOnlyList<MediaLibraryTile> Tiles(ArchiveThreadSummary summary, IReadOnlyList<ArchiveAssetRow> rows,
        IReadOnlyList<GalleryEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(entries);
        ILookup<GalleryAssetKey, GalleryEntry> byFile = entries.ToLookup(entry => entry.Key);
        List<MediaLibraryTile> tiles = new(rows.Count);
        foreach (ArchiveAssetRow row in rows)
        {
            GalleryEntry[] occurrences = byFile[KeyOf(summary, row)].OrderBy(entry => entry.Post == null ? 1 : 0).ToArray();
            if (occurrences.Length == 0) occurrences = new[] { Fallback(summary, row) };
            MediaLibraryEntry[] mapped = occurrences.Select(entry => MediaLibraryIndexBuilder.EntryOf(summary, entry)).Distinct().ToArray();
            tiles.Add(new MediaLibraryTile(mapped[0], mapped));
        }
        return tiles;
    }

    /// <summary>The gallery entries of the rows, one per file in row order, the post-bound occurrence first when the file has one.</summary>
    internal static IReadOnlyList<GalleryEntry> GalleryEntries(ArchiveThreadSummary summary, IReadOnlyList<ArchiveAssetRow> rows,
        IReadOnlyList<GalleryEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(entries);
        ILookup<GalleryAssetKey, GalleryEntry> byFile = entries.ToLookup(entry => entry.Key);
        return rows.Select(row => byFile[KeyOf(summary, row)].OrderBy(entry => entry.Post == null ? 1 : 0).FirstOrDefault() ?? Fallback(summary, row)).ToArray();
    }

    /// <summary>The file an archive row stands for, as a gallery asset key.</summary>
    internal static GalleryAssetKey KeyOf(ArchiveThreadSummary summary, ArchiveAssetRow row) =>
        new(summary.Id, row.Manifest.Directory, row.Asset.AssetId);

    // A file the gallery build did not return (it is never locally taggable) is still exported and shown, like the single-file menu does.
    private static GalleryEntry Fallback(ArchiveThreadSummary summary, ArchiveAssetRow row) =>
        new(KeyOf(summary, row), row.Manifest, row.Asset, null, null, !row.Asset.IsCurrent, true) { ThreadUrl = summary.Url };
}
