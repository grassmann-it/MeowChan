using System;
using System.Collections.Generic;

namespace MeowChan.Desktop;

/// <summary>
/// A small least-recently-used set of decoded tile previews of the media page, keyed by the bytes they were made from (hash and
/// length), so scrolling back shows a tile at once instead of reading, hashing and decoding its file again, and equal bytes in
/// several threads share one preview. A tile view takes a lease with <see cref="TryAcquire"/> or <see cref="Add"/> and gives it back
/// with <see cref="Release"/>; a preview that was pushed out of the set is disposed when its last view lets go of it, never while a
/// tile still shows it. The configured tile count is an upper bound and the retained decoded bytes have a derived budget. No file is
/// read here.
/// </summary>
internal sealed class MediaThumbnailCache<T> where T : class, IDisposable
{
    private sealed class Slot(string key, T bitmap, long bytes)
    {
        internal string Key { get; } = key;
        internal T Bitmap { get; } = bitmap;
        internal long Bytes { get; } = bytes;
        internal int Leases;
        internal bool Evicted;
        internal LinkedListNode<Slot>? Node;
    }

    private readonly object _gate = new();
    private readonly Func<T, long> _bytesOf;
    private readonly Dictionary<string, Slot> _byKey = new(StringComparer.Ordinal);
    private readonly Dictionary<T, Slot> _byBitmap = new(ReferenceEqualityComparer.Instance);
    private readonly LinkedList<Slot> _recent = new(); // front: most recently used
    private int _capacity;
    private long _budgetBytes;
    private long _bytes;

    // This overload preserves the count-only cache used by the non-bitmap contract tests. The production cache supplies a
    // decoded-size function and a byte budget, while these callers continue to exercise the lease/LRU contract itself.
    internal MediaThumbnailCache(int capacity = MediaThumbnailCache.DefaultCapacity)
        : this(static _ => 1, capacity, long.MaxValue) { }

    internal MediaThumbnailCache(Func<T, long> bytesOf, int capacity = MediaThumbnailCache.DefaultCapacity,
        long budgetBytes = long.MaxValue)
    {
        _bytesOf = bytesOf ?? throw new ArgumentNullException(nameof(bytesOf));
        _capacity = Math.Max(1, capacity);
        _budgetBytes = Math.Max(0, budgetBytes);
    }

    /// <summary>How many previews are kept; lowering it drops the least recently used ones at once.</summary>
    internal int Capacity
    {
        get { lock (_gate) return _capacity; }
        set
        {
            List<T> dropped;
            lock (_gate) { _capacity = Math.Max(1, value); dropped = Trim(); }
            foreach (T bitmap in dropped) bitmap.Dispose();
        }
    }

    /// <summary>
    /// The maximum decoded bytes retained by this set; lowering it drops the least recently used previews at once. A preview that is
    /// still leased is removed from the retained set but remains alive until its last lease is released.
    /// </summary>
    internal long BudgetBytes
    {
        get { lock (_gate) return _budgetBytes; }
        set
        {
            List<T> dropped;
            lock (_gate) { _budgetBytes = Math.Max(0, value); dropped = Trim(); }
            foreach (T bitmap in dropped) bitmap.Dispose();
        }
    }

    /// <summary>Decoded bytes of previews still retained in the set; leased and unleased previews both count.</summary>
    internal long Bytes { get { lock (_gate) return _bytes; } }

    /// <summary>Decoded bytes attributed to a preview. The production bitmap implementation uses its pixel dimensions.</summary>
    internal long BytesOf(T bitmap) => CostOf(bitmap);

    /// <summary>Previews in the set (leased and not).</summary>
    internal int Count { get { lock (_gate) return _byKey.Count; } }

    /// <summary>The preview of these bytes if one is kept, leased to the caller; null otherwise. Counts as a use.</summary>
    internal T? TryAcquire(string key)
    {
        lock (_gate)
        {
            if (!_byKey.TryGetValue(key, out Slot? slot)) return null;
            slot.Leases++;
            Touch(slot);
            return slot.Bitmap;
        }
    }

    /// <summary>
    /// Keeps a freshly decoded preview and leases it to the caller. When another view stored the same bytes in the meantime, that
    /// preview is leased instead and <paramref name="bitmap"/> is disposed, so the result is the bitmap to show. A preview larger
    /// than the whole byte budget is returned to the caller without retaining it; <see cref="Release"/> still disposes it.
    /// </summary>
    internal T Add(string key, T bitmap)
    {
        long bytes = CostOf(bitmap);
        List<T> dropped = new();
        T shown = bitmap;
        lock (_gate)
        {
            if (_byKey.TryGetValue(key, out Slot? existing))
            {
                existing.Leases++;
                Touch(existing);
                shown = existing.Bitmap;
            }
            else if (bytes <= _budgetBytes)
            {
                Slot slot = new(key, bitmap, bytes) { Leases = 1 };
                slot.Node = _recent.AddFirst(slot);
                _byKey[key] = slot;
                _byBitmap[bitmap] = slot;
                _bytes = SaturatingAdd(_bytes, bytes);
                dropped = Trim();
            }
        }
        if (!ReferenceEquals(shown, bitmap)) bitmap.Dispose();
        foreach (T old in dropped) old.Dispose();
        return shown;
    }

    /// <summary>Gives a lease back. A bitmap this set does not know (or one that was pushed out and has no other view) is disposed.</summary>
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

    private long CostOf(T bitmap) => Math.Max(0, _bytesOf(bitmap));

    private static long SaturatingAdd(long left, long right) =>
        right > long.MaxValue - left ? long.MaxValue : left + right;

    private void Touch(Slot slot)
    {
        _recent.Remove(slot.Node!);
        slot.Node = _recent.AddFirst(slot);
    }

    // Under the lock. Takes a slot out of the set; a leased preview is disposed by its last Release.
    private T? Evict(Slot slot)
    {
        _recent.Remove(slot.Node!);
        _byKey.Remove(slot.Key);
        _bytes = Math.Max(0, _bytes - slot.Bytes);
        slot.Evicted = true;
        if (slot.Leases > 0) return null;
        _byBitmap.Remove(slot.Bitmap);
        return slot.Bitmap;
    }

    // Under the lock. Returns the previews to dispose now; one that is still shown is disposed by its last Release.
    private List<T> Trim()
    {
        List<T> dropped = new();
        while ((_byKey.Count > _capacity || _bytes > _budgetBytes) && _recent.Last is { } oldest)
            if (Evict(oldest.Value) is { } bitmap) dropped.Add(bitmap);
        return dropped;
    }
}

/// <summary>The defaults and byte-budget conversion of <see cref="MediaThumbnailCache{T}"/>.</summary>
internal static class MediaThumbnailCache
{
    /// <summary>Default number of previews kept (the limit is a setting).</summary>
    internal const int DefaultCapacity = 384;

    // The largest ordinary retained preview is the existing 340x250 video poster (32-bit decoded pixels). The setting remains in
    // tile units by deriving one byte budget from it; actual bitmaps are charged by their own pixel dimensions.
    internal const long ReferencePreviewBytes =
        (long)MediaTileThumbnails.PosterWidth * 2 * MediaTileThumbnails.PosterHeight * 2 * 4;
    internal const long DefaultBudgetBytes = DefaultCapacity * ReferencePreviewBytes;

    internal static long ByteBudgetForTiles(int tiles) => (long)Math.Max(1, tiles) * ReferencePreviewBytes;
}
