using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Archive;
using MeowChan.Storage;

namespace MeowChan.Gallery;

/// <summary>Order of the media page.</summary>
internal enum MediaLibrarySort { ByThread, Newest, Random }

/// <summary>Pure ordering and visibility rules of the media page. No IO.</summary>
internal static class MediaLibraryView
{
    /// <summary>
    /// The entries the page shows, in page order. By thread: threads by latest capture, then post order;
    /// the thread acts like a meta tag that keeps its media together. Newest: post time, else capture time.
    /// Random: a stable shuffle for one seed, so paging and reopening do not reshuffle by themselves.
    /// </summary>
    internal static IReadOnlyList<MediaLibraryEntry> Select(IEnumerable<MediaLibraryEntry> entries, MediaLibrarySort sort,
        bool includeMissing, int seed = 0)
    {
        ArgumentNullException.ThrowIfNull(entries);
        IEnumerable<MediaLibraryEntry> visible = entries.Where(entry => includeMissing || entry.Presence == MediaLibraryPresence.Present);
        IEnumerable<MediaLibraryEntry> ordered = sort switch
        {
            MediaLibrarySort.ByThread => visible
                .OrderByDescending(entry => entry.LastCapturedAt ?? entry.FirstSeenAt)
                .ThenBy(entry => entry.Thread.Source, StringComparer.Ordinal).ThenBy(entry => entry.Thread.Board, StringComparer.Ordinal)
                .ThenBy(entry => entry.Thread.Thread, StringComparer.Ordinal)
                // Post media first in post order, then saved files without a post.
                .ThenBy(entry => entry.PostOrdinal.HasValue ? 0 : 1).ThenBy(entry => entry.PostOrdinal ?? long.MaxValue)
                .ThenBy(entry => entry.Position ?? int.MaxValue).ThenBy(entry => entry.Name, StringComparer.Ordinal)
                .ThenBy(entry => entry.AssetId, StringComparer.Ordinal),
            MediaLibrarySort.Newest => visible
                .OrderByDescending(entry => entry.PostedAt ?? entry.LastCapturedAt ?? entry.FirstSeenAt)
                .ThenByDescending(entry => entry.PostOrdinal ?? -1)
                .ThenBy(entry => entry.Thread.Source, StringComparer.Ordinal).ThenBy(entry => entry.Thread.Thread, StringComparer.Ordinal)
                .ThenBy(entry => entry.Position ?? int.MaxValue).ThenBy(entry => entry.AssetId, StringComparer.Ordinal),
            MediaLibrarySort.Random => visible
                .Select(entry => (Entry: entry, Key: StableKey(entry, seed)))
                .OrderBy(pair => pair.Key).ThenBy(pair => pair.Entry.AssetId, StringComparer.Ordinal).Select(pair => pair.Entry),
            _ => throw new ArgumentOutOfRangeException(nameof(sort))
        };
        return ordered.ToArray();
    }

    // Deterministic per entry and seed, independent of input order and process hash randomization. The hash runs over the characters of
    // the entry's identity fields joined by U+001F, without building that string (200,000 entries would be 60 MB of garbage per shuffle).
    internal static ulong StableKey(MediaLibraryEntry entry, int seed)
    {
        ulong hash = 14695981039346656037UL ^ (uint)seed;
        Span<char> number = stackalloc char[16];
        Mix(ref hash, entry.Thread.Source); Separate(ref hash);
        Mix(ref hash, entry.Thread.Board); Separate(ref hash);
        Mix(ref hash, entry.Thread.Thread); Separate(ref hash);
        Mix(ref hash, entry.PostId); Separate(ref hash);
        if (entry.Position is int position && position.TryFormat(number, out int written, default, CultureInfo.CurrentCulture))
            Mix(ref hash, number[..written]);
        Separate(ref hash);
        Mix(ref hash, entry.Directory); Separate(ref hash);
        Mix(ref hash, entry.AssetId); Separate(ref hash);
        Mix(ref hash, entry.MediaId);
        hash ^= hash >> 33; hash *= 0xff51afd7ed558ccdUL; hash ^= hash >> 33;
        return hash;
    }

    private static void Separate(ref ulong hash) { hash ^= '\u001f'; hash *= 1099511628211UL; }

    private static void Mix(ref ulong hash, ReadOnlySpan<char> text)
    {
        foreach (char character in text) { hash ^= character; hash *= 1099511628211UL; }
    }

    /// <summary>
    /// Present entries around <paramref name="index"/> for the gallery, at most <paramref name="maximum"/>,
    /// and the position of the chosen entry inside that window. Missing entries are skipped: they have no file.
    /// </summary>
    internal static (IReadOnlyList<MediaLibraryEntry> Window, int Start) GalleryWindow(IReadOnlyList<MediaLibraryEntry> shown,
        int index, int maximum = 200) => GalleryWindow(shown, PresentIndex(shown), index, maximum);

    /// <summary>The positions of the saved entries in <paramref name="shown"/>, ascending. A page builds this once per presentation.</summary>
    internal static int[] PresentIndex(IReadOnlyList<MediaLibraryEntry> shown)
    {
        ArgumentNullException.ThrowIfNull(shown);
        List<int> present = new();
        for (int i = 0; i < shown.Count; i++)
            if (shown[i].Presence == MediaLibraryPresence.Present) present.Add(i);
        return present.ToArray();
    }

    /// <summary>
    /// <see cref="GalleryWindow(IReadOnlyList{MediaLibraryEntry}, int, int)"/> with the positions of the saved entries given
    /// (<see cref="PresentIndex"/>): a binary search and a slice, no pass over every shown entry.
    /// </summary>
    internal static (IReadOnlyList<MediaLibraryEntry> Window, int Start) GalleryWindow(IReadOnlyList<MediaLibraryEntry> shown,
        int[] presentIndex, int index, int maximum = 200)
    {
        ArgumentNullException.ThrowIfNull(shown);
        ArgumentNullException.ThrowIfNull(presentIndex);
        if (index < 0 || index >= shown.Count || shown[index].Presence != MediaLibraryPresence.Present || maximum < 1)
            return (Array.Empty<MediaLibraryEntry>(), -1);
        int at = Array.BinarySearch(presentIndex, index);
        if (at < 0) return (Array.Empty<MediaLibraryEntry>(), -1);
        int first = Math.Clamp(at - maximum / 2, 0, Math.Max(0, presentIndex.Length - maximum));
        int count = Math.Min(maximum, presentIndex.Length - first);
        MediaLibraryEntry[] window = new MediaLibraryEntry[count];
        for (int i = 0; i < count; i++) window[i] = shown[presentIndex[first + i]];
        return (window, at - first);
    }
}

/// <summary>
/// Turns index entries back into gallery entries by reading their threads through the same selection
/// the gallery uses. An entry whose thread or file changed since indexing is left out rather than guessed.
/// </summary>
internal static class MediaLibraryGallery
{
    internal static async Task<IReadOnlyList<GalleryEntry>> ResolveAsync(IReadOnlyList<MediaLibraryEntry> entries,
        Func<CancellationToken, Task<ArchiveReader>> openReader,
        Func<string, CancellationToken, Task<MediaAssetSnapshot>> openManifest, CancellationToken cancellationToken = default,
        Func<BooruCatalogSnapshot?>? readBooruCatalog = null, Func<Import.ManualImportCatalogSnapshot?>? readImportCatalog = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        Dictionary<(ResourceId, string, string, string?, int?), GalleryEntry> found = new();
        Dictionary<MediaLibraryEntry, GalleryEntry> booru = new();
        BooruCatalogSnapshot? catalog = null;
        bool catalogRead = false;
        foreach (MediaLibraryEntry provider in entries.Where(entry => entry.BooruPostId != null && entry.Presence == MediaLibraryPresence.Present))
        {
            if (!catalogRead) { catalog = readBooruCatalog == null ? null : ReadCatalog(readBooruCatalog); catalogRead = true; }
            if (await BooruMediaGallery.ResolveAsync(provider, catalog, openManifest, cancellationToken).ConfigureAwait(false) is { } resolved)
                booru.TryAdd(provider, resolved);
        }
        Dictionary<MediaLibraryEntry, GalleryEntry> imported = new();
        Import.ManualImportCatalogSnapshot? imports = null;
        bool importsRead = false;
        foreach (MediaLibraryEntry item in entries.Where(entry => entry.ImportItemId != null && entry.Presence == MediaLibraryPresence.Present))
        {
            if (!importsRead) { imports = readImportCatalog == null ? null : ReadImports(readImportCatalog); importsRead = true; }
            if (await ManualImportMediaGallery.ResolveAsync(item, imports, openManifest, cancellationToken).ConfigureAwait(false) is { } resolved)
                imported.TryAdd(item, resolved);
        }
        ResourceId[] archived = entries.Where(entry => entry.Presence == MediaLibraryPresence.Present && !entry.IsCatalogMedium)
            .Select(entry => entry.Thread).Distinct().ToArray();
        // Provider media and imports need no archive catalog, so a profile without one still resolves them.
        await using ArchiveReader? reader = archived.Length == 0 && entries.Any(entry => entry.IsCatalogMedium)
            ? null : await openReader(cancellationToken).ConfigureAwait(false);
        foreach (ResourceId thread in archived)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArchivedThreadView? view = await reader!.ReadThreadAsync(thread, cancellationToken).ConfigureAwait(false);
            if (view == null) continue;
            List<MediaAssetSnapshot> manifests = new();
            foreach (string directory in (await reader!.GetMediaDirectoriesAsync(thread, cancellationToken).ConfigureAwait(false)).Distinct(StringComparer.Ordinal))
            {
                try { manifests.Add(await openManifest(directory, cancellationToken).ConfigureAwait(false)); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException) { }
            }
            foreach (GalleryEntry entry in GallerySelection.Build(view, manifests, allSavedVersions: false, includeOtherMedia: true).Entries)
                found.TryAdd((thread, entry.Manifest.Directory, entry.Asset.AssetId, entry.Post?.PostId, entry.Attachment?.Position), entry);
        }
        List<GalleryEntry> result = new();
        foreach (MediaLibraryEntry entry in entries)
        {
            if (entry.BooruPostId != null) { if (booru.TryGetValue(entry, out GalleryEntry? provider)) result.Add(provider); continue; }
            if (entry.ImportItemId != null) { if (imported.TryGetValue(entry, out GalleryEntry? import)) result.Add(import); continue; }
            if (entry.Presence == MediaLibraryPresence.Present && entry.Directory != null && entry.AssetId != null
                && found.TryGetValue((entry.Thread, entry.Directory, entry.AssetId, entry.PostId, entry.Position), out GalleryEntry? match))
                result.Add(match);
        }
        return result;
    }

    private static Import.ManualImportCatalogSnapshot? ReadImports(Func<Import.ManualImportCatalogSnapshot?> read)
    {
        try { return read(); }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ObjectDisposedException
            or Import.ManualImportException) { return null; }
    }

    // An unreadable catalog resolves nothing for provider media: they are left out, never guessed.
    private static BooruCatalogSnapshot? ReadCatalog(Func<BooruCatalogSnapshot?> read)
    {
        try { return read(); }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ObjectDisposedException) { return null; }
    }
}
