using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using MeowChan.Gallery;

namespace MeowChan.Desktop;

/// <summary>
/// Decoded gallery images (decoded at display size), kept by the bytes they were made from (hash and length) and bounded by a
/// memory budget in bytes, so stepping back and forth shows an image at once instead of reading, verifying and decoding it again.
/// A viewer takes a lease with <see cref="TryAcquire"/> or <see cref="Add"/> and gives it back with <see cref="Release"/>; an image
/// that was pushed out of the set (or replaced by a larger decode of the same bytes) is disposed when its last lease is given back,
/// never while a viewer still shows it. A bitmap that is shown but no longer kept is not counted against the budget. A bitmap the
/// set does not know is disposed by <see cref="Release"/>, so callers can release every bitmap the same way. No file is read here.
/// </summary>
internal sealed class GalleryImageCache<T> where T : class, IDisposable
{
    private sealed class Slot(string key, T bitmap, GalleryImageInfo info, PixelSize size, Func<bool>? stillValid)
    {
        /// <summary>Checked at every use: false when the file the image was decoded from has changed since, which drops the image.</summary>
        internal Func<bool>? StillValid { get; } = stillValid;
        internal string Key { get; } = key;
        internal T Bitmap { get; } = bitmap;
        internal GalleryImageInfo Info { get; } = info;
        internal PixelSize Size { get; } = size;
        internal long Bytes => GalleryImageCache.BytesOf(Size);
        internal int Leases;
        internal bool Evicted;
        internal LinkedListNode<Slot>? Node;
    }

    private readonly object _gate = new();
    private readonly Func<T, PixelSize> _sizeOf;
    private readonly Dictionary<string, Slot> _byKey = new(StringComparer.Ordinal);
    private readonly Dictionary<T, Slot> _byBitmap = new(ReferenceEqualityComparer.Instance);
    private readonly LinkedList<Slot> _recent = new(); // front: most recently used
    private long _budget;
    private long _bytes;

    internal GalleryImageCache(Func<T, PixelSize> sizeOf, long budgetBytes = GalleryImageCache.DefaultBudgetBytes)
    {
        _sizeOf = sizeOf;
        _budget = Math.Max(0, budgetBytes);
    }

    /// <summary>The memory the kept images may use in bytes; 0 keeps nothing. Lowering it drops the least recently used images at once.</summary>
    internal long BudgetBytes
    {
        get { lock (_gate) return _budget; }
        set
        {
            List<T> dropped;
            lock (_gate) { _budget = Math.Max(0, value); dropped = Trim(); }
            foreach (T bitmap in dropped) bitmap.Dispose();
        }
    }

    /// <summary>Memory of the kept images in bytes (leased and not; a replaced or pushed-out image that is still shown is not counted).</summary>
    internal long Bytes { get { lock (_gate) return _bytes; } }

    /// <summary>Drops every kept image (an image that is still shown is disposed by its last <see cref="Release"/>); tests and diagnostics.</summary>
    internal void Clear()
    {
        long budget;
        lock (_gate) budget = _budget;
        BudgetBytes = 0;
        BudgetBytes = budget;
    }

    /// <summary>The memory a decoded image of this bitmap uses in bytes.</summary>
    internal long BytesOf(T bitmap) => GalleryImageCache.BytesOf(_sizeOf(bitmap));

    /// <summary>Images in the set.</summary>
    internal int Count { get { lock (_gate) return _byKey.Count; } }

    /// <summary>
    /// The kept image of these bytes if one is kept that covers the target of <paramref name="fit"/>, leased to the caller; false
    /// otherwise. Counts as a use.
    /// </summary>
    internal bool TryAcquire(string key, GalleryDecodeRequest.Fit fit, out T bitmap, out GalleryImageInfo info)
    {
        if (FindValid(key, fit) is { } slot)
            lock (_gate)
                if (!slot.Evicted)
                {
                    slot.Leases++;
                    Touch(slot);
                    bitmap = slot.Bitmap; info = slot.Info;
                    return true;
                }
        bitmap = null!; info = null!;
        return false;
    }

    /// <summary>
    /// Marks a kept image that covers the target as recently used without leasing it, and returns its size in bytes; -1 when no
    /// such image is kept. Used by the preloader to keep the images around the shown one at the young end of the set.
    /// </summary>
    internal long Touch(string key, GalleryDecodeRequest.Fit fit)
    {
        if (FindValid(key, fit) is not { } slot) return -1;
        lock (_gate)
        {
            if (slot.Evicted) return -1;
            Touch(slot);
            return slot.Bytes;
        }
    }

    // The kept image of these bytes that covers the target and whose file is unchanged; one whose file changed (or vanished) is
    // dropped. The file check runs outside the lock.
    private Slot? FindValid(string key, GalleryDecodeRequest.Fit fit)
    {
        Slot? slot;
        lock (_gate)
        {
            if (!_byKey.TryGetValue(key, out slot) || !Covers(slot, fit)) return null;
        }
        bool valid;
        try { valid = slot.StillValid?.Invoke() ?? true; }
        catch (Exception) { valid = false; }
        if (valid) return slot;
        T? dead = null;
        lock (_gate)
        {
            if (_byKey.TryGetValue(key, out Slot? current) && ReferenceEquals(current, slot)) dead = Evict(slot);
        }
        dead?.Dispose();
        return null;
    }

    /// <summary>
    /// Keeps a freshly decoded image and leases it to the caller. When the bytes are kept already at the same or a larger size, that
    /// image is leased instead and <paramref name="bitmap"/> is disposed; a larger decode replaces a smaller kept one. An image
    /// larger than the whole budget is not kept: the caller gets it back unleased-by-the-set and <see cref="Release"/> disposes it.
    /// <paramref name="stillValid"/> (optional) is asked at every later use; when it says false the image is dropped, so a file that
    /// changed after it was verified and decoded is never shown from here. The result is the bitmap to show.
    /// </summary>
    internal T Add(string key, T bitmap, GalleryImageInfo info, Func<bool>? stillValid = null)
    {
        PixelSize size = _sizeOf(bitmap);
        List<T> dropped = new();
        // A kept image of a file that changed since must not be shared with this fresh decode.
        Slot? kept;
        lock (_gate) _byKey.TryGetValue(key, out kept);
        if (kept?.StillValid is { } check)
        {
            bool valid;
            try { valid = check(); } catch (Exception) { valid = false; }
            if (!valid) lock (_gate) { if (ReferenceEquals(_byKey.GetValueOrDefault(key), kept) && Evict(kept) is { } stale) dropped.Add(stale); }
        }
        T shown = bitmap;
        lock (_gate)
        {
            if (_byKey.TryGetValue(key, out Slot? existing) && existing.Size.Width >= size.Width && existing.Size.Height >= size.Height)
            {
                existing.Leases++;
                Touch(existing);
                shown = existing.Bitmap;
            }
            else if (GalleryImageCache.BytesOf(size) <= _budget)
            {
                if (existing != null && Evict(existing) is { } replaced) dropped.Add(replaced);
                Slot slot = new(key, bitmap, info, size, stillValid) { Leases = 1 };
                slot.Node = _recent.AddFirst(slot);
                _byKey[key] = slot;
                _byBitmap[bitmap] = slot;
                _bytes += slot.Bytes;
                dropped.AddRange(Trim());
            }
        }
        if (!ReferenceEquals(shown, bitmap)) bitmap.Dispose();
        foreach (T old in dropped) old.Dispose();
        return shown;
    }

    /// <summary>Gives a lease back. A bitmap this set does not know (or one that was pushed out and has no other lease) is disposed.</summary>
    internal void Release(T bitmap)
    {
        bool dispose;
        lock (_gate)
        {
            if (!_byBitmap.TryGetValue(bitmap, out Slot? slot)) dispose = true;
            else
            {
                if (slot.Leases > 0) slot.Leases--;
                dispose = slot.Evicted && slot.Leases == 0;
                if (dispose) _byBitmap.Remove(bitmap);
            }
        }
        if (dispose) bitmap.Dispose();
    }

    private static bool Covers(Slot slot, GalleryDecodeRequest.Fit fit) =>
        GalleryFitTarget.Covers(slot.Size, GalleryFitTarget.Calculate(slot.Info, fit));

    private void Touch(Slot slot)
    {
        _recent.Remove(slot.Node!);
        slot.Node = _recent.AddFirst(slot);
    }

    // Under the lock. Takes a slot out of the set; the bitmap to dispose now (none while a viewer still leases it).
    private T? Evict(Slot slot)
    {
        _recent.Remove(slot.Node!);
        _byKey.Remove(slot.Key);
        _bytes -= slot.Bytes;
        slot.Evicted = true;
        if (slot.Leases > 0) return null;
        _byBitmap.Remove(slot.Bitmap);
        return slot.Bitmap;
    }

    // Under the lock. Returns the images to dispose now; one that is still shown is disposed by its last Release.
    private List<T> Trim()
    {
        List<T> dropped = new();
        while (_bytes > _budget && _recent.Last is { } oldest)
            if (Evict(oldest.Value) is { } bitmap) dropped.Add(bitmap);
        return dropped;
    }
}

/// <summary>The shared cache of the gallery viewers and its settings (Settings, Storage and limits).</summary>
internal static class GalleryImageCache
{
    /// <summary>Default memory of the kept gallery images: 256 MiB (a setting).</summary>
    internal const long DefaultBudgetBytes = 256L * 1024 * 1024;
    internal const int DefaultPreloadAhead = 3, DefaultPreloadBehind = 2;

    /// <summary>Decoded images of this session, shared by every gallery viewer.</summary>
    internal static GalleryImageCache<Bitmap> Shared { get; } = new(bitmap => bitmap.PixelSize);

    private static int _ahead = DefaultPreloadAhead, _behind = DefaultPreloadBehind;

    /// <summary>How many images after the shown one are decoded in the background (a setting).</summary>
    internal static int PreloadAhead => Volatile.Read(ref _ahead);
    /// <summary>How many images before the shown one are decoded in the background (a setting).</summary>
    internal static int PreloadBehind => Volatile.Read(ref _behind);

    /// <summary>Applies the settings: memory in MiB (0 keeps nothing), images preloaded after and before the shown one.</summary>
    internal static void Configure(int cacheMiB, int preloadAhead, int preloadBehind)
    {
        Volatile.Write(ref _ahead, Math.Max(0, preloadAhead));
        Volatile.Write(ref _behind, Math.Max(0, preloadBehind));
        Shared.BudgetBytes = Math.Max(0, cacheMiB) * 1024L * 1024;
    }

    /// <summary>Bytes of a decoded 32-bit image.</summary>
    internal static long BytesOf(PixelSize size) => (long)size.Width * size.Height * 4;

    /// <summary>The bytes an entry's image is kept by: its hash and length; null when the entry has no hash.</summary>
    internal static string? KeyOf(GalleryEntry entry) =>
        string.IsNullOrEmpty(entry.Asset.Sha256) ? null : entry.Asset.Sha256.ToLowerInvariant() + ":" + entry.Asset.Length;

    /// <summary>
    /// A check that the entry's file is still the one a decode starting now reads: the same length and last write time, not a
    /// symbolic link. The decode verifies the whole file (hash) once; a kept image is shown again only while this check holds,
    /// like the session memory of verified files. Take it BEFORE the decode reads, so a file that changes meanwhile fails it.
    /// Null when the file cannot be inspected: such an image is not kept.
    /// </summary>
    internal static Func<bool>? Validity(GalleryEntry entry)
    {
        string path = entry.Asset.Path;
        long length = entry.Asset.Length;
        (long Length, long Ticks)? Stamp()
        {
            FileInfo file = new(path);
            if (!file.Exists || (file.Attributes & FileAttributes.ReparsePoint) != 0) return null;
            return (file.Length, file.LastWriteTimeUtc.Ticks);
        }
        try
        {
            if (Stamp() is not { } before || before.Length != length) return null;
            return () => Stamp() is { } now && now == before;
        }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// Images that are decoded at display size are shown from and kept in <see cref="Shared"/>; the result is leased (give it back with
    /// <c>Shared.Release</c>). The original size is never kept: it can be huge and is asked for explicitly.
    /// </summary>
    internal static Task<(Bitmap Bitmap, GalleryImageInfo Info)> DecodeAsync(GalleryEntry entry, GalleryDecodeRequest request,
        CancellationToken cancellationToken) => DecodeThrough(Shared, GalleryImageDecoder.DecodeAsync, entry, request, cancellationToken);

    internal static Task<(T Bitmap, GalleryImageInfo Info)> DecodeThrough<T>(GalleryImageCache<T> cache,
        Func<GalleryEntry, GalleryDecodeRequest, CancellationToken, Task<(T Bitmap, GalleryImageInfo Info)>> decode,
        GalleryEntry entry, GalleryDecodeRequest request, CancellationToken cancellationToken) where T : class, IDisposable
    {
        if (request is not GalleryDecodeRequest.Fit fit || KeyOf(entry) is not { } key) return decode(entry, request, cancellationToken);
        // A hit needs the file unchanged since it was verified and decoded; otherwise it is decoded (and verified) again.
        if (cache.TryAcquire(key, fit, out T? hit, out GalleryImageInfo? info)) return Task.FromResult((hit, info));
        return DecodeAndKeep();

        async Task<(T Bitmap, GalleryImageInfo Info)> DecodeAndKeep()
        {
            Func<bool>? valid = Validity(entry);
            (T bitmap, GalleryImageInfo decodedInfo) = await decode(entry, request, cancellationToken).ConfigureAwait(false);
            // Without a stamp (file not inspectable) nothing is kept.
            return (valid == null ? bitmap : cache.Add(key, bitmap, decodedInfo, valid), decodedInfo);
        }
    }
}
