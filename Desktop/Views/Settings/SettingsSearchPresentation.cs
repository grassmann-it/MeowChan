using System;
using Avalonia.Input;

namespace MeowChan;

/// <summary>SP1: how a hit of the settings search names its section. A hit's group is "Section" or "Section › Group inside it"; the section is shown as a chip.</summary>
internal static class SettingsSearchChip
{
    /// <summary>The style class of the section chip in a result row (tests and smokes find it by it).</summary>
    internal const string ChipClass = "settings-search-section";

    private const string Separator = " › ";

    /// <summary>The section of a hit and, when there is one, the group inside the section (null otherwise).</summary>
    internal static (string Section, string? Inner) Split(string? group)
    {
        if (string.IsNullOrWhiteSpace(group)) return (string.Empty, null);
        int at = group.IndexOf(Separator, StringComparison.Ordinal);
        if (at < 0) return (group.Trim(), null);
        string inner = group[(at + Separator.Length)..].Trim();
        return (group[..at].Trim(), inner.Length == 0 ? null : inner);
    }
}

/// <summary>SP1: the keys that put the caret in the search field of the settings page. Ctrl+F is the command (see CommandCatalog); "/" is this one.</summary>
internal static class SettingsSearchShortcut
{
    /// <summary>
    /// A typed "/" (by the character, so it is the same key on every layout) without Ctrl, Alt or Meta, while no text is being entered
    /// (a text box, a number field or an open drop-down keeps its "/").
    /// </summary>
    internal static bool IsSlash(string? keySymbol, KeyModifiers modifiers, bool typingText) =>
        !typingText && keySymbol == "/" && (modifiers & (KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Meta)) == KeyModifiers.None;
}

/// <summary>SP1: the layout of the page header row. The search field has a flexible 240 to 480 dip column at the end of the row; in a narrow window it moves to a row of its own.</summary>
internal static class SettingsHeaderLayout
{
    internal const double SearchMinimumWidth = 240;
    internal const double SearchMaximumWidth = 480;
    /// <summary>The window width below which the title and the field no longer fit on one row (title 240 + field 240 + "More" + gaps and insets).</summary>
    internal const double StackBelowWidth = 640;

    internal static bool IsStacked(double editorWidth) => editorWidth < StackBelowWidth;
}
