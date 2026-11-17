using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;
using MeowChan.Tags;

namespace MeowChan.Desktop;

// The card mode (PT4, PLAN-POWER-TAGGEN §1.5): K inside the tagging mode swaps the decision strip's four buttons for up to five big cards,
// each with its own key (1 to 5). A card is one decision the walk already knows: Back, Skip or Done (the strip's own), the stamp (PT5),
// or a favorite / a rating (PT6) that is written first and then finishes the medium. Everything goes through the tagging mode's walk,
// its status line and the panel's decision log (TaggingUndo), so Backspace, Ctrl+Z and the Back card take cards back like any other
// decision, newest first; there is no second log. The tag panel stays as it is (its decision session holds the favorite and rating
// steps): a card that needs it opens it again, like the stamp does. Which cards there are is a setting (TaggingCardSettings).
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "StopAsync drains all viewer-owned disposable subcomponents.")]
internal sealed partial class GalleryViewer
{
    private readonly Border _cardsRow = new() { Name = "GalleryTaggingCards", IsVisible = false };
    private readonly Grid _cardsGrid = new() { Name = "GalleryTaggingCardGrid", ColumnSpacing = 8 };
    private readonly List<Button> _cardButtons = new();
    private IReadOnlyList<TaggingCardAction> _cards = TaggingCardSettings.Default.Cards;
    private bool _cardsOn;
    private bool _cardBusy;

    /// <summary>True while the tagging mode shows the cards (K).</summary>
    internal bool IsCardMode => _tagging != null && _cardsOn;
    internal Border TaggingCardsRow => _cardsRow;
    /// <summary>The cards shown now, left to right (key 1 first).</summary>
    internal IReadOnlyList<Button> TaggingCardButtons => _cardButtons;
    internal IReadOnlyList<TaggingCardAction> TaggingCards => _cards;

    // Part of the strip's content (one parent, so the row follows the controls like the strip does): below the buttons, above the status line.
    private void AddCardsRow(Panel content)
    {
        _cardsRow.Padding = new Thickness(0, 4, 0, 0);
        _cardsGrid.HorizontalAlignment = HorizontalAlignment.Stretch;
        _cardsRow.Child = _cardsGrid;
        AutomationProperties.SetName(_cardsRow, Texts.T("cards.strip.name"));
        content.Children.Add(_cardsRow);
    }

    // The mode starts: the saved setting says which cards there are and whether the walk opens in the card mode.
    private void StartCards()
    {
        TaggingCardSettings settings = _taggingOwner is ITaggingCardsOwner owner ? owner.Cards : TaggingCardSettings.Default;
        _cards = settings.Cards;
        _cardBusy = false;
        BuildCards();
        _cardsOn = settings.StartInCards;
        ApplyCardVisibility(); // the panel is opened by the mode's own start
    }

    private void EndCards()
    {
        _cardsOn = false;
        _cardBusy = false;
        _cardsRow.IsVisible = false;
        ShowStripButtons(true);
        _cardButtons.Clear();
        _cardsGrid.Children.Clear();
    }

    private void ShowStripButtons(bool visible)
    {
        foreach (Button button in new[] { _taggingBack, _taggingSkip, _taggingDone, _taggingStamp }) button.IsVisible = visible;
    }

    private void BuildCards()
    {
        _cardButtons.Clear();
        _cardsGrid.Children.Clear();
        _cardsGrid.ColumnDefinitions.Clear();
        for (int i = 0; i < _cards.Count; i++)
        {
            int number = i + 1;
            TaggingCardAction action = _cards[i];
            Button button = new() { Name = "GalleryTaggingCard" + number, Classes = { "card-action" }, HorizontalAlignment = HorizontalAlignment.Stretch };
            _cardsGrid.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
            Grid.SetColumn(button, i);
            button.Click += (_, _) => _ = ApplyCardAsync(action);
            _cardButtons.Add(button);
            _cardsGrid.Children.Add(button);
        }
        PresentCards();
    }

    // Labels, names and keys from the current language and the effective shortcuts (also after a language or shortcut change).
    private void PresentCards()
    {
        for (int i = 0; i < _cardButtons.Count && i < _cards.Count; i++)
        {
            Button button = _cardButtons[i];
            TaggingCardAction action = _cards[i];
            TextBlock label = new() { Text = action.Label(), TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold };
            TextBlock key = new() { Text = TaggingCardsText.KeyOf(i + 1), TextAlignment = TextAlignment.Center, Classes = { "workspace-meta" } };
            button.Content = new StackPanel { Spacing = 2, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Children = { label, key } };
            string name = TaggingCardsText.Name(action, i + 1);
            AutomationProperties.SetName(button, name);
            AutomationProperties.SetHelpText(button, action.Description());
            AutomationProperties.SetAcceleratorKey(button, CommandCatalog.ShortcutDisplay(TaggingCardCommands.Card(i + 1)) ?? string.Empty);
            ToolTip.SetTip(button, name + " · " + action.Description());
        }
        AutomationProperties.SetName(_cardsRow, Texts.T("cards.strip.name"));
    }

    // Language or shortcut change while the mode runs.
    private void RefreshCardTexts()
    {
        if (_tagging == null) return;
        PresentCards();
        RefreshCards();
    }

    // The cards follow the strip: a medium that cannot be decided (left the queue) leaves every card but Back disabled, and a card
    // being applied holds the others until it ends.
    private void RefreshCards()
    {
        if (!_cardsOn || _tagging == null) return;
        bool decidable = _taggingDone.IsEnabled;
        for (int i = 0; i < _cardButtons.Count && i < _cards.Count; i++)
            _cardButtons[i].IsEnabled = !_cardBusy && (decidable || _cards[i].Kind == TaggingCardKind.Back);
    }

    /// <summary>
    /// Switches the card mode on or off (K). On, the strip's buttons give way to the cards and the tag panel opens when it is closed (its
    /// decision session is the log of favorite and rating cards); off, the strip is back as it was. The choice is kept for the next walk
    /// when <paramref name="persist"/> is set (the person's own toggle).
    /// </summary>
    internal void SetCardMode(bool on, bool persist = true)
    {
        if (_tagging == null || on == _cardsOn) return;
        _cardsOn = on;
        ApplyCardVisibility();
        if (on && !_tagPanelOpen && _taggingOwner != null && _entry != null) ShowTagPanel(_taggingOwner);
        if (persist && _taggingOwner is ITaggingCardsOwner owner) owner.SaveCardsActive(on);
        SayTagging(on ? TaggingCardsText.On(_cards.Count) : TaggingCardsText.Off());
    }

    private void ApplyCardVisibility()
    {
        _cardsRow.IsVisible = _cardsOn;
        ShowStripButtons(!_cardsOn);
        RefreshCards();
    }

    /// <summary>
    /// Routes K and, while the cards are shown, the card keys. Called by the tagging mode's own router after its decision keys, so a key typed
    /// in the tag field stays typing. A card key beyond the shown cards is not taken. Returns true when handled.
    /// </summary>
    private bool RouteCardKey(KeyEventArgs e)
    {
        if (_tagging == null) return false;
        if (CommandBindings.Matches(e, TaggingCardCommands.Toggle))
        {
            e.Handled = true;
            SetCardMode(!_cardsOn);
            return true;
        }
        if (!_cardsOn) return false;
        for (int i = 0; i < _cards.Count; i++)
        {
            if (!CommandBindings.Matches(e, TaggingCardCommands.Card(i + 1))) continue;
            e.Handled = true;
            _ = ApplyCardAsync(_cards[i]);
            return true;
        }
        return false;
    }

    /// <summary>
    /// Applies a card to the shown medium. The favorite or rating is written first (one undoable step of the panel's decision session);
    /// only when it is saved the medium is finished and the walk goes on, with one sentence for both. A refused write leaves the medium
    /// open and says why. One card at a time: a second key while one is being applied is ignored.
    /// </summary>
    internal async Task ApplyCardAsync(TaggingCardAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (_tagging is not { } walk || !_cardsOn || _cardBusy || _navigationPending || _terminalRetirement || _entry is not { } entry) return;
        _cardBusy = true;
        RefreshCards();
        try
        {
            switch (action.Kind)
            {
                case TaggingCardKind.Back: await TaggingBackAsync(); break;
                case TaggingCardKind.Skip: TaggingDecide(TagQueueState.Skipped); break;
                case TaggingCardKind.Done: TaggingDecide(TagQueueState.Done); break;
                case TaggingCardKind.Stamp: await TaggingStampAsync(); break;
                default: await ApplyCardDecisionAsync(walk, entry, action); break;
            }
        }
        catch (Exception error) when (UnhandledErrors.TryRecover(error, "gallery.tagging.cards")) { /* Logged and shown. */ }
        finally
        {
            _cardBusy = false;
            RefreshCards();
        }
    }

    private async Task ApplyCardDecisionAsync(TaggingWalk walk, GalleryEntry entry, TaggingCardAction action)
    {
        if (_tagController?.Decisions == null && _taggingOwner != null) ShowTagPanel(_taggingOwner); // the panel's session is the log
        if (_tagController is not { Decisions: { } decisions } controller)
        {
            SayTagging(Texts.T("gallery.decision.status.unavailable"));
            return;
        }
        IReadOnlyList<GalleryEntry> targets = controller.DecisionTargets(); // read now: paging meanwhile never redirects the write
        GalleryDecisionStatus status = action.Kind == TaggingCardKind.Favorite
            ? await decisions.SetFavoriteAsync(targets, true)
            : await decisions.SetRatingAsync(targets, action.Rating);
        if (!ReferenceEquals(walk, _tagging) || _terminalRetirement) return;
        RefreshDecisionRow();
        bool saved = status.Kind is GalleryDecisionStatusKind.FavoriteSaved or GalleryDecisionStatusKind.RatingSaved;
        // The medium was changed meanwhile (paged away during the write): the decision stays a favorite or rating, nothing else is decided.
        if (!saved || !ReferenceEquals(_entry, entry))
        {
            if (status.Kind != GalleryDecisionStatusKind.None) SayTagging(status.Text());
            return;
        }
        TaggingDecide(TagQueueState.Done, status.Text());
    }
}
