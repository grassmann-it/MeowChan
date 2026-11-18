using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using MeowChan.Storage;

namespace MeowChan.Gallery;

internal sealed record MediaAudioScanProgress(int Done, int Total);

/// <summary>
/// Probes the videos of the media index for sound, one file at a time, read-only. Meant for a background
/// thread: it blocks, reads only container headers (see <see cref="MediaAudioProbe"/>) and never hashes a
/// file. The result is keyed by the indexed SHA-256; the file behind it is trusted only as far as the index
/// already is (manifest entry with that hash, same length), so a missing or changed file stays Unknown
/// and is not recorded, so it is tried again next time.
/// </summary>
internal static class MediaLibraryAudioScanner
{
    /// <summary>Gaps between progress reports and between cache saves while scanning.</summary>
    internal static readonly TimeSpan ReportInterval = TimeSpan.FromMilliseconds(400);
    internal static readonly TimeSpan SaveInterval = TimeSpan.FromSeconds(30);

    /// <summary>Saved videos of the index with their occurrences, by distinct bytes (hash and length), in index order.</summary>
    internal static IReadOnlyList<IReadOnlyList<MediaLibraryEntry>> Videos(MediaLibrarySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return snapshot.Entries.Where(IsProbeable).GroupBy(entry => MediaAudioMap.KeyOf(entry)!, StringComparer.Ordinal)
            .Select(group => (IReadOnlyList<MediaLibraryEntry>)group.ToArray()).ToArray();
    }

    /// <summary>A saved video file. A captured but never saved attachment has no file, so nothing to probe.</summary>
    internal static bool IsProbeable(MediaLibraryEntry entry) => entry.Kind == SavedMediaKind.Video
        && MediaAudioMap.KeyOf(entry) != null;

    /// <summary>
    /// Opens the file of an asset for probing, or null when it no longer is what the index recorded.
    /// Symbolic links are not followed; the manifest must still carry the indexed hash and length, and the file the same length.
    /// </summary>
    internal static FileStream? OpenForProbe(MediaAssetReference? asset, string? expectedSha256, long? expectedLength = null)
    {
        if (asset == null || expectedSha256 == null || !string.Equals(asset.Sha256, expectedSha256, StringComparison.OrdinalIgnoreCase)
            || expectedLength != null && asset.Length != expectedLength) return null;
        try
        {
            if ((File.GetAttributes(asset.Path) & FileAttributes.ReparsePoint) != 0) return null;
            FileStream stream = new(asset.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length == asset.Length) return stream;
            stream.Dispose();
            return null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// Probes every video of <paramref name="snapshot"/> that <paramref name="known"/> has no current answer for
    /// and returns the map with the new answers. <paramref name="open"/> gives a stream for an occurrence or null
    /// when its file is unavailable; the next occurrence of the same bytes is then tried. Progress and the growing
    /// map are reported at most every <see cref="ReportInterval"/>; the map is saved to <paramref name="cachePath"/>
    /// now and then and at the end, also when cancelled (then <see cref="OperationCanceledException"/> follows), within
    /// <paramref name="cacheLimits"/> (default: the default limits).
    /// </summary>
    internal static MediaAudioMap Scan(MediaLibrarySnapshot snapshot, MediaAudioMap known, Func<MediaLibraryEntry, Stream?> open,
        Action<MediaAudioMap, MediaAudioScanProgress>? report = null, string? cachePath = null, CancellationToken cancellationToken = default,
        MediaAudioCacheLimits? cacheLimits = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(known);
        ArgumentNullException.ThrowIfNull(open);
        IReadOnlyList<IReadOnlyList<MediaLibraryEntry>> videos = Videos(snapshot);
        IReadOnlyList<IReadOnlyList<MediaLibraryEntry>> pending = videos.Where(group => !known.WasProbed(MediaAudioMap.KeyOf(group[0]))).ToArray();
        // Results of media that left a complete index are dropped; an incomplete index may just not list them yet.
        HashSet<string> present = videos.Select(group => MediaAudioMap.KeyOf(group[0])!).ToHashSet(StringComparer.Ordinal);
        MediaAudioMap map = snapshot.Complete ? known.Retain(present) : known;
        List<KeyValuePair<string, MediaAudio>> fresh = new();
        DateTime lastReport = DateTime.UtcNow, lastSave = lastReport;
        int done = 0;
        bool changed = !ReferenceEquals(map, known) && map.Count != known.Count;
        try
        {
            if (pending.Count > 0) report?.Invoke(map, new(0, pending.Count));
            foreach (IReadOnlyList<MediaLibraryEntry> group in pending)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (ProbeGroup(group, open) is { } result) { fresh.Add(new(MediaAudioMap.KeyOf(group[0])!, result)); changed = true; }
                done++;
                DateTime now = DateTime.UtcNow;
                if (now - lastReport >= ReportInterval)
                {
                    map = Merge(map, fresh);
                    report?.Invoke(map, new(done, pending.Count));
                    lastReport = now;
                }
                if (changed && cachePath != null && now - lastSave >= SaveInterval)
                {
                    map = Merge(map, fresh);
                    TrySave(cachePath, map, cacheLimits);
                    lastSave = now;
                    changed = false;
                }
            }
            map = Merge(map, fresh);
            if (pending.Count > 0) report?.Invoke(map, new(pending.Count, pending.Count));
            return map;
        }
        finally
        {
            // Also on cancellation: what was learned so far is kept.
            if (changed && cachePath != null) TrySave(cachePath, Merge(map, fresh), cacheLimits);
        }
    }

    // The first occurrence whose file can be opened decides for the hash; null when no file is available.
    private static MediaAudio? ProbeGroup(IReadOnlyList<MediaLibraryEntry> group, Func<MediaLibraryEntry, Stream?> open)
    {
        foreach (MediaLibraryEntry entry in group)
        {
            try
            {
                using Stream? stream = open(entry);
                if (stream == null) continue;
                return MediaAudioProbe.Probe(stream);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException) { }
        }
        return null;
    }

    private static MediaAudioMap Merge(MediaAudioMap map, List<KeyValuePair<string, MediaAudio>> fresh)
    {
        if (fresh.Count == 0) return map;
        MediaAudioMap merged = map.With(fresh);
        fresh.Clear();
        return merged;
    }

    private static void TrySave(string path, MediaAudioMap map, MediaAudioCacheLimits? limits)
    {
        try { MediaAudioCache.Save(path, map, limits); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { } // A cache is optional.
    }
}
