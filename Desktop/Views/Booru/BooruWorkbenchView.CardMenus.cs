using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;

namespace MeowChan.Desktop.Booru;

internal sealed partial class BooruWorkbenchView
{
    internal const string MenuLoad = "booru.menu.load";
    internal const string MenuShowInMedia = "booru.menu.show-in-media";
    internal const string MenuOpenPost = "booru.menu.open-post";
    internal const string MenuCopyLink = "booru.menu.copy-link";
    internal const string MenuCheck = "booru.menu.check";
    internal const string MenuPause = "booru.menu.pause";
    internal const string MenuResume = "booru.menu.resume";
    internal const string MenuRestart = "booru.menu.restart";
    internal const string MenuRemove = "booru.menu.remove";
    // The menu of a source-tag chip (the "copy name" entry is the catalog command tags.copy-name).
    internal const string MenuTagAdd = "booru.menu.tag-add";
    internal const string MenuTagExclude = "booru.menu.tag-exclude";

    // The icons of entries that are no catalog commands: the same masks as the command or the button that does the same thing. They live in a
    // nested class: the view's own string constants are its selectors, each with a compile-time alias on BooruWorkbenchWindow.
    private static class CardMenuIcons
    {
        internal const string Load = "download-cloud", Cancel = "x", Retry = "refresh-cw", Pause = "pause", Resume = "play", Restart = "rotate-ccw";
    }

    /// <summary>Gives a card its context menu; <paramref name="compose"/> is called at every opening, so the entries are current. <paramref name="fallbackFocus"/>
    /// takes the focus when an entry rebuilt the cards (the hit's own card again).</summary>
    private static void AttachCardMenu(Border card, Func<ContextMenuBuilder?> compose, Action? fallbackFocus = null) =>
        _ = new ContextMenuHost(card, compose, fallbackFocus);

    private static ContextMenuBuilder? ComposeResultMenu(ResultActions result)
    {
        if (!result.IsCurrent() || result.CurrentRow() is not { } row) return null;
        ContextMenuBuilder menu = new ContextMenuBuilder().Named(Texts.T("booru.ui.post.title", result.PostId));
        ResultCommand[] commands = result.Commands(row).ToArray();
        foreach (ResultCommand command in commands.Where(command => command.Action is ResultAction.Load or ResultAction.Cancel or ResultAction.Retry))
        {
            string id = command.Action == ResultAction.Cancel && row.CanRetry ? "booru.menu.cancel-original" : MenuLoad;
            AddResultItem(menu, result, command, id);
        }
        if (commands.Any(command => command.Action == ResultAction.ShowInMedia))
            menu.Command("thread.show-in-media", () => result.Run(ResultAction.ShowInMedia), entryId: MenuShowInMedia,
                showShortcut: false, availableWhen: () => result.IsCurrent() && result.CanRun(ResultAction.ShowInMedia));
        menu.Section();
        foreach (ResultCommand link in commands.Where(command => command.Action is ResultAction.Open or ResultAction.Copy))
            AddResultItem(menu, result, link, link.Action == ResultAction.Open ? MenuOpenPost : MenuCopyLink);
        return menu;
    }

    private static void AddResultItem(ContextMenuBuilder menu, ResultActions result, ResultCommand command, string id) =>
        menu.Item(id, command.Label, () => result.Run(command.Action), reason: command.Reason,
            availableWhen: () => result.IsCurrent() && result.CanRun(command.Action), icon: command.Icon);

    private ContextMenuBuilder? ComposeWatcherMenu(Border card, string watcherId, string query)
    {
        if (_closed) return null;
        ContextMenuBuilder menu = new ContextMenuBuilder().Named(query);
        AddButtonItem(menu, ButtonOf(card, CheckPrefix + watcherId), MenuCheck, CommandCatalog.Get("watch.check").Icon);
        if (ButtonOf(card, ResumePrefix + watcherId) is { } resume) AddButtonItem(menu, resume, MenuResume, CardMenuIcons.Resume);
        else AddButtonItem(menu, ButtonOf(card, PausePrefix + watcherId), MenuPause, CardMenuIcons.Pause);
        AddButtonItem(menu, ButtonOf(card, RestartPrefix + watcherId), MenuRestart, CardMenuIcons.Restart);
        menu.Section();
        AddShowInMedia(menu, ButtonOf(card, WatcherShowInMediaPrefix + watcherId));
        AddLinkItems(menu, ButtonOf(card, WatcherOpenPrefix + watcherId), ButtonOf(card, WatcherCopyPrefix + watcherId));
        // Removing a watcher is the last entry and asks inline first, like its button; it never touches local media.
        menu.Section();
        AddButtonItem(menu, ButtonOf(card, RemovePrefix + watcherId), MenuRemove, CommandCatalog.Get("watch.remove").Icon);
        return menu;
    }

    private static Button? ButtonOf(Control card, string name) =>
        card.GetVisualDescendants().OfType<Button>().FirstOrDefault(button => button.Name == name);

    // The entry for a button the card shows; nothing for a button it does not have (a watcher that cannot restart, a post without a saved copy).
    private static void AddButtonItem(ContextMenuBuilder menu, Button? button, string id, string icon)
    {
        if (button == null) return;
        bool available = button.IsEffectivelyEnabled;
        menu.Item(id, button.Content as string ?? AutomationName(button), () => Press(button), available, available ? null : ReasonOf(button), icon: icon);
    }

    // "Show in Media" is the catalog command's label and icon; the key of the command acts on another page, so none is shown.
    private static void AddShowInMedia(ContextMenuBuilder menu, Button? button)
    {
        if (button == null) return;
        bool available = button.IsEffectivelyEnabled;
        menu.Command("thread.show-in-media", () => Press(button), available, available ? null : ReasonOf(button), entryId: MenuShowInMedia, showShortcut: false);
    }

    // The words and icons of the media menu's provider post entries.
    private static void AddLinkItems(ContextMenuBuilder menu, Button? open, Button? copy)
    {
        if (open != null)
            menu.Item(MenuOpenPost, Texts.T("booru.media.openPost"), () => Press(open), open.IsEffectivelyEnabled,
                open.IsEffectivelyEnabled ? null : ReasonOf(open), icon: CommandCatalog.Get("post.open-source").Icon);
        if (copy != null)
            menu.Item(MenuCopyLink, Texts.T("booru.media.copyPost"), () => Press(copy), copy.IsEffectivelyEnabled,
                copy.IsEffectivelyEnabled ? null : ReasonOf(copy), icon: CommandCatalog.Get("post.copy-link").Icon);
    }

    private static string AutomationName(Button button) => Avalonia.Automation.AutomationProperties.GetName(button) ?? button.Name ?? string.Empty;

    // A disabled button says why in its tooltip; the entry says the same.
    private static string? ReasonOf(Button button) => ToolTip.GetTip(button) as string;

    private static void Press(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
}
