using System;
using System.Collections.Generic;
using System.Linq;

namespace MeowChan.Desktop.Commands;

/// <summary>Single source for the base order of the main workspace pages. The footer (until the person reorders it, see <see cref="FooterLayout"/>),
/// the More flyout, the ⋯ menu and the F1 help follow it. The default digits do not: Ctrl/Cmd+1, 2, 3 ... follow the footer buttons the person
/// chose from left to right, then the hidden pages (<see cref="Numbering"/>); the merged Plugins command remains a compatibility route.
/// Settings is not a numbered page (Primary+,); About has no shortcut. Status holds two segments (Statistics,
/// Diagnostics); their own commands (<see cref="StatusSegmentCommandIds"/>) open a segment and have no digit.
/// The pages come in three groups (work, set up, observe) that the footer, the More flyout and the ⋯ menu
/// set apart with separators. Moving a page here must go with the same move in MainWindow.axaml; a test couples the two.</summary>
public static class MainPageOrder
{
    private const string Prefix = "navigation.";
    /// <summary>Downloads, Archive, Media, Tagging, Booru | Rules, Plugins | Status: the default footer order and the three groups the
    /// separators set apart.</summary>
    public static IReadOnlyList<IReadOnlyList<string>> Groups { get; } = Array.AsReadOnly(new IReadOnlyList<string>[]
    {
        Array.AsReadOnly(new[] { "navigation.downloads", "navigation.archive", "navigation.media", "navigation.tagging", "navigation.booru" }),
        Array.AsReadOnly(new[] { "navigation.rules", "navigation.sources" }),
        Array.AsReadOnly(new[] { "navigation.status" }),
    });
    public static IReadOnlyList<string> CommandIds { get; } = Array.AsReadOnly(Groups.SelectMany(group => group).ToArray());
    /// <summary>0-based index of the group a main page belongs to.</summary>
    public static int Group(string commandId) => Groups.Select((group, index) => (group, index))
        .First(entry => entry.group.Contains(commandId, StringComparer.Ordinal)).index;
    public static bool Contains(string commandId) => CommandIds.Contains(commandId, StringComparer.Ordinal);
    /// <summary>Pages that stand in the footer only after the person switched them on (Tom, 14.11.2026: Booru). They still own a digit,
    /// after the visible ones, and stay reachable from More, the ⋯ menu and their shortcut.</summary>
    public static IReadOnlyList<string> DefaultHiddenPages { get; } = Array.AsReadOnly(new[] { "navigation.booru" });
    /// <summary>Downloads is the home page and cannot leave the footer; every other main page can (<see cref="FooterLayout"/>).</summary>
    public static bool CanHideFromFooter(string commandId) => commandId != "navigation.downloads";
    /// <summary>Most digits a page can own: Ctrl/Cmd+1 to 9. There are eight main pages, so no page is ever without one.</summary>
    public const int MaximumDigit = 9;

    private static IReadOnlyList<string> BuildDefaultNumbering() => Array.AsReadOnly(
        CommandIds.Where(id => !DefaultHiddenPages.Contains(id, StringComparer.Ordinal))
            .Concat(CommandIds.Where(id => DefaultHiddenPages.Contains(id, StringComparer.Ordinal))).ToArray());
    /// <summary>The numbering of a footer with the default layout: the visible footer buttons from left to right, then the hidden pages.</summary>
    public static IReadOnlyList<string> DefaultNumbering { get; } = BuildDefaultNumbering();
    private static volatile IReadOnlyList<string> _numbering = DefaultNumbering;
    /// <summary>
    /// The pages in digit order: the footer buttons the person chose, from left to right, take Ctrl/Cmd+1, 2, 3 ...; the pages
    /// they hid follow, so every page keeps a digit (Tom, 14.11.2026). It is set from the profile's footer layout when the shell
    /// applies it (<see cref="SetNumbering"/>); the shortcut defaults, the help and the tooltips all read it.
    /// </summary>
    public static IReadOnlyList<string> Numbering => _numbering;
    /// <summary>Sets <see cref="Numbering"/>. A list that is not a permutation of the main pages is ignored. True when the numbering changed.</summary>
    public static bool SetNumbering(IReadOnlyList<string>? order)
    {
        if (order == null || order.Count != CommandIds.Count || order.Distinct(StringComparer.Ordinal).Count() != order.Count
            || order.Any(id => !Contains(id))) return false;
        if (_numbering.SequenceEqual(order, StringComparer.Ordinal)) return false;
        _numbering = Array.AsReadOnly(order.ToArray());
        return true;
    }
    /// <summary>The digit (1 to 9) that opens the page now; null for a command that is no main page.</summary>
    public static int? Digit(string commandId)
    {
        IReadOnlyList<string> order = _numbering;
        for (int slot = 0; slot < order.Count && slot < MaximumDigit; slot++)
            if (string.Equals(order[slot], commandId, StringComparison.Ordinal)) return slot + 1;
        return null;
    }
    /// <summary>The main pages that own a digit, in digit order (Ctrl/Cmd+1 first). With every page numbered this is <see cref="Numbering"/>.</summary>
    public static IReadOnlyList<string> NumberedCommandIds => Numbering.Where(id => Digit(id) != null).ToArray();
    /// <summary>The default shortcut text of the page now ("Primary+3"); the catalog holds none because the digit follows the footer.</summary>
    public static string DefaultShortcut(string commandId) =>
        Digit(commandId) is int digit ? "Primary+" + digit : throw new ArgumentException("Not a main page: " + commandId, nameof(commandId));
    /// <summary>Localization tag used by the footer, flyout and menu items ("nav.media").</summary>
    public static string Tag(string commandId) => "nav." + commandId[Prefix.Length..];
    /// <summary>Stem of the XAML control names: MediaNavigationButton, FooterSeparatorAfterMedia.</summary>
    public static string ControlStem(string commandId) =>
        char.ToUpperInvariant(commandId[Prefix.Length]) + commandId[(Prefix.Length + 1)..];
    /// <summary>Pages that leave the footer for the More flyout first when the window is narrow.</summary>
    public static IReadOnlyList<string> OverflowOrder { get; } = Array.AsReadOnly(new[]
    {
        "navigation.status", "navigation.sources", "navigation.rules",
        "navigation.booru", "navigation.tagging", "navigation.media", "navigation.archive",
    });
    /// <summary>The commands that open one segment of the Status page. They are no main pages of their own and carry no
    /// default gesture (Primary+8 is free); a binding the user saved for them keeps working.</summary>
    public static IReadOnlyList<string> StatusSegmentCommandIds { get; } = Array.AsReadOnly(new[] { "navigation.statistics", "navigation.diagnostics" });
}
