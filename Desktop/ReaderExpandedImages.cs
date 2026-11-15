using System;
using System.Collections.Generic;

namespace MeowChan.Desktop;

/// <summary>
/// The images of the reader that are expanded in their posts ("Open in the post"), remembered by attachment identity while
/// "Keep images expanded" is on. A post that scrolls out of the view or is built again expands its image again from here; without the
/// setting nothing is recorded and an image goes back to its thumbnail as it always did. The memory belongs to the open thread: when another
/// thread begins it is emptied, so it never holds more than the attachments of one thread (no count setting needed). UI thread only.
/// </summary>
internal sealed class ReaderExpandedImages
{
    private readonly HashSet<string> _keys = new(StringComparer.Ordinal);
    private string? _thread;

    internal int Count => _keys.Count;

    /// <summary>The reader builds posts of <paramref name="thread"/>: what was remembered of another thread is forgotten.</summary>
    internal void BeginThread(string thread)
    {
        if (string.Equals(_thread, thread, StringComparison.Ordinal)) return;
        _thread = thread;
        _keys.Clear();
    }

    internal void Set(string key, bool expanded)
    {
        if (expanded) _keys.Add(key); else _keys.Remove(key);
    }

    internal bool Contains(string key) => _keys.Contains(key);

    internal void Clear() => _keys.Clear();
}
