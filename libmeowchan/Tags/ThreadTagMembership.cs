using System;
using System.Collections.Generic;
using System.Linq;
using MeowChan.Archive;

namespace MeowChan.Tags;

/// <summary>
/// A tag filter bound to one tag snapshot: an immutable answer to "does this thread match".
/// Frozen with its search request, so list, paging, gallery and export all see the same set.
/// Thread assignments only; media tags never make their thread match.
/// </summary>
internal sealed class ThreadTagMembership
{
    private readonly HashSet<ResourceId>[] _included;
    private readonly HashSet<ResourceId> _excluded;
    private readonly bool _any;

    private ThreadTagMembership(long revision, HashSet<ResourceId>[] included, HashSet<ResourceId> excluded, bool any)
    {
        Revision = revision;
        _included = included;
        _excluded = excluded;
        _any = any;
    }

    /// <summary>Revision of the tag snapshot this membership was computed from.</summary>
    internal long Revision { get; }

    /// <summary>False when the filter names a tag the snapshot does not contain; nothing is guessed.</summary>
    internal static bool TryCreate(ArchiveTagFilter filter, PrivateTagStoreSnapshot snapshot, out ThreadTagMembership? membership)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(snapshot);
        membership = null;
        HashSet<Guid> known = snapshot.Tags.Select(tag => tag.Id.Value).ToHashSet();
        if (filter.Include.Concat(filter.Exclude).Any(id => !known.Contains(id))) return false;
        Dictionary<Guid, HashSet<ResourceId>> threads = filter.Include.Concat(filter.Exclude)
            .ToDictionary(id => id, _ => new HashSet<ResourceId>());
        foreach (PrivateThreadTagAssignment assignment in snapshot.ThreadAssignments)
            if (threads.TryGetValue(assignment.TagId.Value, out HashSet<ResourceId>? set)) set.Add(assignment.Thread);
        HashSet<ResourceId> excluded = new();
        foreach (Guid id in filter.Exclude) excluded.UnionWith(threads[id]);
        membership = new(snapshot.Revision, filter.Include.Select(id => threads[id]).ToArray(), excluded,
            filter.Match == ArchiveTagMatch.Any);
        return true;
    }

    internal bool Matches(ResourceId thread)
    {
        if (_excluded.Contains(thread)) return false;
        if (_included.Length == 0) return true;
        return _any ? _included.Any(set => set.Contains(thread)) : _included.All(set => set.Contains(thread));
    }
}
