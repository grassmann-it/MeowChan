using System;
using System.Collections.Generic;
using System.Linq;

namespace MeowChan.Desktop.WorkViews;

/// <summary>
/// A diff cell's meaning. Columns deliberately have their own vocabulary: an off column is offered in the chooser,
/// not a tool under More; a column that is not offered does not hide any data rows.
/// </summary>
public enum WorkViewDiffPlacement { Direct, Overflow, Collapsed, Hidden, ColumnOn, ColumnOff, ColumnNotOffered }

/// <summary>One changed policy row, not a command (several rows may share a command). No live control state is read.</summary>
public sealed class WorkViewDiffChange
{
    public WorkViewEntry Entry { get; }
    public Presence From { get; }
    public Presence To { get; }
    public WorkViewDiffPlacement FromPlacement { get; }
    public WorkViewDiffPlacement ToPlacement { get; }

    /// <summary>Positive = more direct, negative = more reduced. Required and Direct have equal rank.</summary>
    public int RankDelta => WorkViewPolicy.Rank(To) - WorkViewPolicy.Rank(From);

    /// <summary>A lock is metadata, never a suggestion that this row can be hidden or edited.</summary>
    public bool IsLocked => Entry.Safety || Entry.Barebones == Presence.Required
        || Entry.Simple == Presence.Required || Entry.Advanced == Presence.Required;

    internal WorkViewDiffChange(WorkViewEntry entry, Presence from, Presence to)
    {
        Entry = entry;
        From = from;
        To = to;
        FromPlacement = Placement(entry.Kind, from);
        ToPlacement = Placement(entry.Kind, to);
    }

    private static WorkViewDiffPlacement Placement(WorkViewKind kind, Presence presence) => kind == WorkViewKind.Column
        ? presence switch
        {
            Presence.Direct or Presence.Required => WorkViewDiffPlacement.ColumnOn,
            Presence.Overflow => WorkViewDiffPlacement.ColumnOff,
            Presence.Hidden => WorkViewDiffPlacement.ColumnNotOffered,
            _ => throw new InvalidOperationException("A column has no collapsed placement.")
        }
        : presence switch
        {
            Presence.Direct or Presence.Required => WorkViewDiffPlacement.Direct,
            Presence.Overflow => WorkViewDiffPlacement.Overflow,
            Presence.Collapsed => WorkViewDiffPlacement.Collapsed,
            Presence.Hidden => WorkViewDiffPlacement.Hidden,
            _ => throw new InvalidOperationException("Unknown work-view presence.")
        };
}

/// <summary>
/// Counts of changed rows only. The seven To* buckets are disjoint and sum to Total. MoreDirect/MoreReduced/SameRank
/// are a separate partition by rank, not additional changes. All counts include column policy rows (including '*' defaults),
/// not the number of concrete grid columns or controls. ToOverflow and ToHidden never include columns.
/// </summary>
public sealed class WorkViewDiffCounts
{
    public int Total { get; }
    public int MoreDirect { get; }
    public int MoreReduced { get; }
    public int SameRank { get; }
    public int ToDirect { get; }
    public int ToOverflow { get; }
    public int ToCollapsed { get; }
    public int ToHidden { get; }
    public int ToColumnOn { get; }
    public int ToColumnOff { get; }
    public int ToColumnNotOffered { get; }

    internal WorkViewDiffCounts(IReadOnlyList<WorkViewDiffChange> changes)
    {
        Total = changes.Count;
        MoreDirect = changes.Count(change => change.RankDelta > 0);
        MoreReduced = changes.Count(change => change.RankDelta < 0);
        SameRank = changes.Count(change => change.RankDelta == 0);
        ToDirect = Count(WorkViewDiffPlacement.Direct);
        ToOverflow = Count(WorkViewDiffPlacement.Overflow);
        ToCollapsed = Count(WorkViewDiffPlacement.Collapsed);
        ToHidden = Count(WorkViewDiffPlacement.Hidden);
        ToColumnOn = Count(WorkViewDiffPlacement.ColumnOn);
        ToColumnOff = Count(WorkViewDiffPlacement.ColumnOff);
        ToColumnNotOffered = Count(WorkViewDiffPlacement.ColumnNotOffered);

        int Count(WorkViewDiffPlacement placement) => changes.Count(change => change.ToPlacement == placement);
    }
}

/// <summary>One changed page, with its rows in the same order as the policy and independent counts.</summary>
public sealed class WorkViewPageDiff
{
    public WorkViewPage Page { get; }
    public IReadOnlyList<WorkViewDiffChange> Changes { get; }
    public WorkViewDiffCounts Counts { get; }

    internal WorkViewPageDiff(WorkViewPage page, WorkViewDiffChange[] changes)
    {
        Page = page;
        Changes = Array.AsReadOnly(changes);
        Counts = new WorkViewDiffCounts(Changes);
    }
}

/// <summary>
/// Immutable default-policy comparison for WV12's toast and WV11's difference filter. Changes keep Entries order;
/// Pages contains only changed pages in first-appearance order. Entry retains Id, Kind, CommandId, Alternative and Safety
/// for the overview's own label/localization layer. This is not an observation of the current visual tree: R6 state,
/// saved column choices (R10), feature availability and responsive overflow can override these defaults at runtime.
/// </summary>
public sealed class DiffSummary
{
    public WorkView From { get; }
    public WorkView To { get; }
    public IReadOnlyList<WorkViewDiffChange> Changes { get; }
    public WorkViewDiffCounts Counts { get; }
    public IReadOnlyList<WorkViewPageDiff> Pages { get; }
    public bool IsEmpty => Changes.Count == 0;

    internal DiffSummary(WorkView from, WorkView to, WorkViewDiffChange[] changes)
    {
        From = from;
        To = to;
        Changes = Array.AsReadOnly(changes);
        Counts = new WorkViewDiffCounts(Changes);
        Pages = Array.AsReadOnly(changes.GroupBy(change => change.Entry.Page)
            .Select(page => new WorkViewPageDiff(page.Key, page.ToArray())).ToArray());
    }
}

public static partial class WorkViewPolicy
{
    /// <summary>
    /// Compares every row's declared presence exactly once, including columns and derived defaults. No-op comparisons
    /// are empty. Required/Safety rows in the current policy never change. Invalid lock reductions fail rather than
    /// being silently omitted or advertised as hideable. No controls, configuration, catalog or localization are read.
    /// </summary>
    public static DiffSummary Diff(WorkView from, WorkView to) => Diff(from, to, Entries);

    // Internal snapshot seam for pure edge-case tests; not an alternate public policy or a customization API.
    internal static DiffSummary Diff(WorkView from, WorkView to, IEnumerable<WorkViewEntry> entries)
    {
        if (!Enum.IsDefined(from)) throw new ArgumentOutOfRangeException(nameof(from));
        if (!Enum.IsDefined(to)) throw new ArgumentOutOfRangeException(nameof(to));
        ArgumentNullException.ThrowIfNull(entries);

        var changes = new List<WorkViewDiffChange>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (WorkViewEntry entry in entries)
        {
            if (!ids.Add(entry.Id)) throw new InvalidOperationException("Duplicate work-view row: " + entry.Id);
            ValidateDiffEntry(entry);
            Presence before = entry.For(from), after = entry.For(to);
            if (before != after) changes.Add(new WorkViewDiffChange(entry, before, after));
        }
        return new DiffSummary(from, to, changes.ToArray());
    }

    private static void ValidateDiffEntry(WorkViewEntry entry)
    {
        bool locked = entry.Safety || entry.Barebones == Presence.Required
            || entry.Simple == Presence.Required || entry.Advanced == Presence.Required;
        foreach (WorkView view in WorkViewSetting.All)
        {
            Presence presence = entry.For(view);
            if (!Enum.IsDefined(presence)) throw new InvalidOperationException("Unknown presence: " + entry.Id);
            if (entry.Safety && presence != Presence.Required
                || locked && Rank(presence) < Rank(Presence.Direct))
                throw new InvalidOperationException("A Required/Safety row cannot be reduced: " + entry.Id);
            if (entry.Kind == WorkViewKind.Column && presence == Presence.Collapsed)
                throw new InvalidOperationException("A column cannot be collapsed: " + entry.Id);
        }
    }
}
