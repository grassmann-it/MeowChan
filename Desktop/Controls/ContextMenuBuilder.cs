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
    private readonly List<Control> _appended = [];
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

    /// <summary>
    /// A catalog command. <paramref name="label"/> replaces the catalog label where the surface needs a more specific one.
    /// <paramref name="entryId"/> names the entry when the same command stands for several entries of one menu (the places of a list
    /// that all run the one command, each with its own label); it defaults to the command id.
    /// <paramref name="showShortcut"/> is false for an entry that is not what the command's shortcut does (an older place of a list:
    /// the shortcut opens the newest).
    /// </summary>
    internal ContextMenuBuilder Command(string commandId, Action invoke, bool available = true, string? reason = null, string? label = null, string? entryId = null, bool showShortcut = true, Func<bool>? availableWhen = null, Func<string?>? reasonWhen = null, Func<string>? labelWhen = null,
        bool dangerous = false, string? keyContext = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(commandId);
        ArgumentNullException.ThrowIfNull(invoke);
        return Add(new(ContextMenuEntryKind.Command, entryId ?? commandId, label ?? CommandCatalog.Label(commandId), commandId, null, false, available, reason, invoke, showShortcut, availableWhen, reasonWhen, labelWhen,
            Dangerous: dangerous, KeyContext: keyContext));
    }

    /// <summary>
    /// An action that is not a catalog command. <paramref name="icon"/> is a catalog mask (e.g. <c>eye-off</c>) for an entry whose siblings
    /// are commands with icons; the same action shows the same icon as its command elsewhere.
    /// </summary>
    internal ContextMenuBuilder Item(string id, string label, Action invoke, bool available = true, string? reason = null,
        Func<bool>? availableWhen = null, Func<string?>? reasonWhen = null, Func<string>? labelWhen = null, string? icon = null)
    {
        ArgumentNullException.ThrowIfNull(invoke);
        return Add(new(ContextMenuEntryKind.Command, id, label, null, null, false, available, reason, invoke, true,
            availableWhen, reasonWhen, labelWhen, Icon: icon));
    }

    /// <summary>
    /// One value of an exclusive choice; the entries of one <paramref name="group"/> are the choices, one of them checked. An entry that is
    /// <paramref name="available"/> = false stays visible with its state, disabled, and says <paramref name="reason"/> as its tooltip and help text.
    /// </summary>
    internal ContextMenuBuilder Radio(string group, string id, string label, bool isChecked, Action invoke, bool available = true,
        string? reason = null, string? icon = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(group);
        ArgumentNullException.ThrowIfNull(invoke);
        if (isChecked && _sections.SelectMany(section => section).Any(entry => entry.Kind == ContextMenuEntryKind.Radio && entry.Group == group && entry.Checked))
            throw new ArgumentException("Only one entry of the group \"" + group + "\" can be checked.", nameof(isChecked));
        return Add(new(ContextMenuEntryKind.Radio, id, label, null, group, isChecked, available, reason, invoke, true, Icon: icon));
    }

    /// <summary>A switch; a switch that is not <paramref name="available"/> stays visible with its state, disabled, and says <paramref name="reason"/>.</summary>
    internal ContextMenuBuilder Check(string id, string label, bool isChecked, Action invoke, bool available = true,
        string? reason = null, string? icon = null)
    {
        ArgumentNullException.ThrowIfNull(invoke);
        return Add(new(ContextMenuEntryKind.Check, id, label, null, null, isChecked, available, reason, invoke, true, Icon: icon));
    }

    /// <summary>A submenu whose children are composed with the same section and availability rules.</summary>
    internal ContextMenuBuilder Submenu(string id, string label, ContextMenuModel children, bool available = true, string? reason = null,
        string? icon = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentNullException.ThrowIfNull(children);
        return Add(new(ContextMenuEntryKind.Submenu, id, label, null, null, false, available, reason, null, Children: children.Sections, Icon: icon));
    }

    /// <summary>A disabled explanatory sentence.</summary>
    internal ContextMenuBuilder Note(string text) =>
        Add(new(ContextMenuEntryKind.Note, "note:" + _sections.Sum(section => section.Count), text, null, null, false, false, null, null, false));

    private ContextMenuBuilder Add(ContextMenuEntry entry)
    {
        if (!_ids.Add(entry.Id)) throw new ArgumentException("The context menu already has an entry \"" + entry.Id + "\".", nameof(entry));
        _sections[^1].Add(entry);
        return this;
    }

    /// <summary>The composition so far; empty sections are dropped.</summary>
    internal ContextMenuModel ToModel() =>
        new(_sections.Where(section => section.Count > 0).Select(section => (IReadOnlyList<ContextMenuEntry>)section.ToArray()).ToArray());

    /// <summary>
    /// Rich rows that are not entries (an explanation, a count, a submenu with its own items) after the sections. The menu is one
    /// <c>ItemsSource</c>, which makes <see cref="ContextMenu.Items"/> read-only: such rows are appended here, before <see cref="Build()"/> or
    /// <see cref="Populate"/>, never with <c>menu.Items.Add</c> afterwards.
    /// </summary>
    internal ContextMenuBuilder Append(params Control[] rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        _appended.AddRange(rows);
        return this;
    }

    /// <summary>The Avalonia menu; null when nothing was added.</summary>
    internal ContextMenu? Build()
    {
        IReadOnlyList<Control> items = ComposeItems();
        if (items.Count == 0) return null;
        ContextMenu menu = new() { ItemsSource = items };
        if (!string.IsNullOrEmpty(_name)) AutomationProperties.SetName(menu, _name);
        return menu;
    }

    /// <summary>
    /// Replaces the items of an existing popup while retaining its opening/closing handlers. It first empties the popup the right way
    /// (<see cref="Clear"/>): an <c>ItemsSource</c> is refused while the popup still holds items of its own (static XAML children),
    /// and <c>Items.Clear()</c> is refused while an <c>ItemsSource</c> is set.
    /// </summary>
    internal bool Populate(ContextMenu menu)
    {
        ArgumentNullException.ThrowIfNull(menu);
        IReadOnlyList<Control> items = ComposeItems();
        Clear(menu);
        if (items.Count == 0) return false;
        menu.ItemsSource = items;
        if (!string.IsNullOrEmpty(_name)) AutomationProperties.SetName(menu, _name);
        return menu.ItemCount > 0;
    }

    /// <summary>
    /// Empties a popup that <see cref="Populate"/> fills: drops the <c>ItemsSource</c> it set and any item the popup holds itself.
    /// Use this, not <c>menu.Items.Clear()</c>, which throws while an <c>ItemsSource</c> is in use.
    /// </summary>
    internal static void Clear(ContextMenu menu)
    {
        ArgumentNullException.ThrowIfNull(menu);
        if (menu.ItemsSource != null) menu.ItemsSource = null;
        if (menu.ItemCount > 0) menu.Items.Clear();
    }

    private IReadOnlyList<Control> ComposeItems()
    {
        List<Control> items = [.. BuildItems(ToModel())];
        items.AddRange(_appended);
        return items;
    }

    /// <summary>Creates the persistent empty popup used by controls whose entries are frozen at opening time.</summary>
    internal static ContextMenu Empty() => new();

    internal static ContextMenu? Build(ContextMenuModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (model.IsEmpty) return null;
        return new ContextMenu { ItemsSource = BuildItems(model) };
    }

    /// <summary>The items of <paramref name="model"/> with separators between its sections, for a submenu that shows the same entries as a context menu.</summary>
    internal static IReadOnlyList<Control> BuildItems(ContextMenuModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return Items(model.Sections);
    }

    private static List<Control> Items(IReadOnlyList<IReadOnlyList<ContextMenuEntry>> sections)
    {
        List<Control> items = [];
        foreach (IReadOnlyList<ContextMenuEntry> section in sections)
        {
            if (items.Count > 0) items.Add(new Separator());
            items.AddRange(section.Select(Item));
        }
        return items;
    }

    private static Control Item(ContextMenuEntry entry)
    {
        if (entry.Kind == ContextMenuEntryKind.Note) return MediaMenuBuilder.Note(entry.Label);
        if (entry.Kind == ContextMenuEntryKind.Submenu)
        {
            MenuItem submenu = new() { Header = entry.Label, IsEnabled = entry.Available, ItemsSource = Items(entry.Children ?? Array.Empty<IReadOnlyList<ContextMenuEntry>>()) };
            AutomationProperties.SetName(submenu, entry.Label);
            if (!entry.Available && entry.Reason is { } unavailable)
            {
                ToolTip.SetShowOnDisabled(submenu, true);
                ToolTip.SetTip(submenu, entry.Label + " · " + unavailable);
                AutomationProperties.SetHelpText(submenu, unavailable);
            }
            if (entry.Icon != null) submenu.Icon = CommandBindings.IconFromKey(entry.Icon);
            submenu.Tag = entry.Id;
            return submenu;
        }
        Action invoke = entry.Invoke ?? (() => { });
        bool Available() => entry.Availability?.Invoke() ?? entry.Available;
        string? Reason() => Available() ? null : entry.ReasonFactory?.Invoke() ?? entry.Reason;
        string Label() => entry.LabelFactory?.Invoke() ?? entry.Label;
        MenuItem item;
        if (entry.CommandId is { } commandId)
            item = CommandBindings.CreateMenuItem(commandId, invoke, Available, Reason, Label, entry.ShowShortcut, entry.KeyContext);
        else
        {
            item = new MenuItem { Header = Label(), Command = new DelegateCommand(invoke, Available), IsEnabled = Available() };
            if (entry.Icon != null) item.Icon = CommandBindings.IconFromKey(entry.Icon);
            AutomationProperties.SetName(item, Label());
            if (!Available() && Reason() != null)
            {
                AutomationProperties.SetHelpText(item, Reason());
                ToolTip.SetTip(item, Label() + " · " + Reason());
                ToolTip.SetShowOnDisabled(item, true);
            }
        }
        item.Tag = entry.Id;
        if (entry.Dangerous) item.Classes.Add("danger");
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
