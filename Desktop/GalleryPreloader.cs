using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Gallery;

namespace MeowChan.Desktop;

/// <summary>
/// An immutable, bounded view of the gallery around the shown image. The shell creates it on the UI thread before starting
/// background work, so the preloader never observes the mutable live gallery list.
/// </summary>
internal sealed class GalleryPreloadWindow
{
    private GalleryPreloadWindow(IReadOnlyList<GalleryEntry> entries, int index, int ahead, int behind)
    {
        Entries = entries;
        Index = index;
        Ahead = ahead;
        Behind = behind;
    }

    internal IReadOnlyList<GalleryEntry> Entries { get; }
    internal int Index { get; }
    internal int Ahead { get; }
    internal int Behind { get; }

    /// <summary>Copies only the shown image and its configured neighbors, preserving their gallery order.</summary>
    internal static GalleryPreloadWindow Capture(IReadOnlyList<GalleryEntry> source, int index, int ahead, int behind)
    {
        ArgumentNullException.ThrowIfNull(source);
        int count = source.Count;
        int safeAhead = Math.Max(0, ahead);
        int safeBehind = Math.Max(0, behind);
        if (count == 0 || index < 0 || index >= count)
            return new(Array.AsReadOnly(Array.Empty<GalleryEntry>()), -1, safeAhead, safeBehind);

        int start = (int)Math.Max(0L, (long)index - safeBehind);
        int end = (int)Math.Min((long)count - 1, (long)index + safeAhead);
        GalleryEntry[] snapshot = new GalleryEntry[end - start + 1];
        for (int position = start; position <= end; position++) snapshot[position - start] = source[position];
        return new(Array.AsReadOnly(snapshot), index - start, safeAhead, safeBehind);
    }
}

/// <summary>
/// After the gallery shows an image, decodes the images around it (a number after it and a number before it, nearest first) into
/// the shared <see cref="GalleryImageCache"/> in the background, so stepping either way shows them at once. It works one image at a
/// time behind the decode of the shown image, never decodes videos, animations (GIF) or other files, stays inside the memory budget
/// of the cache (it stops before the images around the shown one would not fit, so it never pushes out its own work) and is
/// cancelled as soon as the user moves on. Failures are ignored: the image is decoded normally when it is shown.
/// </summary>
internal sealed class GalleryPreloader<T> where T : class, IDisposable
{
    internal delegate Task<(T Bitmap, GalleryImageInfo Info)> Decoder(GalleryEntry entry, GalleryDecodeRequest request, CancellationToken token);

    private readonly GalleryImageCache<T> _cache;
    private readonly Decoder _decode;
    private readonly TimeSpan _delay;
    private CancellationTokenSource? _active;

    /// <param name="cache">Where the images go (the shared cache of the gallery in the application).</param>
    /// <param name="decode">The background decoder (<see cref="GalleryImageDecoder.DecodeBackgroundAsync"/> in the application).</param>
    /// <param name="delay">Quiet time after the shown image before the first decode starts (default 150 ms), so holding the arrow key preloads nothing.</param>
    internal GalleryPreloader(GalleryImageCache<T> cache, Decoder decode, TimeSpan? delay = null)
    {
        _cache = cache;
        _decode = decode;
        _delay = delay ?? TimeSpan.FromMilliseconds(150);
    }

    /// <summary>Decodes finished (stored in the cache) since the preloader was made; tests and diagnostics.</summary>
    internal int Decoded => Volatile.Read(ref _decoded);
    private int _decoded;

    /// <summary>Cancels what is running. Call it when the viewer moves on or closes.</summary>
    internal void Cancel()
    {
        CancellationTokenSource? active = Interlocked.Exchange(ref _active, null);
        if (active == null) return;
        try { active.Cancel(); } catch (ObjectDisposedException) { }
    }

    /// <summary>
    /// Replaces the running preload with the immutable bounded window. The returned task ends when the work is done or cancelled
    /// and never throws.
    /// </summary>
    internal Task Schedule(GalleryPreloadWindow window, GalleryDecodeRequest.Fit fit)
    {
        ArgumentNullException.ThrowIfNull(window);
        Cancel();
        if ((window.Ahead <= 0 && window.Behind <= 0) || _cache.BudgetBytes <= 0
            || window.Index < 0 || window.Index >= window.Entries.Count) return Task.CompletedTask;
        CancellationTokenSource source = new();
        _active = source;
        return RunAsync(window, fit, source);
    }

    /// <summary>
    /// Compatibility seam for callers that already have a complete list. It snapshots only the configured neighborhood before
    /// handing work to the background runner; the runner never receives the caller's potentially mutable list.
    /// </summary>
    internal Task Schedule(IReadOnlyList<GalleryEntry> entries, int index, GalleryDecodeRequest.Fit fit,
        int? ahead = null, int? behind = null)
    {
        int after = ahead ?? GalleryImageCache.PreloadAhead, before = behind ?? GalleryImageCache.PreloadBehind;
        return Schedule(GalleryPreloadWindow.Capture(entries, index, after, before), fit);
    }

    private async Task<long> TouchVerifiedAsync(GalleryEntry entry, GalleryDecodeRequest.Fit fit, CancellationToken token)
    {
        string? key = GalleryImageCache.KeyOf(entry);
        if (key == null || !_cache.HasCandidate(key, fit)) return -1;
        GalleryImageCache.FileValidity? validity = GalleryImageCache.Capture(entry);
        if (validity == null || !await GalleryImageCache.VerifyForCacheAsync(_cache, entry, validity, token).ConfigureAwait(false))
            return -1;
        token.ThrowIfCancellationRequested();
        return _cache.Touch(key, fit, validity.IsCurrent);
    }

    private async Task RunAsync(GalleryPreloadWindow preloadWindow, GalleryDecodeRequest.Fit fit, CancellationTokenSource source)
    {
        CancellationToken token = source.Token;
        try
        {
            if (_delay > TimeSpan.Zero) await Task.Delay(_delay, token).ConfigureAwait(false);
            long budget = _cache.BudgetBytes;
            // The memory of the window: the shown image plus what was kept or decoded for its neighbors. Upper bound of an image
            // not decoded yet: the whole viewport.
            long viewportBytes = (long)Math.Ceiling(Math.Max(1, fit.PhysicalWidth) * Math.Max(1, fit.PhysicalHeight)) * 4;
            long window = Math.Max(0, await TouchVerifiedAsync(preloadWindow.Entries[preloadWindow.Index], fit, token).ConfigureAwait(false));
            foreach (int position in GalleryPreload.Plan(preloadWindow.Entries.Count, preloadWindow.Index,
                preloadWindow.Ahead, preloadWindow.Behind))
            {
                token.ThrowIfCancellationRequested();
                GalleryEntry entry = preloadWindow.Entries[position];
                if (!GalleryPreload.Eligible(entry) || GalleryImageCache.KeyOf(entry) is not { } key) continue;
                long kept = await TouchVerifiedAsync(entry, fit, token).ConfigureAwait(false);
                if (kept >= 0) { window += kept; continue; }
                // Farther images would not fit either: stop instead of pushing out the nearer ones.
                if (window + viewportBytes > budget) break;
                GalleryImageCache.FileValidity? validity = GalleryImageCache.Capture(entry); // before the decode reads the file
                if (validity == null) continue;
                (T bitmap, GalleryImageInfo info) decoded;
                try { decoded = await _decode(entry, fit, token).ConfigureAwait(false); }
                catch (Exception exception) when (exception is not OperationCanceledException) { continue; }
                try { token.ThrowIfCancellationRequested(); }
                catch
                {
                    decoded.bitmap.Dispose();
                    throw;
                }
                // A replacement during decode is not a cacheable proof. The decoder's result is not shown by the preloader.
                if (!validity.IsCurrent()) { decoded.bitmap.Dispose(); continue; }
                GalleryImageCache.RememberVerified(_cache, validity);
                // Keep it (the lease of Add is not needed: nobody shows it yet) and let go of it.
                T stored = _cache.Add(key, decoded.bitmap, decoded.info, validity.IsCurrent);
                window += _cache.BytesOf(stored);
                Interlocked.Increment(ref _decoded);
                _cache.Release(stored);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { }
        finally
        {
            Interlocked.CompareExchange(ref _active, null, source);
            source.Dispose();
        }
    }
}

/// <summary>Which images the gallery preloads and in which order.</summary>
internal static class GalleryPreload
{
    /// <summary>
    /// Whether an entry is preloaded: a saved image that is not a GIF. Videos, other files and animations stay out; the viewer
    /// opens them in their own way.
    /// </summary>
    internal static bool Eligible(GalleryEntry entry) =>
        entry.Kind is SavedMediaKind.Image or SavedMediaKind.Unknown
        && !string.Equals(Path.GetExtension(entry.Asset.OriginalFileName ?? entry.Asset.Path), ".gif", StringComparison.OrdinalIgnoreCase)
        && entry.Asset.Length <= GalleryImageDecoder.MaximumBytes;

    /// <summary>
    /// Positions to preload, nearest first, alternating the directions while both have images left: index+1, index-1, index+2, ...
    /// Never the index itself and never outside 0..count-1.
    /// </summary>
    internal static IEnumerable<int> Plan(int count, int index, int ahead, int behind)
    {
        int next = 1, previous = 1;
        while (next <= ahead || previous <= behind)
        {
            if (next <= ahead)
            {
                if (index + next < count) yield return index + next;
                next++;
            }
            if (previous <= behind)
            {
                if (index - previous >= 0) yield return index - previous;
                previous++;
            }
        }
    }
}
