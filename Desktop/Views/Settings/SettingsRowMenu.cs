using System;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;

namespace MeowChan;

/// <summary>
/// The context menu of a settings row: reset the setting to its default and copy its id. Pure composition. A row without a registered default
/// lists the reset as unavailable and says so; a row that already holds its default says that.
/// </summary>
internal static class SettingsRowMenu
{
    internal const string Reset = "settings.reset-default";
    internal const string CopyId = "settings.copy-id";

    /// <summary>What a reset would do for the row.</summary>
    internal enum ResetState
    {
        /// <summary>No default is known for the row's setting.</summary>
        NoDefault,
        /// <summary>The draft already holds the default.</summary>
        AtDefault,
        /// <summary>The draft differs from the default.</summary>
        Resettable
    }

    /// <param name="label">The row's label (the menu's accessible name).</param>
    /// <param name="id">The setting's id, null when the row has none (then "Copy setting id" is unavailable).</param>
    internal static ContextMenuBuilder Compose(string label, string? id, ResetState state, Action reset, Action copyId)
    {
        ArgumentNullException.ThrowIfNull(reset);
        ArgumentNullException.ThrowIfNull(copyId);
        string? reason = state switch
        {
            ResetState.NoDefault => Texts.T("ctxmenu.setting.noDefault"),
            ResetState.AtDefault => Texts.T("ctxmenu.setting.atDefault"),
            _ => null
        };
        return new ContextMenuBuilder().Named(Texts.T("ctxmenu.name.setting") + ": " + label)
            .Command(Reset, reset, state == ResetState.Resettable, reason)
            .Section().Command(CopyId, copyId, id != null, id != null ? null : Texts.T("menu.unavailable.target"));
    }
}
