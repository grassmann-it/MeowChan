using System;
using System.Collections.Generic;

namespace MeowChan.Desktop.WorkViews;

/// <summary>
/// The three work views (UI: Minimal / Einfach / Erweitert). The profile keeps storing the setting string in
/// <see cref="Config.WorkspaceMode"/>; code compares this enum or asks <see cref="WorkViewPolicy"/>, never the string.
/// </summary>
public enum WorkView { Barebones, Simple, Advanced }

/// <summary>The only place that maps between <see cref="WorkView"/> and its stored setting string.</summary>
public static class WorkViewSetting
{
    public const string BarebonesName = "Barebones", SimpleName = "Simple", AdvancedName = "Advanced";

    /// <summary>All views from the most reduced to the fullest (the order R12 monotony is checked in).</summary>
    public static IReadOnlyList<WorkView> All { get; } = [WorkView.Barebones, WorkView.Simple, WorkView.Advanced];

    /// <summary>
    /// Reads a stored setting. <paramref name="exact"/> keeps the historical ordinal comparison of call sites that
    /// never trimmed or ignored case (the archive page's column mode); everything else trims and ignores case like
    /// <see cref="Config"/>. Anything unknown is <paramref name="fallback"/>.
    /// </summary>
    public static WorkView Parse(string? setting, WorkView fallback = WorkView.Barebones, bool exact = false)
    {
        string? text = exact ? setting : setting?.Trim();
        StringComparison comparison = exact ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        return string.Equals(text, AdvancedName, comparison) ? WorkView.Advanced
            : string.Equals(text, SimpleName, comparison) ? WorkView.Simple
            : string.Equals(text, BarebonesName, comparison) ? WorkView.Barebones
            : fallback;
    }

    public static string ToSetting(this WorkView view) => view switch
    {
        WorkView.Advanced => AdvancedName,
        WorkView.Simple => SimpleName,
        _ => BarebonesName
    };

    /// <summary>The window style class of a view (density only, never function visibility).</summary>
    public static string StyleClass(this WorkView view) => "view-" + view.ToSetting().ToLowerInvariant();
}

/// <summary>
/// Density and geometry that differ between views. These are layout values, not presence: what a view shows is
/// <see cref="WorkViewPolicy"/>, how tightly it is laid out lives here (and in the window classes view-*).
/// </summary>
public static class WorkViewDensity
{
    /// <summary>Barebones uses compact margins, row spacing and row height, a smaller window and closes the inspector.</summary>
    public static bool Compact(WorkView view) => view == WorkView.Barebones;

    /// <summary>Default width of the downloads progress column: narrower where Advanced adds its technical columns.</summary>
    public static double DownloadCompletionWidth(WorkView view) => view == WorkView.Advanced ? 140 : 300;

    /// <summary>Minimum width of the archive table pane: Advanced shows every column.</summary>
    public static double ArchiveTableMinWidth(WorkView view) => view == WorkView.Advanced ? 900 : 650;
}
