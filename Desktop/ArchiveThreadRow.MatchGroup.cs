using System.ComponentModel;
using MeowChan.Archive;

namespace MeowChan.Desktop;

internal sealed partial record ArchiveThreadRow
{
    /// <summary>Where a relevance-sorted search found this thread; null for any other result.</summary>
    public ArchiveSearchMatchTier? MatchTier { get; private set; }

    /// <summary>The group header shown above this row (the first row of its tier on the page), else null.</summary>
    public string? MatchGroupHeader { get; private set; }
    public bool HasMatchGroupHeader => MatchGroupHeader != null;

    /// <summary>The table's "Found in" cell: the tier's label, empty for an unranked result.</summary>
    public string FoundIn { get; private set; } = string.Empty;

    /// <summary>Table order of the "Found in" column: by tier; unranked rows last.</summary>
    public int FoundInSortKey => MatchTier is { } tier ? (int)tier : 3;

    internal void SetMatchGroup(ArchiveSearchMatchTier? tier, string? header, string foundIn)
    {
        if (MatchTier == tier && MatchGroupHeader == header && FoundIn == foundIn) return;
        MatchTier = tier;
        MatchGroupHeader = header;
        FoundIn = foundIn;
        foreach (string name in new[] { nameof(MatchTier), nameof(MatchGroupHeader), nameof(HasMatchGroupHeader), nameof(FoundIn), nameof(FoundInSortKey) })
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
