using System;
using System.Collections.Generic;
using System.Linq;
using MeowChan.Gallery;
using MeowChan.Tags;

namespace MeowChan.Desktop;

/// <summary>What marking or unmarking a medium did (PLAN-SCHNELL-TAGGEN §1.4, decision E4).</summary>
internal enum GalleryMarkOutcome
{
    /// <summary>The medium is marked now.</summary>
    Marked,
    /// <summary>It already was; nothing changed.</summary>
    AlreadyMarked,
    /// <summary>The mark is gone.</summary>
    Unmarked,
    /// <summary>It was not marked; nothing changed.</summary>
    NotMarked,
    /// <summary>The marked-media limit is reached; nothing changed.</summary>
    LimitReached,
    /// <summary>The medium has no tag target (not saved, no bound post version, a provider medium); it cannot be marked.</summary>
    Unavailable
}

/// <summary>
/// The media the gallery marks for one tag change (Shift+Page Down marks and moves on, Shift+Page Up goes back and unmarks).
/// A mark is the exact tag subject of a medium, so two views of the same file are one mark and the count is the number of
/// distinct media the tag change will touch. The set keeps marking order, never grows beyond <see cref="Limit"/> (the setting
/// <c>QuickTagMarkedMediaLimit</c>, clamped to its range) and counts every change in <see cref="Revision"/>. Marks are gallery
/// state: they live as long as the viewer, not as long as the panel. No UI and no storage.
/// </summary>
internal sealed class GalleryMarks
{
    private readonly List<MediaTagSubject> _order = new();
    private readonly HashSet<MediaTagSubject> _set = new();
    // The entry each mark was made on (favorite and rating are keyed by thread and asset, not by tag subject; PT6).
    private readonly Dictionary<MediaTagSubject, GalleryEntry> _entries = new();
    private int _limit;

    internal GalleryMarks(int limit = QuickTagOptions.DefaultMarkedMediaLimit) => _limit = QuickTagOptions.ClampMarkedMediaLimit(limit);

    /// <summary>The most media that can be marked. A lowered limit keeps the marks already set and only refuses new ones.</summary>
    internal int Limit
    {
        get => _limit;
        set => _limit = QuickTagOptions.ClampMarkedMediaLimit(value);
    }

    internal int Count => _order.Count;
    /// <summary>Increases with every change of the set; a target made for an older revision is out of date.</summary>
    internal int Revision { get; private set; }

    internal bool IsMarked(MediaTagSubject subject) => subject != null && _set.Contains(subject);

    /// <param name="entry">The gallery entry the mark was made on, when known: favorite and rating changes of the marked media write to it.</param>
    internal GalleryMarkOutcome Mark(MediaTagSubject subject, GalleryEntry? entry = null)
    {
        ArgumentNullException.ThrowIfNull(subject);
        if (_set.Contains(subject)) return GalleryMarkOutcome.AlreadyMarked;
        if (_order.Count >= _limit) return GalleryMarkOutcome.LimitReached;
        _set.Add(subject);
        _order.Add(subject);
        if (entry != null) _entries[subject] = entry;
        Revision++;
        return GalleryMarkOutcome.Marked;
    }

    internal GalleryMarkOutcome Unmark(MediaTagSubject subject)
    {
        ArgumentNullException.ThrowIfNull(subject);
        if (!_set.Remove(subject)) return GalleryMarkOutcome.NotMarked;
        _order.Remove(subject);
        _entries.Remove(subject);
        Revision++;
        return GalleryMarkOutcome.Unmarked;
    }

    /// <summary>Removes every mark; returns how many there were.</summary>
    internal int Clear()
    {
        int count = _order.Count;
        if (count == 0) return 0;
        _order.Clear();
        _set.Clear();
        _entries.Clear();
        Revision++;
        return count;
    }

    /// <summary>A copy of the marked subjects in marking order: what a selection session is made over.</summary>
    internal IReadOnlyList<MediaTagSubject> Snapshot() => _order.ToArray();

    /// <summary>The entries the marks were made on, in marking order (a mark without a known entry is left out): what a favorite or rating change writes to.</summary>
    internal IReadOnlyList<GalleryEntry> Entries() =>
        _order.Select(subject => _entries.TryGetValue(subject, out GalleryEntry? entry) ? entry : null).OfType<GalleryEntry>().ToArray();
}
