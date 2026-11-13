using System;
using System.Collections.Generic;

namespace MeowChan.Storage;

/// <summary>
/// "This file was fully hashed in this session and has not changed since": the file's length and last write time at the time it was
/// verified. Previews of the media page use it so scrolling over 200,000 files does not read and hash every file again each time its
/// tile is shown. It is a memory-only convenience for derived previews: anything that exports, plays or opens a file for the user still
/// verifies the whole file. A file that changed length or time, or whose recorded checksum differs, is hashed again. The oldest
/// memories are dropped first once <see cref="Capacity"/> is reached.
/// </summary>
internal sealed class VerifiedFileMemo
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Stamp> _files = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();
    private int _capacity;

    private readonly record struct Stamp(long Length, long WriteTicks, string Sha256);

    internal VerifiedFileMemo(int capacity = 16384) => _capacity = Math.Max(1, capacity);

    internal int Capacity
    {
        get { lock (_gate) return _capacity; }
        set { lock (_gate) { _capacity = Math.Max(1, value); Trim(); } }
    }

    internal int Count { get { lock (_gate) return _files.Count; } }

    internal bool IsVerified(string path, long length, long writeTicks, string sha256)
    {
        lock (_gate)
            return _files.TryGetValue(path, out Stamp stamp) && stamp.Length == length && stamp.WriteTicks == writeTicks
                && string.Equals(stamp.Sha256, sha256, StringComparison.OrdinalIgnoreCase);
    }

    internal void Remember(string path, long length, long writeTicks, string sha256)
    {
        lock (_gate)
        {
            if (!_files.ContainsKey(path)) _order.Enqueue(path);
            _files[path] = new Stamp(length, writeTicks, sha256);
            Trim();
        }
    }

    internal void Clear()
    {
        lock (_gate) { _files.Clear(); _order.Clear(); }
    }

    private void Trim()
    {
        while (_files.Count > _capacity && _order.Count > 0) _files.Remove(_order.Dequeue());
    }
}
