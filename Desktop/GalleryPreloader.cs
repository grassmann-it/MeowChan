using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Gallery;

namespace MeowChan.Desktop;

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
    /// Replaces the running preload by one around <paramref name="index"/> with the configured counts. The returned task ends
    /// when the work is done or cancelled and never throws.
    /// </summary>
    internal Task Schedule(IReadOnlyList<GalleryEntry> entries, int index, GalleryDecodeRequest.Fit fit,
        int? ahead = null, int? behind = null)
    {
        Cancel();
        int after = ahead ?? GalleryImageCache.PreloadAhead, before = behind ?? GalleryImageCache.PreloadBehind;
        if ((after <= 0 && before <= 0) || _cache.BudgetBytes <= 0 || index < 0 || index >= entries.Count) return Task.CompletedTask;
        CancellationTokenSource source = new();
        _active = source;
        return RunAsync(entries, index, fit, after, before, source);
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

    private async Task RunAsync(IReadOnlyList<GalleryEntry> entries, int index, GalleryDecodeRequest.Fit fit,
        int ahead, int behind, CancellationTokenSource source)
    {
        CancellationToken token = source.Token;
        try
        {
            if (_delay > TimeSpan.Zero) await Task.Delay(_delay, token).ConfigureAwait(false);
            long budget = _cache.BudgetBytes;
            // The memory of the window: the shown image plus what was kept or decoded for its neighbors. Upper bound of an image
            // not decoded yet: the whole viewport.
            long viewportBytes = (long)Math.Ceiling(Math.Max(1, fit.PhysicalWidth) * Math.Max(1, fit.PhysicalHeight)) * 4;
            long window = await TouchVerifiedAsync(entries[index], fit, token).ConfigureAwait(false);
            foreach (int position in GalleryPreload.Plan(entries.Count, index, ahead, before))
            {
                token.ThrowIfCancellationRequested();
                GalleryEntry entry = entries[position];
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
