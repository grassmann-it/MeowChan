using System;

namespace MeowChan.Desktop.Theming;

/// <summary>
/// The stored theme preference (<c>Config.Theme</c>: System, Light or Dark) and what the header theme button and its context
/// menu make of it. Pure: the window passes the stored preference and whether the effective variant is dark.
/// </summary>
internal static class ThemePreference
{
    internal const string System = "System", Light = "Light", Dark = "Dark";

    /// <summary>Same reading as <c>Config.Theme</c>: Light, System, anything else Dark.</summary>
    internal static string Normalize(string? preference) =>
        string.Equals(preference?.Trim(), Light, StringComparison.OrdinalIgnoreCase) ? Light
        : string.Equals(preference?.Trim(), System, StringComparison.OrdinalIgnoreCase) ? System : Dark;

    internal static bool FollowsSystem(string? preference) => Normalize(preference) == System;

    /// <summary>
    /// A click on the theme button: the opposite of what is shown now, stored explicitly. From System this is the opposite
    /// of the system's current variant; "Follow system" in the context menu restores System.
    /// </summary>
    internal static string Toggled(string? preference, bool effectiveDark) => effectiveDark ? Light : Dark;

    /// <summary>
    /// The "Follow system" switch: from Light or Dark it restores System; from System it keeps the variant shown now as an
    /// explicit choice, so switching it off never changes the colours.
    /// </summary>
    internal static string FollowSystemToggled(string? preference, bool effectiveDark) =>
        FollowsSystem(preference) ? (effectiveDark ? Dark : Light) : System;
}
