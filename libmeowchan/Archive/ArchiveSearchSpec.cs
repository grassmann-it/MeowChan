using System;
using System.Collections.Generic;
using System.Linq;
using MeowChan.Interfaces;

namespace MeowChan.Archive;

public enum ArchiveWorkFilter { All, Watching, Followup, Intervention, Completed, Stopped }

/// <summary>A null source explicitly means this board name in every source.</summary>
public sealed record ArchiveBoardConstraint(string? Source, string Board);

/// <summary>Validated, immutable, canonical search definition. Work membership belongs to the desktop.</summary>
public sealed class ArchiveSearchSpec : IEquatable<ArchiveSearchSpec>
{
    /// <summary>Creates a definition from new input; source and board identifiers are trimmed.</summary>
    public ArchiveSearchSpec(string text = "", IEnumerable<string>? sources = null,
        IEnumerable<ArchiveBoardConstraint>? boards = null, IEnumerable<ThreadSourceState>? sourceStates = null,
        bool? captureEnabled = null, bool? hasPosts = null, ArchiveThreadSort sort = ArchiveThreadSort.LatestCapture,
        ArchiveWorkFilter workFilter = ArchiveWorkFilter.All, ArchiveDateRange? dateRange = null, int schemaVersion = 1)
        : this(true, text, sources, boards, sourceStates, captureEnabled, hasPosts, sort, workFilter, dateRange, schemaVersion) { }

    // Persisted, legacy and selected catalog identities are authoritative, not fresh text input.
    // Keep all validation/canonical set handling shared, without changing their identity through Trim().
    internal static ArchiveSearchSpec FromExactIdentities(string text = "", IEnumerable<string>? sources = null,
        IEnumerable<ArchiveBoardConstraint>? boards = null, IEnumerable<ThreadSourceState>? sourceStates = null,
        bool? captureEnabled = null, bool? hasPosts = null, ArchiveThreadSort sort = ArchiveThreadSort.LatestCapture,
        ArchiveWorkFilter workFilter = ArchiveWorkFilter.All, ArchiveDateRange? dateRange = null, int schemaVersion = 1)
        => new(false, text, sources, boards, sourceStates, captureEnabled, hasPosts, sort, workFilter, dateRange, schemaVersion);

    private ArchiveSearchSpec(bool normalizeIdentities, string text, IEnumerable<string>? sources,
        IEnumerable<ArchiveBoardConstraint>? boards, IEnumerable<ThreadSourceState>? sourceStates,
        bool? captureEnabled, bool? hasPosts, ArchiveThreadSort sort, ArchiveWorkFilter workFilter,
        ArchiveDateRange? dateRange, int schemaVersion)
    {
        if (schemaVersion != 1) throw new ArgumentOutOfRangeException(nameof(schemaVersion), "Unsupported search schema version.");
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > 512 || text.Contains('\0')) throw new ArgumentException("Search text must contain at most 512 characters and no NUL.", nameof(text));
        if (!Enum.IsDefined(sort)) throw new ArgumentOutOfRangeException(nameof(sort));
        if (!Enum.IsDefined(workFilter)) throw new ArgumentOutOfRangeException(nameof(workFilter));
        string[] sourceValues = (sources ?? []).Take(129).Select(value => Identity(value, nameof(sources), normalizeIdentities)).ToArray();
        ArchiveBoardConstraint[] boardValues = (boards ?? []).Take(129).Select(value => value == null
            ? throw new ArgumentException("A board selector cannot be null.", nameof(boards))
            : new ArchiveBoardConstraint(value.Source == null ? null : Identity(value.Source, nameof(boards), normalizeIdentities), Identity(value.Board, nameof(boards), normalizeIdentities))).ToArray();
        if (sourceValues.Length > 128) throw new ArgumentException("At most 128 sources are supported.", nameof(sources));
        if (boardValues.Length > 128) throw new ArgumentException("At most 128 board selectors are supported.", nameof(boards));
        ThreadSourceState[] stateValues = (sourceStates ?? []).ToArray();
        if (stateValues.Any(value => !Enum.IsDefined(value))) throw new ArgumentOutOfRangeException(nameof(sourceStates));
        SchemaVersion = schemaVersion;
        Text = text;
        Sources = Array.AsReadOnly(sourceValues.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());
        Boards = Array.AsReadOnly(boardValues.Distinct().OrderBy(value => value.Source, StringComparer.Ordinal).ThenBy(value => value.Board, StringComparer.Ordinal).ToArray());
        SourceStates = Array.AsReadOnly(stateValues.Distinct().Order().ToArray());
        CaptureEnabled = captureEnabled;
        HasPosts = hasPosts;
        Sort = sort;
        WorkFilter = workFilter;
        DateRange = dateRange is { StartDate: null, EndDate: null } ? null : dateRange;
    }

    public int SchemaVersion { get; }
    public string Text { get; }
    public IReadOnlyList<string> Sources { get; }
    public IReadOnlyList<ArchiveBoardConstraint> Boards { get; }
    public IReadOnlyList<ThreadSourceState> SourceStates { get; }
    public bool? CaptureEnabled { get; }
    public bool? HasPosts { get; }
    public ArchiveThreadSort Sort { get; }
    public ArchiveWorkFilter WorkFilter { get; }
    public ArchiveDateRange? DateRange { get; }

    public static ArchiveSearchSpec FromLegacy(ArchiveThreadQuery query, ArchiveWorkFilter workFilter = ArchiveWorkFilter.All)
    {
        ArgumentNullException.ThrowIfNull(query);
        query.Validate();
        return FromExactIdentities(query.Text, query.Source == null ? [] : [query.Source],
            query.Board == null ? [] : [new ArchiveBoardConstraint(query.Source, query.Board)],
            query.SourceState.HasValue ? [query.SourceState.Value] : [], query.CaptureEnabled, query.HasPosts, query.Sort, workFilter);
    }

    /// <summary>UI indices are adapted explicitly; they are never persisted.</summary>
    internal static ArchiveWorkFilter WorkFilterFromIndex(int index) => index switch
    {
        0 => ArchiveWorkFilter.All, 1 => ArchiveWorkFilter.Watching, 2 => ArchiveWorkFilter.Followup,
        3 => ArchiveWorkFilter.Intervention, 4 => ArchiveWorkFilter.Completed, 5 => ArchiveWorkFilter.Stopped,
        _ => throw new ArgumentOutOfRangeException(nameof(index))
    };

    internal static int WorkFilterToIndex(ArchiveWorkFilter filter) => filter switch
    {
        ArchiveWorkFilter.All => 0, ArchiveWorkFilter.Watching => 1, ArchiveWorkFilter.Followup => 2,
        ArchiveWorkFilter.Intervention => 3, ArchiveWorkFilter.Completed => 4, ArchiveWorkFilter.Stopped => 5,
        _ => throw new ArgumentOutOfRangeException(nameof(filter))
    };

    public bool Equals(ArchiveSearchSpec? other) => other != null && SchemaVersion == other.SchemaVersion && Text == other.Text
        && Sources.SequenceEqual(other.Sources) && Boards.SequenceEqual(other.Boards) && SourceStates.SequenceEqual(other.SourceStates)
        && CaptureEnabled == other.CaptureEnabled && HasPosts == other.HasPosts && Sort == other.Sort
        && WorkFilter == other.WorkFilter && Equals(DateRange, other.DateRange);
    public override bool Equals(object? obj) => obj is ArchiveSearchSpec other && Equals(other);
    public override int GetHashCode()
    {
        HashCode hash = new();
        hash.Add(SchemaVersion); hash.Add(Text); hash.Add(CaptureEnabled); hash.Add(HasPosts); hash.Add(Sort); hash.Add(WorkFilter); hash.Add(DateRange);
        foreach (string source in Sources) hash.Add(source);
        foreach (ArchiveBoardConstraint board in Boards) hash.Add(board);
        foreach (ThreadSourceState state in SourceStates) hash.Add(state);
        return hash.ToHashCode();
    }

    private static string Identity(string value, string parameter, bool normalize)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Contains('\0')) throw new ArgumentException("An identity must be nonempty and cannot contain NUL.", parameter);
        return normalize ? value.Trim() : value;
    }
}
