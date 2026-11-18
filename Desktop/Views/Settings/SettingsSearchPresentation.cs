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

/// <summary>
/// SP1 / UX-LEDGER-02: the layout of the page header. The search field and the "More" button lie on a row over the content column (the right-hand
/// column of the form); the field takes all the width the button leaves, so its edges are the edges of the content column, not of the window or the navigation.
/// </summary>
internal static class SettingsHeaderLayout
{
    /// <summary>The category navigation column of the shared form grid (SettingsEditor.axaml: ColumnDefinitions="176,*").</summary>
    internal const double NavigationWidth = 176;
    /// <summary>The gap between the navigation column and the content column.</summary>
    internal const double NavigationGap = 16;
    /// <summary>The window width from which the category navigation column is shown.</summary>
    internal const double CategoriesFromWidth = 820;
    /// <summary>The inset of the settings content inside its scroll viewer on both sides (SettingsContent.Width = column - 20); the header row has the same margin.</summary>
    internal const double ContentInset = 10;

    internal static bool ShowsCategories(double editorWidth) => editorWidth >= CategoriesFromWidth;
}
