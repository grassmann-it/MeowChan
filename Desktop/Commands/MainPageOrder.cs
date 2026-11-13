using System;
using System.Collections.Generic;
using System.Linq;

namespace MeowChan.Desktop.Commands;

/// <summary>Single source for the order of the main workspace pages. The footer, the Tools flyout, the
/// ⋯ menu, the F1 help and the default digit shortcuts all follow it: the n-th page from the left is
/// Primary+n. Settings is not a numbered page (Primary+,); About has no shortcut.
/// Moving a page here must go with the same move in MainWindow.axaml; a test couples the two.</summary>
public static class MainPageOrder
{
    private const string Prefix = "navigation.";
    public static IReadOnlyList<string> CommandIds { get; } = Array.AsReadOnly(new[]
    {
        "navigation.downloads", "navigation.archive", "navigation.media", "navigation.rules",
        "navigation.sources", "navigation.diagnostics", "navigation.plugins",
    });
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
    /// <summary>Pages that leave the footer for the Tools flyout first when the window is narrow.</summary>
    public static IReadOnlyList<string> OverflowOrder { get; } = Array.AsReadOnly(new[]
    {
        "navigation.diagnostics", "navigation.sources", "navigation.plugins", "navigation.rules",
        "navigation.media", "navigation.archive",
    });
}
