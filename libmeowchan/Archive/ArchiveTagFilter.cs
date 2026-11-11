using System;
using System.Collections.Generic;
using System.Linq;

namespace MeowChan.Archive;

/// <summary>How several included tags combine. Excluded tags always exclude.</summary>
internal enum ArchiveTagMatch { All, Any }

/// <summary>
/// Own-tag part of a search definition: threads that carry the included tags (all or at least one)
/// and none of the excluded tags. Tags are referenced by their local ID, so a rename keeps the
/// filter intact and a deleted tag makes it unresolvable instead of silently widening the search.
/// Only thread tags count; tags on media do not make their thread match. Canonical and immutable.
/// </summary>
internal sealed class ArchiveTagFilter : IEquatable<ArchiveTagFilter>
{
    internal const int MaximumTagsPerList = 32;

    internal ArchiveTagFilter(IEnumerable<Guid>? include, IEnumerable<Guid>? exclude, ArchiveTagMatch match = ArchiveTagMatch.All)
    {
        if (!Enum.IsDefined(match)) throw new ArgumentOutOfRangeException(nameof(match));
        Guid[] included = Canonical(include, nameof(include));
        Guid[] excluded = Canonical(exclude, nameof(exclude));
        if (included.Length == 0 && excluded.Length == 0)
            throw new ArgumentException("A tag filter needs at least one tag.");
        if (included.Intersect(excluded).Any())
            throw new ArgumentException("A tag cannot be both included and excluded.");
        Include = Array.AsReadOnly(included);
        Exclude = Array.AsReadOnly(excluded);
        // With fewer than two included tags both modes mean the same; keep one canonical spelling.
        Match = included.Length < 2 ? ArchiveTagMatch.All : match;
    }

    /// <summary>Null when both lists are empty, so "no tag filter" has exactly one representation.</summary>
    internal static ArchiveTagFilter? OrNull(IEnumerable<Guid>? include, IEnumerable<Guid>? exclude,
        ArchiveTagMatch match = ArchiveTagMatch.All)
    {
        Guid[] included = (include ?? []).ToArray(), excluded = (exclude ?? []).ToArray();
        return included.Length == 0 && excluded.Length == 0 ? null : new ArchiveTagFilter(included, excluded, match);
    }

    internal IReadOnlyList<Guid> Include { get; }
    internal IReadOnlyList<Guid> Exclude { get; }
    internal ArchiveTagMatch Match { get; }

    private static Guid[] Canonical(IEnumerable<Guid>? values, string parameter)
    {
        Guid[] items = (values ?? []).Take(MaximumTagsPerList + 1).ToArray();
        if (items.Length > MaximumTagsPerList)
            throw new ArgumentException("At most " + MaximumTagsPerList + " tags are supported per list.", parameter);
        if (items.Any(item => item == Guid.Empty)) throw new ArgumentException("A tag reference cannot be empty.", parameter);
        return items.Distinct().OrderBy(item => item.ToString("D"), StringComparer.Ordinal).ToArray();
    }

    public bool Equals(ArchiveTagFilter? other) => other != null && Match == other.Match
        && Include.SequenceEqual(other.Include) && Exclude.SequenceEqual(other.Exclude);
    public override bool Equals(object? obj) => obj is ArchiveTagFilter other && Equals(other);
    public override int GetHashCode()
    {
        HashCode hash = new();
        hash.Add(Match);
        foreach (Guid id in Include) hash.Add(id);
        hash.Add(0);
        foreach (Guid id in Exclude) hash.Add(id);
        return hash.ToHashCode();
    }
}

internal enum ArchiveTagFilterError { TagsUnavailable, UnknownTag }

/// <summary>
/// A tag filter could not be bound to the current tag index. The search must not run without it:
/// dropping the filter would silently show more threads than the definition asks for.
/// </summary>
internal sealed class ArchiveTagFilterException : Exception
{
    internal ArchiveTagFilterException(ArchiveTagFilterError error)
        : base(error == ArchiveTagFilterError.UnknownTag
            ? "The search refers to a tag that no longer exists."
            : "The search uses tags, but the tag index is unavailable.") => Error = error;
    internal ArchiveTagFilterError Error { get; }
}
