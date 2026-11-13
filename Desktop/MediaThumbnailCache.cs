using System;
using System.Collections.Generic;

namespace MeowChan.Desktop;

/// <summary>
/// A small least-recently-used set of decoded tile previews of the media page, keyed by the bytes they were made from (hash and
/// length), so scrolling back shows a tile at once instead of reading, hashing and decoding its file again, and equal bytes in
/// several threads share one preview. A tile view takes a lease with <see cref="TryAcquire"/> or <see cref="Add"/> and gives it back
/// with <see cref="Release"/>; a preview that was pushed out of the set is disposed when its last view lets go of it, never while a
/// tile still shows it. The count is a setting (Settings, Storage and limits). No file is read here.
/// </summary>
internal sealed class MediaThumbnailCache<T> where T : class, IDisposable
{
    private sealed class Slot(string key, T bitmap)
    {
        internal string Key { get; } = key;
        internal T Bitmap { get; } = bitmap;
        internal int Leases;
        internal bool Evicted;
        internal LinkedListNode<Slot>? Node;
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, Slot> _byKey = new(StringComparer.Ordinal);
    private readonly Dictionary<T, Slot> _byBitmap = new(ReferenceEqualityComparer.Instance);
    private readonly LinkedList<Slot> _recent = new(); // front: most recently used
    private int _capacity;

    internal MediaThumbnailCache(int capacity = MediaThumbnailCache.DefaultCapacity) => _capacity = Math.Max(1, capacity);

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

    /// <summary>Previews in the set (leased and not).</summary>
    internal int Count { get { lock (_gate) return _byKey.Count; } }

    /// <summary>The preview of these bytes if one is kept, leased to the caller; null otherwise. Counts as a use.</summary>
    internal T? TryAcquire(string key)
    {
        lock (_gate)
        {
            if (!_byKey.TryGetValue(key, out Slot? slot)) return null;
            slot.Leases++;
            _recent.Remove(slot.Node!);
            slot.Node = _recent.AddFirst(slot);
            return slot.Bitmap;
        }
    }

    /// <summary>
    /// Keeps a freshly decoded preview and leases it to the caller. When another view stored the same bytes in the meantime, that
    /// preview is leased instead and <paramref name="bitmap"/> is disposed, so the result is the bitmap to show.
    /// </summary>
    internal T Add(string key, T bitmap)
    {
        List<T> dropped;
        T shown = bitmap;
        lock (_gate)
        {
            if (_byKey.TryGetValue(key, out Slot? existing))
            {
                existing.Leases++;
                _recent.Remove(existing.Node!);
                existing.Node = _recent.AddFirst(existing);
                shown = existing.Bitmap;
            }
            else
            {
                Slot slot = new(key, bitmap) { Leases = 1 };
                slot.Node = _recent.AddFirst(slot);
                _byKey[key] = slot;
                _byBitmap[bitmap] = slot;
            }
            dropped = Trim();
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

    // Under the lock. Returns the previews to dispose now; one that is still shown is disposed by its last Release.
    private List<T> Trim()
    {
        List<T> dropped = new();
        while (_byKey.Count > _capacity && _recent.Last is { } oldest)
        {
            Slot slot = oldest.Value;
            _recent.RemoveLast();
            _byKey.Remove(slot.Key);
            slot.Evicted = true;
            if (slot.Leases == 0) { _byBitmap.Remove(slot.Bitmap); dropped.Add(slot.Bitmap); }
        }
        return dropped;
    }
}

/// <summary>The default of <see cref="MediaThumbnailCache{T}"/>.</summary>
internal static class MediaThumbnailCache
{
    /// <summary>Default number of previews kept (the limit is a setting). At about 0.2 MiB a preview, roughly 60 MiB of memory.</summary>
    internal const int DefaultCapacity = 384;
}
