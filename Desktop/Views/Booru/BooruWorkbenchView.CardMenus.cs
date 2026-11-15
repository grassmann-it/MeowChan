using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;

namespace MeowChan.Desktop.Booru;

// Interaction audit 1.1.0 (V8): a result card and a watcher card have a context menu. A right-click, the Menu key, Shift+F10 and the rebindable
// "Open context menu" open the card's own actions: load the original (or cancel / retry it), show it in Media, open or copy the post link; for a
// watcher check now, pause or resume, restart from now, the same links for its latest post and, last, remove. The menu holds no logic of its own:
// every entry presses the card's button of the same action, so it does exactly that, is available exactly when that button is and gives the same
// reason when it is not. The entries are composed when the menu opens, from the buttons the card has then. Icons are those of the matching
// commands (the media menu's post links, "Check now", "Show in Media", "Remove from watch list"). A result card is a keyboard stop of its own
// (BooruWorkbenchView.Hits.cs), so the keys also work on the focused card; a watcher card's keys work from the buttons inside it.
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

    // The icons of entries that are no catalog commands: the same masks as the command or the button that does the same thing.
    private const string LoadIcon = "download-cloud", CancelIcon = "x", RetryIcon = "refresh-cw", PauseIcon = "pause", ResumeIcon = "play", RestartIcon = "rotate-ccw";

    /// <summary>Gives a card its context menu; <paramref name="compose"/> is called at every opening, so the entries are current. <paramref name="fallbackFocus"/>
    /// takes the focus when an entry rebuilt the cards (the hit's own card again).</summary>
    private static void AttachCardMenu(Border card, Func<ContextMenuBuilder?> compose, Action? fallbackFocus = null) =>
        _ = new ContextMenuHost(card, compose, fallbackFocus);

    private ContextMenuBuilder? ComposeResultMenu(Border card, string postId)
    {
        if (_closed) return null;
        ContextMenuBuilder menu = new ContextMenuBuilder().Named(Texts.T("booru.ui.post.title", postId));
        Button? load = ButtonOf(card, LoadPrefix + postId), cancel = ButtonOf(card, CancelLoadPrefix + postId), retry = ButtonOf(card, RetryLoadPrefix + postId);
        // The primary action of the card is the one button it shows: load the original, cancel the running load, or try again.
        if (cancel != null) AddButtonItem(menu, cancel, MenuLoad, CancelIcon);
        else if (retry != null) AddButtonItem(menu, retry, MenuLoad, RetryIcon);
        else if (load != null) AddButtonItem(menu, load, MenuLoad, LoadIcon);
        AddShowInMedia(menu, ButtonOf(card, ShowInMediaPrefix + postId));
        menu.Section();
        AddLinkItems(menu, ButtonOf(card, OpenPostPrefix + postId), ButtonOf(card, CopyLinkPrefix + postId));
        return menu;
    }

    private ContextMenuBuilder? ComposeWatcherMenu(Border card, string watcherId, string query)
    {
        if (_closed) return null;
        ContextMenuBuilder menu = new ContextMenuBuilder().Named(query);
        AddButtonItem(menu, ButtonOf(card, CheckPrefix + watcherId), MenuCheck, CommandCatalog.Get("watch.check").Icon);
        if (ButtonOf(card, ResumePrefix + watcherId) is { } resume) AddButtonItem(menu, resume, MenuResume, ResumeIcon);
        else AddButtonItem(menu, ButtonOf(card, PausePrefix + watcherId), MenuPause, PauseIcon);
        AddButtonItem(menu, ButtonOf(card, RestartPrefix + watcherId), MenuRestart, RestartIcon);
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
