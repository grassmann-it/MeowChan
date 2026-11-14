using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Automation;
using Avalonia.Controls;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;

namespace MeowChan.Desktop.WorkViews;

/// <summary>
/// The "More" (⋯) button of a page that has no <see cref="ActionToolBar"/> (Rules, Sources, Plugins). The page lists what its
/// view moved under More; this class shows the button only when something is there (R7: the accessible name announces the
/// count), builds the menu fresh on every opening and forwards each entry to the moved control, so no feature logic is copied.
/// </summary>
public static class WorkViewMoreSurface
{
    /// <summary>One entry under More: a catalog command id for label, icon and shortcut text, or none for a plain label.</summary>
    public sealed record Entry(int Order, string? CommandId, Func<string> Label, Action Run, Func<bool> CanRun);

    /// <summary>Entries for the buttons the policy puts under More in <paramref name="view"/> (state-hidden buttons are skipped).</summary>
    public static IEnumerable<Entry> ForButtons(IEnumerable<Button> buttons, WorkView view) => buttons
        .Select(button => (Button: button, Id: WorkViewElement.GetId(button)))
        .Where(item => item.Id != null && WorkViewElement.GetStateVisible(item.Button))
        .Select(item => (item.Button, Entry: WorkViewPolicy.Get(item.Id!)))
        .Where(item => item.Entry.For(view) == Presence.Overflow)
        .Select(item => new Entry(item.Entry.OverflowOrder, item.Entry.CommandId, () => LabelOf(item.Button),
            () => CommandBindings.InvokeAction(item.Button), () => item.Button.IsEnabled
                && (item.Button.Command?.CanExecute(item.Button.CommandParameter) ?? true)));

    /// <summary>Shows the button when there are entries and names it "More, n actions"; the icon is the catalog's ⋯.</summary>
    public static void Configure(Button more, int count)
    {
        more.IsVisible = count > 0;
        if (more.Content is not Control) more.Content = CommandBindings.Icon("plugins.actions");
        string name = Texts.Count("workview.moreCount", count);
        AutomationProperties.SetName(more, name);
        ToolTip.SetTip(more, Texts.T("workview.more"));
    }

    /// <summary>The menu for <paramref name="entries"/> in policy order (a disabled entry stays listed, greyed out).</summary>
    public static ContextMenu CreateMenu(IEnumerable<Entry> entries)
    {
        ContextMenu menu = new();
        foreach (Entry entry in entries.OrderBy(entry => entry.Order))
        {
            MenuItem item = entry.CommandId != null
                ? CommandBindings.CreateMenuItem(entry.CommandId, entry.Run, entry.CanRun, null, entry.Label)
                : new MenuItem { Header = entry.Label(), Command = new DelegateCommand(entry.Run, entry.CanRun), IsEnabled = entry.CanRun() };
            AutomationProperties.SetName(item, entry.Label());
            menu.Items.Add(item);
        }
        return menu;
    }

    /// <summary>Opens the menu under <paramref name="more"/>; nothing opens when no entry is left.</summary>
    public static void Open(Button more, IEnumerable<Entry> entries)
    {
        Entry[] current = entries.ToArray();
        if (current.Length == 0) return;
        CreateMenu(current).Open(more);
    }

    private static string LabelOf(Button button) =>
        AutomationProperties.GetName(button) is { Length: > 0 } name ? name : button.Content as string ?? string.Empty;
}
