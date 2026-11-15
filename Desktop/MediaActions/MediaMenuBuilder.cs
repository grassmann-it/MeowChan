using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;

namespace MeowChan.Desktop.MediaActions;

/// <summary>
/// Turns a composed media menu into the Avalonia menu every surface shows. The surface only supplies what each action
/// does (frozen to the target) and its own context-specific items; the common section is built here, in one place.
/// </summary>
internal static class MediaMenuBuilder
{
    /// <param name="session">The frozen target and its guard; every command refuses once it is no longer current.</param>
    /// <param name="model">The composed common actions.</param>
    /// <param name="handlers">What each available action does; always for the frozen target, never for "the selection".</param>
    /// <param name="run">Runs a handler with the surface's own error reporting.</param>
    /// <param name="contextItems">Context-specific items, shown after a separator below the common section.</param>
    /// <param name="explain">Shows the summary of unavailable actions; null keeps the summary item out of the menu.</param>
    /// <param name="leadingItems">A group on top of the common section (the playback group of a video in the reader), followed by a separator.</param>
    /// <param name="rate">Writes a rating (1 to 5, null removes it) for the frozen target; the "Rating" submenu is available only with it.</param>
    internal static ContextMenu Build(MediaMenuSession session, MediaMenuModel model,
        IReadOnlyDictionary<MediaMenuAction, Func<Task>> handlers, Func<Func<Task>, Task> run,
        IReadOnlyList<Control>? contextItems = null, Action<string>? explain = null, IReadOnlyList<Control>? leadingItems = null,
        Func<int?, Task>? rate = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(handlers);
        ArgumentNullException.ThrowIfNull(run);
        List<Control> items = new();
        if (leadingItems is { Count: > 0 })
        {
            items.AddRange(leadingItems);
            items.Add(new Separator());
        }
        int group = -1;
        foreach (MediaMenuItemModel entry in model.Items)
        {
            if (group >= 0 && entry.Group != group) items.Add(new Separator());
            group = entry.Group;
            items.Add(entry.Children != null ? RatingSubmenu(session, entry, rate, run) : Item(session, entry, handlers, run));
        }
        if (contextItems is { Count: > 0 })
        {
            if (items.Count > 0) items.Add(new Separator());
            items.AddRange(contextItems);
        }
        string[] unavailable = model.Unavailable.ToArray();
        if (unavailable.Length > 0 && explain != null)
        {
            string explanation = string.Join(Environment.NewLine, unavailable);
            MenuItem info = new() { Header = Texts.T("archive.command.unavailable"), Icon = CommandBindings.Icon("thread.details"), Tag = "media.menu.unavailable" };
            AutomationProperties.SetName(info, info.Header.ToString());
            AutomationProperties.SetHelpText(info, explanation);
            ToolTip.SetTip(info, explanation);
            info.Click += (_, _) => { if (session.IsCurrent) explain(explanation); };
            items.Add(info);
        }
        ContextMenu menu = new() { ItemsSource = items };
        session.Invalidated += () =>
        {
            foreach (MenuItem item in items.OfType<MenuItem>()) item.IsEnabled = false;
            menu.Close();
        };
        return menu;
    }

    /// <summary>
    /// The standalone rating menu (R on the media page and in the archive, and in the gallery when its panel cannot take the focus): the same
    /// five stars and "Remove rating" as the "Rating" submenu, as a menu of their own at the focused tile or row.
    /// </summary>
    internal static ContextMenu BuildRatingMenu(MediaMenuSession session, int? rating, bool available, string? reason,
        Func<int?, Task> rate, Func<Func<Task>, Task> run)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(rate);
        ArgumentNullException.ThrowIfNull(run);
        List<Control> items = RatingChildren(session, MediaMenuComposition.RatingItems(rating, available, reason), rate, run);
        ContextMenu menu = new() { ItemsSource = items };
        session.Invalidated += () =>
        {
            foreach (MenuItem item in items.OfType<MenuItem>()) item.IsEnabled = false;
            menu.Close();
        };
        return menu;
    }

    // The "Rating" entry is a submenu with the entry's own id, label, icon and key (R); it does nothing by itself.
    private static MenuItem RatingSubmenu(MediaMenuSession session, MediaMenuItemModel entry, Func<int?, Task>? rate, Func<Func<Task>, Task> run)
    {
        bool executable = entry.Available && rate != null;
        MenuItem item = CommandBindings.CreateMenuItem(entry.CommandId!, () => { }, () => executable,
            () => executable ? null : entry.Reason ?? MediaMenuReasons.Unavailable, () => entry.Label);
        item.Command = null;
        item.Tag = entry.Id;
        item.IsEnabled = executable && session.IsCurrent;
        ToolTip.SetShowOnDisabled(item, true);
        item.ItemsSource = rate == null ? Array.Empty<Control>() : RatingChildren(session, entry.Children!, rate, run);
        session.Invalidated += () => item.IsEnabled = false;
        return item;
    }

    private static List<Control> RatingChildren(MediaMenuSession session, IReadOnlyList<MediaMenuItemModel> children, Func<int?, Task> rate,
        Func<Func<Task>, Task> run)
    {
        List<Control> items = new();
        foreach (MediaMenuItemModel child in children)
        {
            // The stars are a group of radio items; the removal stands apart below them.
            if (child.CommandId == "media.rating.clear") items.Add(new Separator());
            int? value = child.RatingValue;
            bool executable = child.Available;
            void Execute()
            {
                if (session.IsCurrent) _ = run(() => rate(value));
            }
            bool CanExecute() => executable && session.IsCurrent;
            MenuItem item = CommandBindings.CreateMenuItem(child.CommandId!, Execute, CanExecute,
                () => executable ? null : child.Reason ?? MediaMenuReasons.Unavailable, () => child.Label);
            item.Tag = child.Id;
            item.Click += (_, _) => RetireIfStale(session);
            ToolTip.SetShowOnDisabled(item, true);
            if (child.Checked is { } isChecked)
            {
                item.ToggleType = MenuItemToggleType.Radio;
                item.GroupName = "media.rating";
                item.IsChecked = isChecked;
            }
            items.Add(item);
        }
        return items;
    }

    private static MenuItem Item(MediaMenuSession session, MediaMenuItemModel entry,
        IReadOnlyDictionary<MediaMenuAction, Func<Task>> handlers, Func<Func<Task>, Task> run)
    {
        bool executable = entry.Available && handlers.ContainsKey(entry.Action);
        void Execute()
        {
            if (!session.IsCurrent || !handlers.TryGetValue(entry.Action, out Func<Task>? handler)) return;
            _ = run(handler);
        }
        bool CanExecute() => executable && session.IsCurrent;
        MenuItem item;
        if (entry.CommandId is { } id)
            item = CommandBindings.CreateMenuItem(id, Execute, CanExecute, () => executable ? null : entry.Reason ?? MediaMenuReasons.Unavailable,
                () => entry.Label);
        else
        {
            item = new MenuItem { Header = entry.Label, Command = new DelegateCommand(Execute, CanExecute), IsEnabled = CanExecute() };
            if (entry.Icon != null) item.Icon = CommandBindings.IconFromKey(entry.Icon);
            AutomationProperties.SetName(item, entry.Label);
            if (!executable) AutomationProperties.SetHelpText(item, entry.Reason ?? MediaMenuReasons.Unavailable);
            ToolTip.SetTip(item, executable ? entry.Label : entry.Label + " · " + (entry.Reason ?? MediaMenuReasons.Unavailable));
        }
        item.Tag = entry.Id;
        item.Click += (_, _) => RetireIfStale(session);
        // A disabled item must still say why, so its tooltip is shown on the disabled control as well.
        ToolTip.SetShowOnDisabled(item, true);
        if (entry.Checked is { } isChecked)
        {
            item.ToggleType = MenuItemToggleType.CheckBox;
            item.IsChecked = isChecked;
        }
        return item;
    }

    // A click on an item of a menu whose target is gone retires the whole menu instead of leaving live-looking items.
    private static void RetireIfStale(MediaMenuSession session)
    {
        if (!session.IsCurrent) session.Invalidate();
    }

    /// <summary>
    /// A context-specific item that runs only while the session is current. <paramref name="icon"/> is a catalog mask: the items below the
    /// common section keep its icon column, and an item that does what a command does takes that command's icon.
    /// </summary>
    internal static MenuItem ContextItem(MediaMenuSession session, string textKey, Func<Task> action, Func<bool> available,
        Func<Func<Task>, Task> run, string? reason = null, string? icon = null)
    {
        void Execute()
        {
            if (session.IsCurrent) _ = run(action);
        }
        bool CanExecute() => available() && session.IsCurrent;
        MenuItem item = new() { Header = Texts.T(textKey), Tag = textKey, Command = new DelegateCommand(Execute, CanExecute) };
        if (icon != null) item.Icon = CommandBindings.IconFromKey(icon);
        item.IsEnabled = CanExecute();
        item.Click += (_, _) => RetireIfStale(session);
        AutomationProperties.SetName(item, item.Header?.ToString() ?? string.Empty);
        if (!item.IsEnabled && reason != null)
        {
            ToolTip.SetTip(item, reason);
            ToolTip.SetShowOnDisabled(item, true);
            AutomationProperties.SetHelpText(item, reason);
        }
        return item;
    }

    /// <summary>A context-specific item with a dynamic label, used for named queue groups.</summary>
    internal static MenuItem ContextItem(MediaMenuSession session, string id, string label, Func<Task> action, Func<bool> available,
        Func<Func<Task>, Task> run, string? reason = null, string? icon = null)
    {
        void Execute()
        {
            if (session.IsCurrent) _ = run(action);
        }
        bool CanExecute() => available() && session.IsCurrent;
        MenuItem item = new() { Header = label, Tag = id, Command = new DelegateCommand(Execute, CanExecute) };
        if (icon != null) item.Icon = CommandBindings.IconFromKey(icon);
        item.IsEnabled = CanExecute();
        item.Click += (_, _) => RetireIfStale(session);
        AutomationProperties.SetName(item, label);
        if (!item.IsEnabled && reason != null)
        {
            ToolTip.SetTip(item, reason);
            ToolTip.SetShowOnDisabled(item, true);
            AutomationProperties.SetHelpText(item, reason);
        }
        return item;
    }

    /// <summary>Builds a context-specific submenu while retaining the media menu's stale-target guard.</summary>
    internal static MenuItem Submenu(MediaMenuSession session, string id, string label, IReadOnlyList<Control> children,
        Func<bool>? available = null, string? reason = null, string? icon = null)
    {
        ArgumentNullException.ThrowIfNull(children);
        bool enabled = available == null || available();
        MenuItem item = new() { Header = label, Tag = id, IsEnabled = enabled, ItemsSource = children };
        if (icon != null) item.Icon = CommandBindings.IconFromKey(icon);
        AutomationProperties.SetName(item, label);
        if (!enabled && reason != null)
        {
            ToolTip.SetTip(item, reason);
            ToolTip.SetShowOnDisabled(item, true);
            AutomationProperties.SetHelpText(item, reason);
        }
        session.Invalidated += () => item.IsEnabled = false;
        return item;
    }

    /// <summary>A context-specific item that is a catalog command (its icon and shortcut show), with its own visible label.</summary>
    internal static MenuItem ContextCommandItem(MediaMenuSession session, string commandId, Func<Task> action, Func<bool> available,
        Func<Func<Task>, Task> run, string? unavailableReason, string label)
    {
        void Execute()
        {
            if (session.IsCurrent) _ = run(action);
        }
        bool CanExecute() => available() && session.IsCurrent;
        MenuItem item = CommandBindings.CreateMenuItem(commandId, Execute, CanExecute, () => CanExecute() ? null : unavailableReason, () => label);
        item.Tag = commandId;
        item.Click += (_, _) => RetireIfStale(session);
        ToolTip.SetShowOnDisabled(item, true);
        return item;
    }

    /// <summary>
    /// The playback group on top of a video's media menu (reader and gallery): one catalog-command item per entry, each running its action
    /// only while the session is current. Null when there are no entries.
    /// </summary>
    internal static List<Control>? PlaybackGroup(MediaMenuSession session, IReadOnlyList<ReaderPlaybackMenu.Entry> entries,
        Func<ReaderVideoAction, Task> execute, Func<Func<Task>, Task> run)
    {
        if (entries.Count == 0) return null;
        List<Control> items = new();
        foreach (ReaderPlaybackMenu.Entry entry in entries)
        {
            ReaderVideoAction action = entry.Action;
            items.Add(ContextCommandItem(session, entry.CommandId, () => execute(action), () => entry.Available, run,
                MediaMenuReasons.Unavailable, entry.Label));
        }
        return items;
    }

    /// <summary>The post bookmark entry ("Add / edit post bookmark…") of the media menu, with the catalog command's icon and shortcut.</summary>
    internal static MenuItem PostBookmarkItem(MediaMenuSession session, Func<Task> action, Func<bool> available, Func<Func<Task>, Task> run)
        => ContextCommandItem(session, ReaderPlaybackMenu.GalleryExtras.PostBookmarkCommandId, action, available, run,
            MediaMenuReasons.Unavailable, CommandCatalog.Label(ReaderPlaybackMenu.GalleryExtras.PostBookmarkCommandId));

    /// <summary>A disabled note inside the menu (for example which items the single-target actions apply to).</summary>
    internal static MenuItem Note(string text)
    {
        // The note is a sentence: it wraps inside the menu instead of being cut at the menu's edge.
        MenuItem note = new() { Header = new TextBlock { Text = text, TextWrapping = Avalonia.Media.TextWrapping.Wrap, MaxWidth = 320 }, IsEnabled = false, Tag = "media.menu.note" };
        AutomationProperties.SetName(note, text);
        ToolTip.SetTip(note, text);
        ToolTip.SetShowOnDisabled(note, true);
        return note;
    }
}

/// <summary>
/// Owns the one currently open media menu of a surface: opens it on the exact origin control, replaces or retires a
/// previous menu, and returns the keyboard focus to where it was when the menu closes.
/// </summary>
internal sealed class MediaMenuPresenter
{
    private Control? _owner;
    private MediaMenuSession? _session;

    internal ContextMenu? Menu { get; private set; }
    internal MediaMenuSession? Session => _session;

    /// <summary>Opens <paramref name="menu"/> on <paramref name="origin"/> for <paramref name="session"/>.</summary>
    /// <param name="attachToOrigin">Also sets the origin's ContextMenu for the lifetime of the open menu.</param>
    /// <param name="fallbackFocus">Where focus goes when neither the previous focus nor the origin can take it any more.</param>
    internal void Show(ContextMenu menu, MediaMenuSession session, Control origin, bool attachToOrigin = false, Action? fallbackFocus = null)
    {
        Retire();
        Menu = menu;
        _session = session;
        _owner = origin;
        object? previousFocus = TopLevel.GetTopLevel(origin)?.FocusManager?.GetFocusedElement();
        if (attachToOrigin) origin.ContextMenu = menu;
        menu.Closed += (_, _) =>
        {
            if (ReferenceEquals(Menu, menu))
            {
                if (attachToOrigin && ReferenceEquals(origin.ContextMenu, menu)) origin.ContextMenu = null;
                _owner = null;
            }
            RestoreFocus(menu, session, origin, previousFocus as Control, fallbackFocus);
        };
        menu.Open(origin);
    }

    /// <summary>Invalidates the open menu (its items refuse and the popup closes) and forgets it.</summary>
    internal void Invalidate()
    {
        MediaMenuSession? session = _session;
        Retire();
        session?.Invalidate();
    }

    private void Retire()
    {
        ContextMenu? previous = Menu;
        Control? owner = _owner;
        if (previous == null) return;
        previous.Close();
        if (owner != null && ReferenceEquals(owner.ContextMenu, previous)) owner.ContextMenu = null;
        _owner = null;
    }

    private void RestoreFocus(ContextMenu menu, MediaMenuSession session, Control origin, Control? previous, Action? fallback)
    {
        Dispatcher.UIThread.Post(() =>
        {
            // A newer menu, an invalidated target or a dialog opened by the action owns the focus now.
            if (!ReferenceEquals(Menu, menu) || menu.IsOpen || !session.IsCurrent) return;
            TopLevel? top = TopLevel.GetTopLevel(origin);
            // The shortcuts window is modeless help, not a dialog that took the focus.
            if (top is Window owner && owner.OwnedWindows.Any(window => window.IsVisible && !Views.Shortcuts.ShortcutsWindow.IsPassive(window))) return;
            object? focused = top?.FocusManager?.GetFocusedElement();
            if (focused is Control control && !ReferenceEquals(control, menu) && control.GetSelfAndVisualAncestors().OfType<ContextMenu>().Any()) return;
            static bool Usable(Control? control) => control != null && control.IsAttachedToVisualTree() && control.IsEffectivelyVisible && control.IsEffectivelyEnabled;
            if (Usable(previous)) previous!.Focus();
            else if (Usable(origin)) origin.Focus();
            else fallback?.Invoke();
        }, DispatcherPriority.Background);
    }

    /// <summary>
    /// A source that has its own native menu (text input, combo box, slider, selectable text): the media menu must not
    /// replace it.
    /// </summary>
    internal static bool IsNativeTextSource(object? source) => source is Visual visual
        && visual.GetSelfAndVisualAncestors().Any(control => control is TextBox or ComboBox or Slider or SelectableTextBlock
            or Ursa.Controls.AutoCompleteBox);

    /// <summary>The keyboard gestures that open a context menu: the Menu key, Shift+F10 and the configurable context-open command.</summary>
    internal static bool IsContextKey(KeyEventArgs e) => !e.Handled && !CommandRouter.IsNativeInput(e.Source, e.Key)
        && (e.Key == Key.Apps && e.KeyModifiers == KeyModifiers.None
            || e.Key == Key.F10 && e.KeyModifiers == KeyModifiers.Shift
            || CommandBindings.Matches(e, "menu.context-open"));
}
