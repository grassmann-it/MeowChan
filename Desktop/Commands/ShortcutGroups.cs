using System;
using System.Collections.Generic;
using System.Linq;
using MeowChan.Desktop.Localization;

namespace MeowChan.Desktop.Commands;

/// <summary>
/// The areas of the shortcuts window (modals plan KS1): the main pages in <see cref="MainPageOrder"/>, then the large reader, the
/// gallery, the quick tagging panel, Settings, dialogs and the application-wide commands. Each command belongs to exactly one
/// group; a command that runs in several areas lists under the current area when it runs there (F1 opens on it).
/// </summary>
public static class ShortcutGroups
{
    public const string Reader = "reader";
    public const string Gallery = "gallery";
    public const string TagPanel = "tag-panel";
    public const string Settings = "settings";
    public const string Dialogs = "dialogs";
    public const string Application = "application";
    /// <summary>The single group of an ungrouped table.</summary>
    public const string All = "all";

    /// <summary>Group keys of the numbered main pages, in footer order ("downloads", "archive", ...). The tagging page is no group of its
    /// own: its commands run in the "tagging" context, which <see cref="AreaOf"/> files under the application commands.</summary>
    public static IReadOnlyList<string> Pages { get; } = Array.AsReadOnly(MainPageOrder.NumberedCommandIds.Select(PageKey).ToArray());

    /// <summary>Every group in table order (without a current area).</summary>
    public static IReadOnlyList<string> Order { get; } = Array.AsReadOnly(
        Pages.Concat(new[] { Reader, Gallery, TagPanel, Settings, Dialogs, Application }).ToArray());

    /// <summary>Groups that collect a kind of surface rather than a place: a command there never moves to the current area.</summary>
    public static bool IsFixed(string group) => group is TagPanel or Dialogs or Application;

    /// <summary>The group key of a main page command ("navigation.media" gives "media").</summary>
    public static string PageKey(string navigationCommandId) => navigationCommandId["navigation.".Length..];

    /// <summary>
    /// Table order with <paramref name="current"/> first (when it is a group). The gallery's tag panel is part of that surface
    /// and follows the gallery directly.
    /// </summary>
    public static IReadOnlyList<string> OrderFor(string? current)
    {
        if (current == null || !Order.Contains(current, StringComparer.Ordinal)) return Order;
        string[] first = current == Gallery ? new[] { Gallery, TagPanel } : new[] { current };
        return first.Concat(Order.Where(g => !first.Contains(g))).ToArray();
    }

    /// <summary>The area an execution context belongs to; null for a context that names no area (a planned surface).</summary>
    public static string? AreaOf(string executionContext)
    {
        if (executionContext.StartsWith("dialog", StringComparison.Ordinal)) return Dialogs;
        // The reader's own surfaces: its history, its gallery return and its post pages.
        if (executionContext.StartsWith("reader-", StringComparison.Ordinal)) return Reader;
        return executionContext switch
        {
            // The header display menu, the toasts and the tagging page (opened from the app menu, no footer page) are window surfaces.
            "shell" or "about" or "header-io-monitor" or "notification" or "tagging" => Application,
            "gallery-tagging" => Gallery,
            "reader" or "large-reader" => Reader,
            "gallery" => Gallery,
            "settings" => Settings,
            // The merged pages (goal): plugin commands run on the Plugins page, diagnostics and statistics on the Status page.
            "plugins" when Pages.Contains("sources", StringComparer.Ordinal) => "sources",
            "diagnostics" or "statistics" when Pages.Contains("status", StringComparer.Ordinal) => "status",
            _ when Pages.Contains(executionContext, StringComparer.Ordinal) => executionContext,
            _ => null
        };
    }

    /// <summary>The areas where <paramref name="command"/> runs, in table order.</summary>
    public static IReadOnlyList<string> Areas(CommandDefinition command)
    {
        HashSet<string> areas = new(command.ExecutionContexts.Select(AreaOf).OfType<string>(), StringComparer.Ordinal);
        return Order.Where(areas.Contains).ToArray();
    }

    /// <summary>The group a command lists under when no area is current.</summary>
    public static string Primary(CommandDefinition command)
    {
        if (command.Id.StartsWith("tags.quick.", StringComparison.Ordinal)) return TagPanel;
        IReadOnlyList<string> contexts = command.ExecutionContexts;
        if (contexts.Contains("shell", StringComparer.Ordinal)) return Application;
        if (contexts.Count > 0 && contexts.All(c => c.StartsWith("dialog:", StringComparison.Ordinal))) return Dialogs;
        return Areas(command).FirstOrDefault(area => area != Dialogs) ?? Areas(command).FirstOrDefault() ?? Application;
    }

    /// <summary>The group of <paramref name="command"/> in a table whose current area is <paramref name="current"/>.</summary>
    public static string For(CommandDefinition command, string? current)
    {
        string primary = Primary(command);
        if (current == null || IsFixed(primary) || IsFixed(current) || primary == current) return primary;
        return Areas(command).Contains(current, StringComparer.Ordinal) ? current : primary;
    }

    /// <summary>The visible name of a group; the pages use their navigation label.</summary>
    public static string Label(string group) => group switch
    {
        Reader => Texts.T("shortcuts.group.reader"),
        Gallery => Texts.T("shortcuts.group.gallery"),
        TagPanel => Texts.T("shortcuts.group.tagPanel"),
        Settings => CommandCatalog.Label("navigation.settings"),
        Dialogs => Texts.T("shortcuts.group.dialogs"),
        Application => Texts.T("shortcuts.group.application"),
        All => Texts.T("shortcuts.window.area.all"),
        _ when Pages.Contains(group, StringComparer.Ordinal) => CommandCatalog.Label("navigation." + group),
        _ => throw new ArgumentOutOfRangeException(nameof(group), group, "Unknown shortcut group.")
    };
}
