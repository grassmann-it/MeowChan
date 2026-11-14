using MeowChan.Desktop.Localization;

namespace MeowChan;

/// <summary>
/// The sentences a screen reader hears from the settings search. Avalonia has no active-descendant link from the field to the
/// list, so the current hit and the empty result are spoken through texts of the field itself (help text, polite live region).
/// </summary>
internal static class SettingsSearchAnnouncement
{
    /// <summary>"3 of 12: Download folder, Downloads"; <paramref name="position"/> is one-based. An empty group is left out.</summary>
    internal static string Current(int position, int count, string label, string group)
    {
        if (count <= 0 || position < 1 || position > count) return string.Empty;
        return string.IsNullOrWhiteSpace(group)
            ? Texts.T("settings.search.current", position, count, label, string.Empty).TrimEnd().TrimEnd(',', '、', '，').TrimEnd()
            : Texts.T("settings.search.current", position, count, label, group);
    }

    /// <summary>The state with no match, naming the query, so it is spoken as the list empties.</summary>
    internal static string None(string query) => Texts.T("settings.search.none", query.Trim());
}
