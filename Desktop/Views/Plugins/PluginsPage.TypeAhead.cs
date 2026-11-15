using System;
using System.Linq;
using Avalonia.Controls;
using MeowChan.Desktop.Controls;
using Ursa.Controls;

namespace MeowChan;

// Type-ahead search of the plugin list (audit V7): the first letters of a plugin's name jump to the next card that starts with them, and the card becomes the
// selected one exactly as with the arrow keys. A running operation, an open discard question and a hidden list leave the keys alone.
public partial class PluginsPage
{
    private void AttachTypeAhead()
    {
        SelectionList list = C<SelectionList>("PluginCards");
        SelectionListItem[] cards = [];
        TypeAheadSearch.Attach(list,
            () => { cards = list.Items.OfType<SelectionListItem>().Where(card => card.IsEffectivelyVisible).ToArray(); return cards.Select(CardLabel).ToArray(); },
            () => list.SelectedItem is SelectionListItem current ? Array.IndexOf(cards, current) : -1,
            index => { list.SelectedItem = cards[index]; cards[index].BringIntoView(); cards[index].Focus(); },
            source => MainWindow.IsListCommandSource(source, list),
            () => !_ended && !_busy && !IsDiscardConfirmationOpen && list.IsEffectivelyVisible);
    }

    /// <summary>The plugin's name on its card (the first line of the card), what the search compares.</summary>
    internal static string? CardLabel(SelectionListItem card) =>
        (card.Content as Avalonia.Controls.Panel)?.Children.OfType<TextBlock>().FirstOrDefault()?.Text;
}
