using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace MeowChan.Desktop.Commands;

public enum ShortcutRowStatus { Default, Custom, Unavailable }

/// <summary>Another action that claims the same gesture where both can run; <see cref="BlocksSave"/> false for an advisory
/// (a new default that yields to the user's own binding).</summary>
public sealed record ShortcutConflict(string OtherCommandId, bool BlocksSave);

/// <summary>One action of the shortcuts window.</summary>
public sealed class ShortcutTableRow
{
    internal ShortcutTableRow(CommandDefinition command, int catalogIndex, string group, IReadOnlyList<string> areas,
        IReadOnlyList<ShortcutGesture> gestures, IReadOnlyList<string> gestureTexts, ShortcutRowStatus status,
        IReadOnlyList<ShortcutConflict> conflicts, IReadOnlyList<ShortcutIssue> problems, bool underMore, int preference)
    {
        Command = command; CatalogIndex = catalogIndex; Group = group; Areas = areas; Gestures = gestures; GestureTexts = gestureTexts;
        Status = status; Conflicts = conflicts; Problems = problems; UnderMore = underMore; Preference = preference;
        Label = command.Label;
        SearchText = string.Join("\n", new[] { Label, command.Id, string.Join(" ", command.ExecutionContexts) }.Concat(gestureTexts))
            .ToLower(CultureInfo.InvariantCulture);
    }
    public CommandDefinition Command { get; }
    public string CommandId => Command.Id;
    public string Label { get; }
    /// <summary>Position in the command catalog: the fixed order of the rows within a group.</summary>
    public int CatalogIndex { get; }
    public string Group { get; }
    public IReadOnlyList<string> Areas { get; }
    /// <summary>The gestures the draft asks for (a conflicting one included, so the table can show the conflict).</summary>
    public IReadOnlyList<ShortcutGesture> Gestures { get; }
    /// <summary><see cref="Gestures"/> in the platform's notation.</summary>
    public IReadOnlyList<string> GestureTexts { get; }
    public bool IsBound => Gestures.Count > 0;
    public ShortcutRowStatus Status { get; }
    /// <summary>Whether the row can be edited: an implemented action, or one that only waits for its handler (quick tagging).</summary>
    public bool IsEditable => Command.IsImplemented || Command.IsAwaitingHandler;
    public IReadOnlyList<ShortcutConflict> Conflicts { get; }
    public bool HasBlockingConflict => Conflicts.Any(c => c.BlocksSave) || Problems.Any(p => p.BlocksSave);
    /// <summary>Issues of this action without a partner (an invalid saved gesture, an unimplemented action with a binding).</summary>
    public IReadOnlyList<ShortcutIssue> Problems { get; }
    /// <summary>The current work view keeps this action's button under More.</summary>
    public bool UnderMore { get; }
    /// <summary>Position in the preferred list of the opening context (F1); <see cref="int.MaxValue"/> otherwise.</summary>
    public int Preference { get; }
    /// <summary>Lower-case label, id, contexts and gesture texts for the search.</summary>
    public string SearchText { get; }
}

public sealed record ShortcutTableGroup(string Key, bool IsCurrent, IReadOnlyList<ShortcutTableRow> Rows);

/// <summary>What the table shows: search text, area, filters and grouping. The order is fixed (no column sorting).</summary>
public sealed record ShortcutTableQuery(string Text = "", string? Area = null, bool AssignedOnly = false, bool ConflictsOnly = false,
    bool Grouped = true);

/// <summary>
/// The pure table of the shortcuts window (modals plan KS1): one row per catalog command with its group, gestures, status and
/// conflicts, plus filtering and grouping in a fixed order. Built from a draft, never from presentation state, so it is testable.
/// </summary>
public sealed class ShortcutTableModel
{
    private ShortcutTableModel(string? currentArea, ShortcutPlatform platform, IReadOnlyList<ShortcutTableRow> rows)
    { CurrentArea = currentArea; Platform = platform; Rows = rows; }

    public string? CurrentArea { get; }
    public ShortcutPlatform Platform { get; }
    /// <summary>Every catalog command exactly once, in catalog order.</summary>
    public IReadOnlyList<ShortcutTableRow> Rows { get; }
    /// <summary>Actions with a conflict or problem that blocks Save.</summary>
    public int BlockingCount => Rows.Count(row => row.HasBlockingConflict);

    public static ShortcutTableModel Build(ShortcutBindingsSection draft, ShortcutPlatform platform, string? currentArea = null,
        IReadOnlyList<string>? preferred = null, Func<string, bool>? underMore = null)
        => Build(draft, EffectiveShortcutBindings.Build(draft, platform), currentArea, preferred, underMore);

    public static ShortcutTableModel Build(ShortcutBindingsSection draft, EffectiveShortcutBindings snapshot, string? currentArea = null,
        IReadOnlyList<string>? preferred = null, Func<string, bool>? underMore = null)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(snapshot);
        ShortcutPlatform platform = snapshot.Platform;
        Dictionary<string, List<ShortcutConflict>> conflicts = new(StringComparer.Ordinal);
        Dictionary<string, List<ShortcutIssue>> problems = new(StringComparer.Ordinal);
        foreach (ShortcutIssue issue in snapshot.Issues)
        {
            if (issue.OtherCommandId is { } other)
            {
                // Symmetric: both sides of a pair name each other once.
                AddConflict(issue.CommandId, other, issue.BlocksSave);
                AddConflict(other, issue.CommandId, issue.BlocksSave);
            }
            else
            {
                if (!problems.TryGetValue(issue.CommandId, out var list)) problems[issue.CommandId] = list = new();
                list.Add(issue);
            }
        }
        Dictionary<string, int> preference = new(StringComparer.Ordinal);
        if (preferred != null)
            for (int i = 0; i < preferred.Count; i++) preference.TryAdd(preferred[i], i);
        List<ShortcutTableRow> rows = new(CommandCatalog.All.Count);
        for (int index = 0; index < CommandCatalog.All.Count; index++)
        {
            CommandDefinition command = CommandCatalog.All[index];
            IReadOnlyList<ShortcutGesture> gestures = snapshot.RequestedGestures.TryGetValue(command.Id, out var requested)
                ? requested : Array.Empty<ShortcutGesture>();
            string[] texts = gestures.Select(g => SafeDisplay(g, platform)).ToArray();
            ShortcutRowStatus status = !command.IsImplemented || command.Contexts.All(x => x.EndsWith(":planned", StringComparison.Ordinal))
                ? ShortcutRowStatus.Unavailable : draft.HasOverride(command.Id) ? ShortcutRowStatus.Custom : ShortcutRowStatus.Default;
            rows.Add(new ShortcutTableRow(command, index, ShortcutGroups.For(command, currentArea), ShortcutGroups.Areas(command),
                gestures, texts, status,
                conflicts.TryGetValue(command.Id, out var c) ? c.DistinctBy(x => (x.OtherCommandId, x.BlocksSave)).ToArray() : Array.Empty<ShortcutConflict>(),
                problems.TryGetValue(command.Id, out var p) ? p.ToArray() : Array.Empty<ShortcutIssue>(),
                underMore?.Invoke(command.Id) == true,
                preference.TryGetValue(command.Id, out int rank) ? rank : int.MaxValue));
        }
        return new ShortcutTableModel(currentArea, platform, rows.AsReadOnly());

        void AddConflict(string id, string other, bool blocks)
        {
            if (!conflicts.TryGetValue(id, out var list)) conflicts[id] = list = new();
            list.Add(new ShortcutConflict(other, blocks));
        }
    }

    /// <summary>An invalid saved gesture has no display; the table still shows its raw key instead of failing.</summary>
    private static string SafeDisplay(ShortcutGesture gesture, ShortcutPlatform platform)
    {
        try { return gesture.Display(platform); }
        catch (FormatException) { return string.Join("+", gesture.Modifiers.Append(gesture.Key)); }
    }

    /// <summary>Whether <paramref name="row"/> passes the filters of <paramref name="query"/> (not the area).</summary>
    public static bool Matches(ShortcutTableRow row, ShortcutTableQuery query)
    {
        if (query.AssignedOnly && (!row.IsBound || row.Status == ShortcutRowStatus.Unavailable)) return false;
        if (query.ConflictsOnly && row.Conflicts.Count == 0 && row.Problems.Count == 0) return false;
        string text = query.Text.Trim();
        return text.Length == 0 || row.SearchText.Contains(text.ToLower(CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    /// <summary>
    /// The visible groups for <paramref name="query"/>, in table order with the current area first. An area filter keeps the rows
    /// that run in that area, all in its group. Rows keep catalog order within a group; the opening context's preferred
    /// commands come first.
    /// </summary>
    public IReadOnlyList<ShortcutTableGroup> Query(ShortcutTableQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        IEnumerable<ShortcutTableRow> visible = Rows.Where(row => Matches(row, query));
        if (query.Area is { } area)
            visible = visible.Where(row => row.Group == area || row.Areas.Contains(area, StringComparer.Ordinal));
        ShortcutTableRow[] rows = visible.ToArray();
        if (!query.Grouped)
            return rows.Length == 0 ? Array.Empty<ShortcutTableGroup>() : new[] { new ShortcutTableGroup(ShortcutGroups.All, false, Ordered(rows)) };
        if (query.Area is { } only)
            return rows.Length == 0 ? Array.Empty<ShortcutTableGroup>() : new[] { new ShortcutTableGroup(only, only == CurrentArea, Ordered(rows)) };
        return ShortcutGroups.OrderFor(CurrentArea)
            .Select(key => new ShortcutTableGroup(key, key == CurrentArea, Ordered(rows.Where(row => row.Group == key))))
            .Where(group => group.Rows.Count > 0).ToArray();
    }

    private static IReadOnlyList<ShortcutTableRow> Ordered(IEnumerable<ShortcutTableRow> rows)
        => rows.OrderBy(row => row.Preference).ThenBy(row => row.CatalogIndex).ToArray();
}
