using System;
using System.Collections.Generic;
using System.Linq;
using MeowChan.Tags;

namespace MeowChan.Desktop;

/// <summary>What <see cref="QuickTagStamp.Add"/> did.</summary>
internal enum QuickTagStampAdd { Added, AlreadyThere, Full }

/// <summary>
/// The stamp set (PLAN-POWER-TAGGEN PT5): an ordered list of at most <see cref="Capacity"/> own tag names that every quick tag
/// surface offers first and can give to the current medium or selection with one key or click. It is kept by name, like the
/// persisted form (<c>Config.TagStamp</c>, later <c>tag-queue.json</c>), so it survives a reload of the tag store; a name that no
/// tag carries any more is simply not offered. The stamp never assigns anything by itself: only the host's apply action does.
/// One instance is shared by all surfaces of a profile (the gallery panel and the popover show the same stamp). No UI, no storage:
/// a host saves <see cref="ToNames"/> whenever <see cref="Changed"/> is raised.
/// </summary>
internal sealed class QuickTagStamp
{
    private readonly List<PrivateTagName> _names = new();
    private int _capacity;

    /// <param name="names">The saved names; blank, invalid and repeated ones are dropped, more than <paramref name="capacity"/> are cut.</param>
    /// <param name="capacity">The most tags the stamp holds (the stamp size limit); clamped to its range.</param>
    internal QuickTagStamp(IEnumerable<string?>? names = null, int capacity = QuickTagOptions.DefaultStampSize)
    {
        _capacity = Math.Clamp(capacity, QuickTagOptions.MinimumStampSize, QuickTagOptions.MaximumStampSize);
        _names.AddRange(Parse(names, _capacity));
    }

    /// <summary>The stamp tag names in stamp order.</summary>
    internal IReadOnlyList<PrivateTagName> Names => _names;
    internal int Count => _names.Count;
    internal bool IsFull => _names.Count >= _capacity;

    /// <summary>
    /// The most tags the stamp holds. Lowering it below the current number shortens the stamp from its end (the cut names are
    /// gone, like a lowered own-tag limit refuses new ones, except that the stamp is only a shortcut list).
    /// </summary>
    internal int Capacity
    {
        get => _capacity;
        set
        {
            int clamped = Math.Clamp(value, QuickTagOptions.MinimumStampSize, QuickTagOptions.MaximumStampSize);
            if (clamped == _capacity) return;
            _capacity = clamped;
            if (_names.Count > clamped) _names.RemoveRange(clamped, _names.Count - clamped);
            Changed?.Invoke();
        }
    }

    /// <summary>Raised after every change of the names (not for a refused add).</summary>
    internal event Action? Changed;

    internal bool Contains(PrivateTagName name) => name != null && _names.Any(item => item.Key == name.Key);

    /// <summary>Appends <paramref name="name"/>; refused when the stamp is full, ignored when it already holds the name.</summary>
    internal QuickTagStampAdd Add(PrivateTagName name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (Contains(name)) return QuickTagStampAdd.AlreadyThere;
        if (IsFull) return QuickTagStampAdd.Full;
        _names.Add(name);
        Changed?.Invoke();
        return QuickTagStampAdd.Added;
    }

    internal bool Remove(PrivateTagName name)
    {
        ArgumentNullException.ThrowIfNull(name);
        int index = _names.FindIndex(item => item.Key == name.Key);
        if (index < 0) return false;
        _names.RemoveAt(index);
        Changed?.Invoke();
        return true;
    }

    internal bool Clear()
    {
        if (_names.Count == 0) return false;
        _names.Clear();
        Changed?.Invoke();
        return true;
    }

    /// <summary>Replaces the whole stamp (a host loading the saved names); no event when nothing differs.</summary>
    internal void Replace(IEnumerable<string?>? names)
    {
        IReadOnlyList<PrivateTagName> next = Parse(names, _capacity);
        if (next.Select(item => item.Key).SequenceEqual(_names.Select(item => item.Key))) return;
        _names.Clear();
        _names.AddRange(next);
        Changed?.Invoke();
    }

    /// <summary>The names as they are saved: display spelling, stamp order.</summary>
    internal IReadOnlyList<string> ToNames() => _names.Select(item => item.Display).ToArray();

    /// <summary>The tags of <paramref name="snapshot"/> the stamp names, in stamp order; a name without a tag is skipped.</summary>
    internal IReadOnlyList<PrivateTagDefinition> Resolve(PrivateTagStoreSnapshot? snapshot)
    {
        if (snapshot == null || _names.Count == 0) return Array.Empty<PrivateTagDefinition>();
        Dictionary<string, PrivateTagDefinition> byKey = new(StringComparer.Ordinal);
        foreach (PrivateTagDefinition tag in snapshot.Tags) byKey.TryAdd(tag.Name.Key, tag);
        return _names.Select(name => byKey.GetValueOrDefault(name.Key)).OfType<PrivateTagDefinition>().ToArray();
    }

    /// <summary>The valid, distinct names of <paramref name="names"/> in order, at most <paramref name="capacity"/>.</summary>
    internal static IReadOnlyList<PrivateTagName> Parse(IEnumerable<string?>? names, int capacity)
    {
        List<PrivateTagName> result = new();
        if (names == null) return result;
        HashSet<string> keys = new(StringComparer.Ordinal);
        foreach (string? text in names)
        {
            if (result.Count >= capacity) break;
            if (!PrivateTagName.TryCreate(text, out PrivateTagName? name, out _) || name == null || !keys.Add(name.Key)) continue;
            result.Add(name);
        }
        return result;
    }
}
