using System;
using System.Collections.Generic;
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

    // Deterministic per entry and seed, independent of input order and process hash randomization.
    private static ulong StableKey(MediaLibraryEntry entry, int seed)
    {
        string identity = string.Join("\u001f", entry.Thread.Source, entry.Thread.Board, entry.Thread.Thread,
            entry.PostId, entry.Position, entry.Directory, entry.AssetId, entry.MediaId);
        ulong hash = 14695981039346656037UL ^ (uint)seed;
        foreach (char character in identity) { hash ^= character; hash *= 1099511628211UL; }
        hash ^= hash >> 33; hash *= 0xff51afd7ed558ccdUL; hash ^= hash >> 33;
        return hash;
    }

    /// <summary>
    /// Present entries around <paramref name="index"/> for the gallery, at most <paramref name="maximum"/>,
    /// and the position of the chosen entry inside that window. Missing entries are skipped: they have no file.
    /// </summary>
    internal static (IReadOnlyList<MediaLibraryEntry> Window, int Start) GalleryWindow(IReadOnlyList<MediaLibraryEntry> shown,
        int index, int maximum = 200)
    {
        ArgumentNullException.ThrowIfNull(shown);
        if (index < 0 || index >= shown.Count || shown[index].Presence != MediaLibraryPresence.Present || maximum < 1)
            return (Array.Empty<MediaLibraryEntry>(), -1);
        List<int> present = Enumerable.Range(0, shown.Count).Where(i => shown[i].Presence == MediaLibraryPresence.Present).ToList();
        int at = present.IndexOf(index);
        int first = Math.Clamp(at - maximum / 2, 0, Math.Max(0, present.Count - maximum));
        int[] taken = present.Skip(first).Take(maximum).ToArray();
        return (taken.Select(i => shown[i]).ToArray(), Array.IndexOf(taken, index));
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
        Func<string, CancellationToken, Task<MediaAssetSnapshot>> openManifest, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        Dictionary<(ResourceId, string, string, string?, int?), GalleryEntry> found = new();
        await using ArchiveReader reader = await openReader(cancellationToken).ConfigureAwait(false);
        foreach (ResourceId thread in entries.Where(entry => entry.Presence == MediaLibraryPresence.Present)
            .Select(entry => entry.Thread).Distinct())
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArchivedThreadView? view = await reader.ReadThreadAsync(thread, cancellationToken).ConfigureAwait(false);
            if (view == null) continue;
            List<MediaAssetSnapshot> manifests = new();
            foreach (string directory in (await reader.GetMediaDirectoriesAsync(thread, cancellationToken).ConfigureAwait(false)).Distinct(StringComparer.Ordinal))
            {
                try { manifests.Add(await openManifest(directory, cancellationToken).ConfigureAwait(false)); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException) { }
            }
            foreach (GalleryEntry entry in GallerySelection.Build(view, manifests, allSavedVersions: false, includeOtherMedia: true).Entries)
                found.TryAdd((thread, entry.Manifest.Directory, entry.Asset.AssetId, entry.Post?.PostId, entry.Attachment?.Position), entry);
        }
        List<GalleryEntry> result = new();
        foreach (MediaLibraryEntry entry in entries)
            if (entry.Presence == MediaLibraryPresence.Present && entry.Directory != null && entry.AssetId != null
                && found.TryGetValue((entry.Thread, entry.Directory, entry.AssetId, entry.PostId, entry.Position), out GalleryEntry? match))
                result.Add(match);
        return result;
    }
}
