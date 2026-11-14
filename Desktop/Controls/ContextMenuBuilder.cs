using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Automation;
using Avalonia.Controls;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.MediaActions;

namespace MeowChan.Desktop.Controls;

/// <summary>
/// Composes a context menu from sections: catalog commands (label, icon, shortcut and the reason an unavailable one gives come
/// with the command), exclusive choices, switches and notes. It is the common builder for the menus that are not about a media
/// target (those use <see cref="MediaMenuBuilder"/>, which builds the same kind of menu for a frozen target). The composition is
/// plain data (<see cref="ToModel"/>), <see cref="Build"/> makes the Avalonia menu, and <see cref="ContextMenuHost"/> opens it on
/// a right-click and on the context key and gives the focus back.
/// Rules for every surface: sections of at most <see cref="ContextMenuModel.MaxEntriesPerSection"/> entries, a destructive entry last,
/// entries that are actions are catalog commands so their translation and shortcut come with them.
/// </summary>
internal sealed class ContextMenuBuilder
{
    private readonly List<List<ContextMenuEntry>> _sections = [[]];
    private readonly HashSet<string> _ids = new(StringComparer.Ordinal);
    private string? _name;

    /// <summary>The accessible name of the menu (what a screen reader announces when it opens).</summary>
    internal ContextMenuBuilder Named(string name)
    {
        _name = name;
        return this;
    }

    /// <summary>Starts the next section (a separator); a section that stays empty leaves no separator behind.</summary>
    internal ContextMenuBuilder Section()
    {
        if (_sections[^1].Count > 0) _sections.Add([]);
        return this;
    }

    /// <summary>A catalog command. <paramref name="label"/> replaces the catalog label where the surface needs a more specific one.</summary>
    internal ContextMenuBuilder Command(string commandId, Action invoke, bool available = true, string? reason = null, string? label = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(commandId);
        ArgumentNullException.ThrowIfNull(invoke);
        return Add(new(ContextMenuEntryKind.Command, commandId, label ?? CommandCatalog.Label(commandId), commandId, null, false, available, reason, invoke));
    }

    /// <summary>An action that is not a catalog command.</summary>
    internal ContextMenuBuilder Item(string id, string label, Action invoke, bool available = true, string? reason = null)
    {
        ArgumentNullException.ThrowIfNull(invoke);
        return Add(new(ContextMenuEntryKind.Command, id, label, null, null, false, available, reason, invoke));
    }

    /// <summary>One value of an exclusive choice; the entries of one <paramref name="group"/> are the choices, one of them checked.</summary>
    internal ContextMenuBuilder Radio(string group, string id, string label, bool isChecked, Action invoke)
    {
        ArgumentException.ThrowIfNullOrEmpty(group);
        ArgumentNullException.ThrowIfNull(invoke);
        if (isChecked && _sections.SelectMany(section => section).Any(entry => entry.Kind == ContextMenuEntryKind.Radio && entry.Group == group && entry.Checked))
            throw new ArgumentException("Only one entry of the group \"" + group + "\" can be checked.", nameof(isChecked));
        return Add(new(ContextMenuEntryKind.Radio, id, label, null, group, isChecked, true, null, invoke));
    }

    /// <summary>A switch.</summary>
    internal ContextMenuBuilder Check(string id, string label, bool isChecked, Action invoke)
    {
        ArgumentNullException.ThrowIfNull(invoke);
        return Add(new(ContextMenuEntryKind.Check, id, label, null, null, isChecked, true, null, invoke));
    }

    /// <summary>A disabled explanatory sentence.</summary>
    internal ContextMenuBuilder Note(string text) =>
        Add(new(ContextMenuEntryKind.Note, "note:" + _sections.Sum(section => section.Count), text, null, null, false, false, null, null));

    private ContextMenuBuilder Add(ContextMenuEntry entry)
    {
        if (!_ids.Add(entry.Id)) throw new ArgumentException("The context menu already has an entry \"" + entry.Id + "\".", nameof(entry));
        _sections[^1].Add(entry);
        return this;
    }

    /// <summary>The composition so far; empty sections are dropped.</summary>
    internal ContextMenuModel ToModel() =>
        new(_sections.Where(section => section.Count > 0).Select(section => (IReadOnlyList<ContextMenuEntry>)section.ToArray()).ToArray());

    /// <summary>The Avalonia menu; null when nothing was added.</summary>
    internal ContextMenu? Build()
    {
        ContextMenu? menu = Build(ToModel());
        if (menu != null && !string.IsNullOrEmpty(_name)) AutomationProperties.SetName(menu, _name);
        return menu;
    }

    internal static ContextMenu? Build(ContextMenuModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (model.IsEmpty) return null;
        List<Control> items = [];
        foreach (IReadOnlyList<ContextMenuEntry> section in model.Sections)
        {
            if (items.Count > 0) items.Add(new Separator());
            items.AddRange(section.Select(Item));
        }
        return new ContextMenu { ItemsSource = items };
    }

    private static Control Item(ContextMenuEntry entry)
    {
        if (entry.Kind == ContextMenuEntryKind.Note) return MediaMenuBuilder.Note(entry.Label);
        Action invoke = entry.Invoke ?? (() => { });
        bool available = entry.Available;
        MenuItem item;
        if (entry.CommandId is { } commandId)
            item = CommandBindings.CreateMenuItem(commandId, invoke, () => available, () => available ? null : entry.Reason, () => entry.Label);
        else
        {
            item = new MenuItem { Header = entry.Label, Command = new DelegateCommand(invoke, () => available), IsEnabled = available };
            AutomationProperties.SetName(item, entry.Label);
            if (!available && entry.Reason != null)
            {
                AutomationProperties.SetHelpText(item, entry.Reason);
                ToolTip.SetTip(item, entry.Label + " · " + entry.Reason);
                ToolTip.SetShowOnDisabled(item, true);
            }
        }
        item.Tag = entry.Id;
        if (entry.Kind == ContextMenuEntryKind.Radio)
        {
            item.ToggleType = MenuItemToggleType.Radio;
            item.GroupName = entry.Group;
            item.IsChecked = entry.Checked;
        }
        else if (entry.Kind == ContextMenuEntryKind.Check)
        {
            item.ToggleType = MenuItemToggleType.CheckBox;
            item.IsChecked = entry.Checked;
        }
        return item;
    }
}
