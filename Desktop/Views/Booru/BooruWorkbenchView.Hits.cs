using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;

namespace MeowChan.Desktop.Booru;

/// <summary>What a key on a focused Booru hit asks for.</summary>
internal enum BooruHitKey { None, Activate, Previous, Next, First, Last,
    /// <summary>Tile view only: the tile one row up or down (the column count steps).</summary>
    RowUp, RowDown }

/// <summary>
/// The key routing of the Booru hit cards (interaction audit V11), without any control state so it can be checked on its own.
/// Enter is the card's primary action wherever the focus is not on a part with its own meaning (a button, selectable text);
/// the arrow keys and Home/End move between hits only while the card itself holds the focus. The context keys (Menu, Shift+F10 and the
/// rebindable command) are not routed here: <see cref="ContextMenuHost"/> owns them.
/// </summary>
internal static class BooruHitKeys
{
    internal static BooruHitKey Route(KeyEventArgs e, Avalonia.Visual card, bool shortcutCapture = false, bool tiles = false)
    {
        if (e.Handled || shortcutCapture || e.KeyModifiers != KeyModifiers.None) return BooruHitKey.None;
        if (ItemActivation.IsPrimaryKey(e))
            return ItemActivation.IsOwnedByInteractivePart(e.Source, card) ? BooruHitKey.None : BooruHitKey.Activate;
        if (!ReferenceEquals(e.Source, card)) return BooruHitKey.None;
        // In the tile view Left and Right step by one tile, Up and Down by a row; in the list Up and Down step by one hit and Left and Right do nothing.
        return e.Key switch
        {
            Key.Left when tiles => BooruHitKey.Previous,
            Key.Right when tiles => BooruHitKey.Next,
            Key.Up => tiles ? BooruHitKey.RowUp : BooruHitKey.Previous,
            Key.Down => tiles ? BooruHitKey.RowDown : BooruHitKey.Next,
            Key.Home => BooruHitKey.First,
            Key.End => BooruHitKey.Last,
            _ => BooruHitKey.None
        };
    }

    /// <summary>The index of the hit that <paramref name="key"/> moves to (without wrapping); -1 when there is none.</summary>
    internal static int Target(BooruHitKey key, int index, int count, int columns = 1)
    {
        if (count <= 0 || index < 0 || index >= count) return -1;
        columns = Math.Max(1, columns);
        return key switch
        {
            // One row up stays put in the first row; one row down from a row above a shorter last row lands on the last tile, in the last row it stays put.
            BooruHitKey.RowUp => index >= columns ? index - columns : index,
            BooruHitKey.RowDown => index + columns < count ? index + columns : index / columns < (count - 1) / columns ? count - 1 : index,
            BooruHitKey.Previous => Math.Max(0, index - 1),
            BooruHitKey.Next => Math.Min(count - 1, index + 1),
            BooruHitKey.First => 0,
            BooruHitKey.Last => count - 1,
            _ => -1
        };
    }

}

/// <summary>Keyboard, focus and menu of the result cards, and the double click of the watcher cards.</summary>
internal sealed partial class BooruWorkbenchView
{
    private readonly MiddleClickTracker _resultMiddleClick = new();

    private void InitializeHitList()
    {
        AutomationProperties.SetControlTypeOverride(_results, AutomationControlType.List);
        AutomationProperties.SetControlTypeOverride(_watchers, AutomationControlType.List);
        AddHandler(InputElement.PointerPressedEvent, (_, _) => _resultMiddleClick.Cancel(), RoutingStrategies.Tunnel, handledEventsToo: true);
        InitializeGallery();
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
    private void ConfigureHitCard(Border card, BooruResultRowView row, ResultActions result)
    {
        string postId = row.Post.PostId;
        card.Classes.Add("workspace-card-focusable");
        card.Focusable = true;
        card.IsTabStop = true;
        AutomationProperties.SetControlTypeOverride(card, AutomationControlType.ListItem);
        AutomationProperties.SetName(card, card.Name);
        AutomationProperties.SetAutomationId(card, card.Name);
        AutomationProperties.SetHelpText(card, Texts.T("booru.ui.result.hitName", postId, BooruText.Rating(row.Post.Rating),
            Texts.T(result.IsAvailable(ResultAction.ShowInMedia, row) ? "booru.ui.result.localYes" : "booru.ui.result.localNo")));
        card.AddHandler(ContextRequestedEvent, (sender, e) =>
        {
            if (e.TryGetPosition(card, out _) && result.IsCurrent()) card.Focus(NavigationMethod.Pointer);
        });
        AttachCardMenu(card, () => ComposeResultMenu(result), () =>
        {
            if (result.IsCurrent()) FocusHit(postId);
        });
        // Saved originals keep their Show in Media route. Unsaved hits open the inline gallery
        // (preview and sample only); neither Enter nor double-click implicitly downloads an original.
        bool Activate() => result.IsCurrent() && (result.Run(ResultAction.ShowInMedia) || OpenGallery(postId));
        card.DoubleTapped += (_, e) =>
        {
            if (ItemActivation.IsCardDoubleClick(e, card)) e.Handled = Activate();
        };
        ConfigureMiddleClick(card, result);
        ConfigureHitCursor(card, result);
        card.KeyDown += (_, e) =>
        {
            if (!result.IsCurrent()) return;
            if (TryToggleSelectionKey(e, card, postId)) { e.Handled = true; return; }
            BooruHitKey key = BooruHitKeys.Route(e, card, ShortcutServiceCapturing(), tiles: card.Parent is BooruTilePanel);
            switch (key)
            {
                case BooruHitKey.Activate:
                    e.Handled = Activate();
                    break;
                case BooruHitKey.None:
                    break;
                default:
                    e.Handled = true;
                    MoveHitFocus(card.Parent as Panel ?? _results, card, key);
                    break;
            }
        };
    }

    /// <summary>
    /// Dead-affordance review 15.11.: a middle click on a hit card (not on a button, a tag or selectable text; press and release on the same card)
    /// opens the post page in the browser with the same availability rules as the context menu.
    /// </summary>
    private void ConfigureMiddleClick(Border card, ResultActions result)
    {
        card.AddHandler(InputElement.PointerPressedEvent, (object? _, PointerPressedEventArgs e) =>
        {
            if (MiddleClickTracker.IsPress(e, card) && result.IsCurrent() && !ItemActivation.IsOwnedByInteractivePart(e.Source, card))
                _resultMiddleClick.Pressed(result);
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        card.AddHandler(InputElement.PointerReleasedEvent, (object? _, PointerReleasedEventArgs e) =>
        {
            if (e.Handled || !MiddleClickTracker.IsRelease(e)) return;
            bool onHit = new Rect(card.Bounds.Size).Contains(e.GetPosition(card)) && !ItemActivation.IsOwnedByInteractivePart(e.Source, card);
            if (_resultMiddleClick.Released(onHit ? result : null) && card.IsAttachedToVisualTree() && result.IsCurrent())
                e.Handled = result.Run(ResultAction.Open);
        }, RoutingStrategies.Tunnel);
    }

    private static void MoveHitFocus(Panel list, Border card, BooruHitKey key)
    {
        int index = list.Children.IndexOf(card);
        int target = BooruHitKeys.Target(key, index, list.Children.Count, (list as BooruTilePanel)?.Columns ?? 1);
        if (target < 0 || target == index) return;
        Control next = list.Children[target];
        next.BringIntoView();
        next.Focus(NavigationMethod.Directional);
    }

    /// <summary>Scrolls to the same hit and restores focus after its card has been rebuilt.</summary>
    private void FocusHit(string postId)
    {
        if (_resultView == BooruResultView.Gallery && ReferenceEquals(_gallery.Parent, _results)) { FocusGalleryStage(); return; }
        IReadOnlyList<Control> cards = ResultCards;
        Control? card = cards.FirstOrDefault(child => child.Name == "BooruResult:" + postId);
        if (card is { IsEffectivelyVisible: true })
        {
            card.BringIntoView();
            card.Focus(NavigationMethod.Directional);
        }
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
        if (OpensNewSearchTab)
        {
            if (!TryShowQueryInNewTab(query)) return;
        }
        else ShowQuery(query);
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
