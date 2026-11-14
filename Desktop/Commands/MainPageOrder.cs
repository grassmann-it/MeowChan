using System;
using System.Collections.Generic;
using System.Linq;

namespace MeowChan.Desktop.Commands;

/// <summary>Single source for the order of the main workspace pages. The footer, the More flyout, the
/// ⋯ menu, the F1 help and the default digit shortcuts all follow it: the n-th page from the left is
/// Primary+n. Settings is not a numbered page (Primary+,); About has no shortcut. Status holds two segments (Statistics,
/// Diagnostics); their own commands (<see cref="StatusSegmentCommandIds"/>) open a segment and have no digit.
/// The pages come in three groups (work, set up, observe) that the footer, the More flyout and the ⋯ menu
/// set apart with separators. Moving a page here must go with the same move in MainWindow.axaml; a test couples the two.</summary>
public static class MainPageOrder
{
    private const string Prefix = "navigation.";
    /// <summary>Downloads, Archive, Media | Rules, Sources, Plugins | Status.</summary>
    public static IReadOnlyList<IReadOnlyList<string>> Groups { get; } = Array.AsReadOnly(new IReadOnlyList<string>[]
    {
        Array.AsReadOnly(new[] { "navigation.downloads", "navigation.archive", "navigation.media" }),
        Array.AsReadOnly(new[] { "navigation.rules", "navigation.sources", "navigation.plugins" }),
        Array.AsReadOnly(new[] { "navigation.status" }),
    });
    public static IReadOnlyList<string> CommandIds { get; } = Array.AsReadOnly(Groups.SelectMany(group => group).ToArray());
    /// <summary>0-based index of the group a main page belongs to.</summary>
    public static int Group(string commandId) => Groups.Select((group, index) => (group, index))
        .First(entry => entry.group.Contains(commandId, StringComparer.Ordinal)).index;
    public static bool Contains(string commandId) => CommandIds.Contains(commandId, StringComparer.Ordinal);
    /// <summary>1-based position, which is also the default digit shortcut; null for other commands.</summary>
    public static int? Digit(string commandId) => Contains(commandId) ? Array.IndexOf(CommandIds.ToArray(), commandId) + 1 : null;
    /// <summary>The default shortcut text written into the command catalog.</summary>
    public static string DefaultShortcut(string commandId) =>
        Digit(commandId) is int digit ? "Primary+" + digit : throw new ArgumentException("Not a main page: " + commandId, nameof(commandId));
    /// <summary>Localization tag used by the footer, flyout and menu items ("nav.media").</summary>
    public static string Tag(string commandId) => "nav." + commandId[Prefix.Length..];
    /// <summary>Stem of the XAML control names: MediaNavigationButton, MediaMenuItem.</summary>
    public static string ControlStem(string commandId) =>
        char.ToUpperInvariant(commandId[Prefix.Length]) + commandId[(Prefix.Length + 1)..];
    /// <summary>Pages that leave the footer for the More flyout first when the window is narrow.</summary>
    public static IReadOnlyList<string> OverflowOrder { get; } = Array.AsReadOnly(new[]
    {
        "navigation.status", "navigation.sources", "navigation.plugins", "navigation.rules",
        "navigation.media", "navigation.archive",
    });
    /// <summary>The commands that open one segment of the Status page. They are no main pages of their own and carry no
    /// default gesture (Primary+8 is free); a binding the user saved for them keeps working.</summary>
    public static IReadOnlyList<string> StatusSegmentCommandIds { get; } = Array.AsReadOnly(new[] { "navigation.statistics", "navigation.diagnostics" });
}
