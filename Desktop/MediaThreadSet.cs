using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;

namespace MeowChan.Desktop;

/// <summary>Where the thread set of the Media page came from (it words the chip): the results of an Archive search, or threads picked in a list.</summary>
internal enum MediaThreadSetOrigin { ArchiveSearch, Selection }

/// <summary>
/// A set of threads as one filter value of the Media page (<see cref="MediaFilterField.Threads"/>): "the media of exactly these threads".
/// Runtime state handed over by another page, never typed query syntax. It is immutable, keeps the order it was given (the order of the
/// Archive results, which the chip's tip shows) and answers membership as a set lookup, so a set of tens of thousands of threads is as
/// cheap to filter with as one of three. There is no ceiling on its size. Two sets are equal when they hold the same threads from the
/// same origin, whatever their order.
/// </summary>
internal sealed class MediaThreadSet : IEquatable<MediaThreadSet>
{
    private readonly FrozenSet<ResourceId> _members;

    internal MediaThreadSet(IEnumerable<ResourceId> threads, MediaThreadSetOrigin origin)
    {
        ArgumentNullException.ThrowIfNull(threads);
        if (!Enum.IsDefined(origin)) throw new ArgumentOutOfRangeException(nameof(origin));
        HashSet<ResourceId> seen = [];
        List<ResourceId> ordered = [];
        foreach (ResourceId thread in threads)
        {
            ArgumentNullException.ThrowIfNull(thread);
            if (seen.Add(thread)) ordered.Add(thread);
        }
        Ordered = ordered;
        _members = seen.ToFrozenSet();
        Origin = origin;
    }

    /// <summary>The threads without duplicates, in the order they were given.</summary>
    internal IReadOnlyList<ResourceId> Ordered { get; }
    /// <summary>The same threads as a set (the lookup the filter uses).</summary>
    internal IReadOnlySet<ResourceId> Members => _members;
    internal int Count => Ordered.Count;
    internal MediaThreadSetOrigin Origin { get; }

    public bool Equals(MediaThreadSet? other) => other != null && Origin == other.Origin && Count == other.Count && _members.SetEquals(other._members);
    public override bool Equals(object? obj) => obj is MediaThreadSet other && Equals(other);

    public override int GetHashCode()
    {
        int members = 0;
        foreach (ResourceId thread in _members) members ^= thread.GetHashCode(); // order independent
        return HashCode.Combine(Origin, Count, members);
    }
}

/// <summary>
/// What "Show in Media" does with the threads selected in a list (the Archive and Downloads lists, their menus and Alt+M share it): one thread
/// opens as the thread: chip, with its title when it is the lead; several open as one <see cref="MediaThreadSet"/> from
/// <see cref="MediaThreadSetOrigin.Selection"/>. Entries without a thread number (boards) have no media and are left out; nothing is
/// opened for a selection that has no thread.
/// </summary>
internal static class MediaThreadTargets
{
    internal static void Open(IReadOnlyList<ResourceId> selection, ResourceId lead, string? leadTitle,
        Action<ResourceId, string?> one, Action<IReadOnlyList<ResourceId>> many)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ResourceId[] threads = [.. selection.Where(thread => thread.Thread != null).Distinct()];
        if (threads.Length > 1) many(threads);
        else if (threads.Length == 1) one(threads[0], threads[0] == lead ? leadTitle : null);
    }
}
