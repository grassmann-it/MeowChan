using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Layout;

namespace MeowChan.Desktop.Controls;

/// <summary>
/// The one action footer of every dialog (modals plan MD1): status or progress on the left, secondary actions, then the
/// primary action right-most. The buttons keep their own classes (<c>primary</c>, <c>danger</c>, <c>subtle</c>), names,
/// commands and enabled state; the footer only orders and spaces them (8 dip, wrapping into further lines when narrow).
/// </summary>
/// <remarks>
/// The primary slot is the dialog's default way out (Close, Save, Discard), not necessarily an accent button. × in the
/// title bar always cancels or closes and never confirms, so a dialog whose only action is Close may leave the footer out.
/// </remarks>
internal static class DialogFooter
{
    internal const string StatusName = "DialogFooterStatus";
    internal const string ActionsName = "DialogFooterActions";

    /// <summary>Builds the footer border (class <c>workspace-dialog-footer</c>, so <see cref="DialogFrame"/> adopts it as is).</summary>
    internal static Border Build(Control? primary, IReadOnlyList<Control>? secondary = null, Control? status = null)
    {
        WrapPanel actions = new()
        {
            Name = ActionsName, ItemSpacing = 8, LineSpacing = 8, HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        foreach (Control control in Order(primary, secondary)) actions.Children.Add(control);
        Border footer = new();
        footer.Classes.Add("workspace-dialog-footer");
        if (status == null) { footer.Child = actions; return footer; }
        // Docked right, the actions are measured against the real width and still wrap; the status takes what is left.
        status.Name ??= StatusName;
        status.VerticalAlignment = VerticalAlignment.Center;
        status.Margin = new Avalonia.Thickness(0, 0, 8, 0);
        DockPanel row = new() { LastChildFill = true };
        DockPanel.SetDock(actions, Dock.Right);
        row.Children.Add(actions);
        row.Children.Add(status);
        footer.Child = row;
        return footer;
    }

    /// <summary>Left-to-right order of the actions: the secondary ones as given, the primary one last.</summary>
    internal static IReadOnlyList<Control> Order(Control? primary, IReadOnlyList<Control>? secondary)
    {
        List<Control> ordered = new();
        if (secondary != null)
            foreach (Control control in secondary)
            {
                ArgumentNullException.ThrowIfNull(control);
                if (!ReferenceEquals(control, primary)) ordered.Add(control);
            }
        if (primary != null) ordered.Add(primary);
        return ordered;
    }
}
