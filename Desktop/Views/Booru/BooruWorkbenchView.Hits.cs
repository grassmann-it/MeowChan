using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;

namespace MeowChan.Desktop.Booru;

/// <summary>What a key on a focused Booru hit asks for.</summary>
internal enum BooruHitKey { None, Activate, Previous, Next, First, Last }

/// <summary>
/// The key routing of the Booru hit cards (interaction audit V11), without any control state so it can be checked on its own.
/// Enter is the card's primary action wherever the focus is not on a part with its own meaning (a button, selectable text);
/// the arrow keys and Home/End move between hits only while the card itself holds the focus. The context keys (Menu, Shift+F10 and the
/// rebindable command) are not routed here: <see cref="ContextMenuHost"/> owns them.
/// </summary>
internal static class BooruHitKeys
{
    internal static BooruHitKey Route(KeyEventArgs e, Avalonia.Visual card, bool shortcutCapture = false)
    {
        if (e.Handled || shortcutCapture || e.KeyModifiers != KeyModifiers.None) return BooruHitKey.None;
        if (ItemActivation.IsPrimaryKey(e))
            return ItemActivation.IsOwnedByInteractivePart(e.Source, card) ? BooruHitKey.None : BooruHitKey.Activate;
        if (!ReferenceEquals(e.Source, card)) return BooruHitKey.None;
        return e.Key switch
        {
            Key.Up => BooruHitKey.Previous,
            Key.Down => BooruHitKey.Next,
            Key.Home => BooruHitKey.First,
            Key.End => BooruHitKey.Last,
            _ => BooruHitKey.None
        };
    }

    /// <summary>The index of the hit that <paramref name="key"/> moves to (without wrapping); -1 when there is none.</summary>
    internal static int Target(BooruHitKey key, int index, int count)
    {
        if (count <= 0 || index < 0 || index >= count) return -1;
        return key switch
        {
            BooruHitKey.Previous => Math.Max(0, index - 1),
            BooruHitKey.Next => Math.Min(count - 1, index + 1),
            BooruHitKey.First => 0,
            BooruHitKey.Last => count - 1,
            _ => -1
        };
    }

    /// <summary>
    /// The main action of a hit (decision 95): "Show in Media" once the original is saved locally and that button is enabled. Without
    /// a local copy nothing happens: Enter and a double click never start a download.
    /// </summary>
    internal static Button? Primary(Button? showInMedia) => showInMedia is { IsEffectivelyEnabled: true } ? showInMedia : null;
}

/// <summary>Keyboard, focus and menu of the result cards, and the double click of the watcher cards.</summary>
internal sealed partial class BooruWorkbenchView
{
    private void InitializeHitList()
    {
        AutomationProperties.SetControlTypeOverride(_results, AutomationControlType.List);
        AutomationProperties.SetControlTypeOverride(_watchers, AutomationControlType.List);
    }

    /// <summary>Numbers the hits for assistive technology ("3 of 20") once the whole page of cards exists.</summary>
    private static List<Control> NumberHits(List<Control> cards)
    {
        for (int index = 0; index < cards.Count; index++)
        {
            AutomationProperties.SetPositionInSet(cards[index], index + 1);
            AutomationProperties.SetSizeOfSet(cards[index], cards.Count);
        }
        return cards;
    }

    /// <summary>
    /// Makes a hit card reachable by keyboard: a tab stop with the shared focus ring and a name (post, provider rating, local copy
    /// yes or no), Enter and the double click run the primary action, and the arrow keys move between hits. The context menu is the card's
    /// one menu of BooruWorkbenchView.CardMenus.cs (AttachCardMenu): the card's ContextMenuHost opens it on a right click, the Menu key,
    /// Shift+F10 and the context-open command, here from the focused card as well.
    /// </summary>
    private void ConfigureHitCard(Border card, BooruResultRowView row, Button? showInMedia)
    {
        string postId = row.Post.PostId;
        card.Classes.Add("workspace-card-focusable");
        card.Focusable = true;
        card.IsTabStop = true;
        AutomationProperties.SetControlTypeOverride(card, AutomationControlType.ListItem);
        AutomationProperties.SetName(card, Texts.T("booru.ui.result.hitName", postId, BooruText.Rating(row.Post.Rating),
            Texts.T(showInMedia != null ? "booru.ui.result.localYes" : "booru.ui.result.localNo")));
        // Saved originals keep their Show in Media route. Unsaved hits open the preview-only
        // Booru viewer instead; neither Enter nor double-click implicitly downloads an original.
        card.DoubleTapped += (_, e) =>
        {
            if (_closed || !ItemActivation.IsCardDoubleClick(e, card)) return;
            if (BooruHitKeys.Primary(showInMedia) is { } primary) { e.Handled = true; Click(primary); }
            else e.Handled = OpenResultViewer(postId);
        };
        ConfigureMiddleClick(card, postId);
        card.KeyDown += (_, e) =>
        {
            if (_closed) return;
            BooruHitKey key = BooruHitKeys.Route(e, card, ShortcutServiceCapturing());
            switch (key)
            {
                case BooruHitKey.Activate:
                    if (BooruHitKeys.Primary(showInMedia) is { } primary) { e.Handled = true; Click(primary); }
                    else e.Handled = OpenResultViewer(postId);
                    break;
                case BooruHitKey.None:
                    break;
                default:
                    e.Handled = true;
                    MoveHitFocus(_results, card, key);
                    break;
            }
        };
    }

    /// <summary>
    /// Dead-affordance review 15.11.: a middle click on a hit card (not on a button, a tag or selectable text; press and release on the same card)
    /// opens the post page in the browser, like the middle click on a thread row opens its source: the card's "Open post" button, with its rules.
    /// Nothing is loaded or downloaded.
    /// </summary>
    private void ConfigureMiddleClick(Border card, string postId)
    {
        MiddleClickTracker tracker = new();
        card.AddHandler(InputElement.PointerPressedEvent, (object? _, PointerPressedEventArgs e) =>
        {
            if (!MiddleClickTracker.IsPress(e, card)) { tracker.Cancel(); return; }
            tracker.Pressed(ItemActivation.IsOwnedByInteractivePart(e.Source, card) ? null : card);
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        card.AddHandler(InputElement.PointerReleasedEvent, (object? _, PointerReleasedEventArgs e) =>
        {
            if (_closed || e.Handled || !MiddleClickTracker.IsRelease(e)) return;
            if (!tracker.Released(ItemActivation.IsOwnedByInteractivePart(e.Source, card) ? null : card)) return;
            e.Handled = true;
            if (ButtonOf(card, OpenPostPrefix + postId) is { IsEffectivelyEnabled: true } open) Click(open);
        }, RoutingStrategies.Tunnel);
    }

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static void MoveHitFocus(Panel list, Border card, BooruHitKey key)
    {
        int index = list.Children.IndexOf(card);
        int target = BooruHitKeys.Target(key, index, list.Children.Count);
        if (target < 0 || target == index) return;
        Control next = list.Children[target];
        next.Focus(NavigationMethod.Directional);
        next.BringIntoView();
    }

    /// <summary>The card of <paramref name="postId"/>, else the first hit, else the search field (after a menu action rebuilt the cards).</summary>
    private void FocusHit(string postId)
    {
        Control? card = _results.Children.FirstOrDefault(child => child.Name == "BooruResult:" + postId) ?? _results.Children.FirstOrDefault();
        if (card is { IsEffectivelyVisible: true }) card.Focus(NavigationMethod.Directional);
        else FocusSearchInput();
    }

    // ---- watcher cards ---------------------------------------------------------------------------------------

    /// <summary>
    /// Interaction audit V12: a double click on a watcher card (not on a button or selectable text) shows its query in the search box.
    /// Nothing is submitted and nothing is checked; the search field has the focus with the caret behind the query.
    /// Dead-affordance review 15.11.: the card is a keyboard stop like a hit card (the shared focus ring, a name, Up/Down/Home/End between
    /// cards), Enter on it does what the double click does, and its menu (the card's ContextMenuHost) opens from the focused card on the Menu
    /// key and Shift+F10 as well.
    /// </summary>
    private void ConfigureWatcherCard(Border card, string query)
    {
        card.Classes.Add("workspace-card-focusable");
        card.Focusable = true;
        card.IsTabStop = true;
        AutomationProperties.SetControlTypeOverride(card, AutomationControlType.ListItem);
        AutomationProperties.SetName(card, query);
        card.DoubleTapped += (_, e) =>
        {
            if (_closed || !ItemActivation.IsCardDoubleClick(e, card)) return;
            e.Handled = true;
            ShowWatcherQuery(query);
        };
        card.KeyDown += (_, e) =>
        {
            if (_closed) return;
            BooruHitKey key = BooruHitKeys.Route(e, card, ShortcutServiceCapturing());
            switch (key)
            {
                case BooruHitKey.None:
                    break;
                case BooruHitKey.Activate:
                    e.Handled = true;
                    ShowWatcherQuery(query);
                    break;
                default:
                    e.Handled = true;
                    MoveHitFocus(_watchers, card, key);
                    break;
            }
        };
    }

    internal void ShowWatcherQuery(string query)
    {
        if (_closed) return;
        ShowQuery(query);
        // The search tab may not be laid out yet when the tab changes: focus and caret are settled once it is.
        Dispatcher.UIThread.Post(() =>
        {
            if (_closed) return;
            _input.Focus();
            _input.ClearSelection();
            _input.CaretIndex = _input.Text?.Length ?? 0;
        }, DispatcherPriority.Loaded);
    }
}
