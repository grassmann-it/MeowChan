using System;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Theming;

namespace MeowChan.Desktop;

/// <summary>
/// The context menu of the header theme button (right-click, Menu key, Shift+F10): the switch "Follow system", checked while
/// the stored preference is System. Checking it restores System; unchecking it keeps the variant shown now as an explicit
/// choice (<see cref="ThemePreference.FollowSystemToggled"/>). Pure composition: the window supplies the stored preference,
/// the effective variant and what a choice does.
/// </summary>
internal static class HeaderThemeMenu
{
    internal const string FollowSystemId = "header.theme.followSystem";

    /// <param name="preference">The stored <c>Config.Theme</c>.</param>
    /// <param name="effectiveDark">Whether the window shows the dark variant now.</param>
    /// <param name="choose">Stores and applies a preference (System, Light or Dark).</param>
    internal static ContextMenuBuilder Compose(string? preference, bool effectiveDark, Action<string> choose)
    {
        ArgumentNullException.ThrowIfNull(choose);
        return new ContextMenuBuilder().Named(Texts.T("settings.theme"))
            .Check(FollowSystemId, Texts.T("shell.theme.followSystem"), ThemePreference.FollowsSystem(preference),
                () => choose(ThemePreference.FollowSystemToggled(preference, effectiveDark)));
    }
}
