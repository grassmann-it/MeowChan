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

    /// <summary>
    /// Builds the footer border (class <c>workspace-dialog-footer</c>, so <see cref="DialogFrame"/> adopts it as is).
    /// <paramref name="statusAbove"/> stacks a long, multi-line status (a result summary that wraps) above the actions
    /// instead of docking it to their left, so it keeps the full width.
    /// </summary>
    internal static Border Build(Control? primary, IReadOnlyList<Control>? secondary = null, Control? status = null, bool statusAbove = false)
    {
        WrapPanel actions = new()
        {
            Name = ActionsName, ItemSpacing = 8, LineSpacing = 8, HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        actions.Classes.Add("workspace-actions"); // the shared action-row style: 8 dip spacing, right aligned (as set above)
        foreach (Control control in Order(primary, secondary)) actions.Children.Add(control);
        Border footer = new();
        footer.Classes.Add("workspace-dialog-footer");
        if (status == null) { footer.Child = actions; return footer; }
        // Docked right, the actions are measured against the real width and still wrap; the status takes what is left.
        status.Name ??= StatusName;
        status.VerticalAlignment = VerticalAlignment.Center;
        if (statusAbove)
        {
            status.Margin = default;
            footer.Child = new StackPanel { Spacing = 12, Children = { status, actions } };
            return footer;
        }
        status.Margin = new Avalonia.Thickness(0, 0, 8, 0);
        DockPanel row = new() { LastChildFill = true };
        DockPanel.SetDock(actions, Dock.Right);
        row.Children.Add(actions);
        row.Children.Add(status);
        footer.Child = row;
        return footer;
    }

    /// <summary>
    /// Fills the footer border <paramref name="host"/> of a window declared in XAML (the named <c>DialogFrameFooter</c>) the way
    /// <see cref="Build"/> does for code-built dialogs. The controls keep their names; they are taken out of the XAML's own
    /// panels first, so a control keeps its single parent.
    /// </summary>
    internal static void Fill(Border host, Control? primary, IReadOnlyList<Control>? secondary = null, Control? status = null, bool statusAbove = false)
    {
        ArgumentNullException.ThrowIfNull(host);
        host.Child = null;
        foreach (Control control in Order(primary, secondary)) Detach(control);
        if (status != null) Detach(status);
        Border built = Build(primary, secondary, status, statusAbove);
        Control? content = built.Child;
        built.Child = null;
        host.Child = content;
    }

    private static void Detach(Control control)
    {
        switch (control.Parent)
        {
            case Panel panel: panel.Children.Remove(control); break;
            case Decorator decorator: decorator.Child = null; break;
            case ContentControl holder: holder.Content = null; break;
        }
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
