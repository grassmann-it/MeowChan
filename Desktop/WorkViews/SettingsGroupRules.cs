using System;

namespace MeowChan.Desktop.WorkViews;

/// <summary>
/// The pure decisions behind the collapsible settings groups (WV8, no Avalonia): when a group is shown without its extra
/// header, when a change opens it, and how its header with the counter reads. The editor applies them to its expanders.
/// </summary>
public static class SettingsGroupRules
{
    /// <summary>A group the view shows directly (Direct/Required) loses its "More settings" header and expander: its content sits on the page.</summary>
    public static bool IsFlat(string groupId, WorkView view) => WorkViewPolicy.Resolve(groupId, view) != Presence.Collapsed;

    /// <summary>R6: a changed value or a field with an error (also an unchanged one the view collapses) is a non-default state.</summary>
    public static bool IsNonDefault(int changedCount, bool hasError) => changedCount > 0 || hasError;

    /// <summary>
    /// Opens a group when something new needs attention: more changed values than before, or an error that was not there.
    /// Anything already seen leaves the user's choice (a group they collapsed again) alone.
    /// </summary>
    public static bool ShouldOpen(int previousChanged, int changed, bool hadError, bool hasError) =>
        changed > previousChanged || hasError && !hadError;

    /// <summary>"More settings (29)" and, when values are changed, "More settings (29) · 3 changed".</summary>
    public static string Header(string moreSettings, int count, int changed, Func<int, string> changedText) =>
        moreSettings + " (" + count.ToString(System.Globalization.CultureInfo.CurrentCulture) + ")"
        + (changed > 0 ? " · " + changedText(changed) : string.Empty);
}
