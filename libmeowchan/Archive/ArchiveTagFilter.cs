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
    internal const int DefaultTagsPerList = 32;
    internal const int MinimumTagsPerList = 8;
    /// <summary>Absolute per-list allocation/iteration ceiling, also for restoring saved filters.</summary>
    internal const int MaximumConfigurableTagsPerList = 128;
    // Compatibility name for callers that have not yet adopted a configurable admission limit.
    internal const int MaximumTagsPerList = DefaultTagsPerList;

    internal ArchiveTagFilter(IEnumerable<Guid>? include, IEnumerable<Guid>? exclude, ArchiveTagMatch match = ArchiveTagMatch.All)
        : this(include, exclude, match, DefaultTagsPerList) { }

    /// <summary>
    /// Admission limit for a new/expanded filter, not part of its identity. When restoring or reducing a saved
    /// filter, callers must use MaximumConfigurableTagsPerList, not a possibly lowered user preference.
    /// </summary>
    internal ArchiveTagFilter(IEnumerable<Guid>? include, IEnumerable<Guid>? exclude, ArchiveTagMatch match, int tagsPerList)
    {
        ValidateLimit(tagsPerList);
        if (!Enum.IsDefined(match)) throw new ArgumentOutOfRangeException(nameof(match));
        Guid[] included = Canonical(include, nameof(include), tagsPerList);
        Guid[] excluded = Canonical(exclude, nameof(exclude), tagsPerList);
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
        ArchiveTagMatch match = ArchiveTagMatch.All) => OrNull(include, exclude, match, DefaultTagsPerList);

    internal static ArchiveTagFilter? OrNull(IEnumerable<Guid>? include, IEnumerable<Guid>? exclude,
        ArchiveTagMatch match, int tagsPerList)
    {
        ValidateLimit(tagsPerList);
        Guid[] included = Canonical(include, nameof(include), tagsPerList);
        Guid[] excluded = Canonical(exclude, nameof(exclude), tagsPerList);
        return included.Length == 0 && excluded.Length == 0 ? null : new ArchiveTagFilter(included, excluded, match, tagsPerList);
    }

    internal IReadOnlyList<Guid> Include { get; }
    internal IReadOnlyList<Guid> Exclude { get; }
    internal ArchiveTagMatch Match { get; }

    private static void ValidateLimit(int tagsPerList)
    {
        // Validate before +1 or enumeration, including for an empty OrNull call.
        if (tagsPerList is < MinimumTagsPerList or > MaximumConfigurableTagsPerList)
            throw new ArgumentOutOfRangeException(nameof(tagsPerList));
    }

    private static Guid[] Canonical(IEnumerable<Guid>? values, string parameter, int tagsPerList)
    {
        Guid[] items = (values ?? []).Take(tagsPerList + 1).ToArray();
        if (items.Length > tagsPerList)
            throw new ArgumentException("At most " + tagsPerList + " tags are supported per list.", parameter);
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
