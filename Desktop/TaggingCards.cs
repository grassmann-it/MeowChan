using System;
using System.Collections.Generic;
using System.Linq;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;

namespace MeowChan.Desktop;

// PT4 (PLAN-POWER-TAGGEN-2026-11-14 §1.5): the card mode of the tagging mode, model without UI. Five big cards below the picture, each
// with its own key (1 to 5); a card applies its decision, finishes the medium (Done) or skips it, and the walk goes on. The cards are a
// setting: Config.TaggingCards names them (any of the actions below, in order), Config.TaggingCardCount says how many are shown
// (default 5, minimum 2, maximum 5 = the number of card keys), Config.TaggingCardsDefault says whether a walk starts in the card mode.
// The viewer (GalleryViewer.Cards.cs) shows the cards and applies them through the tagging mode's own walk and decision log.

/// <summary>What a card does.</summary>
internal enum TaggingCardKind
{
    /// <summary>Takes back the newest decision and goes one medium back (the Backspace of the mode).</summary>
    Back,
    /// <summary>Skips the medium and goes to the next open one.</summary>
    Skip,
    /// <summary>Finishes the medium and goes to the next open one.</summary>
    Done,
    /// <summary>Makes the medium (or every marked one) a favorite, then finishes it.</summary>
    Favorite,
    /// <summary>Gives the stamp tags (PT5) to the medium, then finishes it.</summary>
    Stamp,
    /// <summary>Rates the medium 1 to 5, then finishes it.</summary>
    Rate
}

/// <summary>One choice a card can offer: its stable id (as saved in the settings), its kind and, for a rating, the stars.</summary>
internal sealed record TaggingCardAction(string Id, TaggingCardKind Kind, int Rating = 0)
{
    /// <summary>The text key of the label ("Skip"); a rating card takes its stars as the placeholder.</summary>
    internal string LabelKey => Kind == TaggingCardKind.Rate ? "cards.action.rate" : "cards.action." + Id;

    /// <summary>The text key of the longer description (the card's tooltip and help text).</summary>
    internal string DescriptionKey => Kind == TaggingCardKind.Rate ? "cards.desc.rate" : "cards.desc." + Id;

    internal string Icon => Kind switch
    {
        TaggingCardKind.Back => CommandCatalog.Get("tags.queue.back").Icon,
        TaggingCardKind.Skip => CommandCatalog.Get("tags.queue.skip").Icon,
        TaggingCardKind.Favorite => CommandCatalog.Get("favorites.add").Icon,
        TaggingCardKind.Stamp => CommandCatalog.Get(QuickTagCommands.StampApply).Icon,
        TaggingCardKind.Rate => CommandCatalog.Get("media.rating.edit").Icon,
        _ => CommandCatalog.Get("tags.queue.done-next").Icon
    };

    /// <summary>The card's visible label in the current language.</summary>
    internal string Label() => Kind == TaggingCardKind.Rate ? Texts.T(LabelKey, Rating.ToString(Texts.Culture)) : Texts.T(LabelKey);

    /// <summary>What the card does, in the current language.</summary>
    internal string Description() => Kind == TaggingCardKind.Rate ? Texts.T(DescriptionKey, Rating.ToString(Texts.Culture)) : Texts.T(DescriptionKey);
}

/// <summary>The catalog commands of the card mode: the toggle (K) and the five card keys (1 to 5, only while the mode shows cards).</summary>
internal static class TaggingCardCommands
{
    /// <summary>The most cards there are: one key each. A fixed ceiling, documented; the setting can only lower it.</summary>
    internal const int CardKeys = 5;
    internal const string Toggle = "tags.queue.cards";

    internal static string Card(int number) => "tags.queue.card-" + number;

    internal static IReadOnlyList<string> Cards { get; } = Array.AsReadOnly(Enumerable.Range(1, CardKeys).Select(Card).ToArray());

    internal static IReadOnlyList<string> All { get; } = Array.AsReadOnly(new[] { Toggle }.Concat(Cards).ToArray());
}

/// <summary>Every action a card can have and the default five.</summary>
internal static class TaggingCardActions
{
    internal static IReadOnlyList<TaggingCardAction> All { get; } = Array.AsReadOnly(new[]
    {
        new TaggingCardAction("back", TaggingCardKind.Back),
        new TaggingCardAction("skip", TaggingCardKind.Skip),
        new TaggingCardAction("done", TaggingCardKind.Done),
        new TaggingCardAction("favorite", TaggingCardKind.Favorite),
        new TaggingCardAction("stamp", TaggingCardKind.Stamp),
        new TaggingCardAction("rate-1", TaggingCardKind.Rate, 1),
        new TaggingCardAction("rate-2", TaggingCardKind.Rate, 2),
        new TaggingCardAction("rate-3", TaggingCardKind.Rate, 3),
        new TaggingCardAction("rate-4", TaggingCardKind.Rate, 4),
        new TaggingCardAction("rate-5", TaggingCardKind.Rate, 5)
    });

    /// <summary>The plan's order (Back, Skip, Favorite, then the stamp instead of the rating, Done): left to right on keys 1 to 5.</summary>
    internal static IReadOnlyList<string> DefaultIds { get; } = Array.AsReadOnly(new[] { "back", "skip", "favorite", "stamp", "done" });

    internal static TaggingCardAction? Find(string? id) =>
        string.IsNullOrWhiteSpace(id) ? null : All.FirstOrDefault(action => string.Equals(action.Id, id.Trim(), StringComparison.OrdinalIgnoreCase));
}

/// <summary>The card setting as the viewer needs it: whether a walk starts in the card mode, and the cards in order (key 1 first).</summary>
internal sealed record TaggingCardSettings(bool StartInCards, IReadOnlyList<TaggingCardAction> Cards)
{
    internal static TaggingCardSettings Default { get; } = From(false, null, Config.TaggingCardCountDefault);

    /// <summary>
    /// Reads the saved choice. Unknown names and repeated ones are dropped; the count is clamped to its range; fewer cards than the count
    /// are filled up from the default set and then from the other actions, so a damaged or short list never leaves the mode without cards.
    /// </summary>
    internal static TaggingCardSettings From(bool startInCards, IEnumerable<string?>? ids, int count)
    {
        int wanted = Math.Clamp(count, Config.TaggingCardCountMinimum, Config.TaggingCardCountMaximum);
        List<TaggingCardAction> cards = new(wanted);
        void Take(TaggingCardAction? action)
        {
            if (action != null && cards.Count < wanted && cards.All(card => card.Id != action.Id)) cards.Add(action);
        }
        foreach (string? id in ids ?? Array.Empty<string?>()) Take(TaggingCardActions.Find(id));
        foreach (string id in TaggingCardActions.DefaultIds) Take(TaggingCardActions.Find(id));
        foreach (TaggingCardAction action in TaggingCardActions.All) Take(action);
        return new TaggingCardSettings(startInCards, Array.AsReadOnly(cards.ToArray()));
    }

    internal static TaggingCardSettings FromConfig(Config config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return From(config.TaggingCardsDefault, config.TaggingCards, config.TaggingCardCount);
    }
}

/// <summary>What the owner (the window) keeps for the card mode: the setting, and the choice of the mode made with K.</summary>
internal interface ITaggingCardsOwner
{
    /// <summary>The saved card setting, read when a walk starts and when the mode is toggled.</summary>
    TaggingCardSettings Cards { get; }
    /// <summary>The person switched the card mode on or off: the next walk starts the same way.</summary>
    void SaveCardsActive(bool active);
}

/// <summary>The sentences of the card mode; one place so the strip, the live region and the tests agree.</summary>
internal static class TaggingCardsText
{
    /// <summary>"Card mode on: the keys 1–5 choose a card, K leaves the card mode."</summary>
    internal static string On(int cards) => Texts.T("cards.status.on",
        Commands.CommandCatalog.ShortcutDisplay(TaggingCardCommands.Card(1)) + "–" + Commands.CommandCatalog.ShortcutDisplay(TaggingCardCommands.Card(Math.Max(1, cards))),
        TaggingModeText.Key(TaggingCardCommands.Toggle));

    internal static string Off() => Texts.T("cards.status.off");

    /// <summary>The accessible name of a card: "Skip and next, key 2".</summary>
    internal static string Name(TaggingCardAction action, int number) => Name(action.Label(), number);

    internal static string Name(string label, int number) =>
        KeyOf(number) is { } key ? Texts.T("cards.name", label, key) : label;

    /// <summary>The effective shortcut, if the card has one.</summary>
    internal static string? KeyOf(int number) => CommandCatalog.ShortcutTooltipDisplay(TaggingCardCommands.Card(number), "gallery-tagging");

    /// <summary>The note in F1 under the mode's keys.</summary>
    internal static string HelpNote() => Texts.T("cards.help.note", TaggingModeText.Key(TaggingCardCommands.Toggle),
        KeyOf(1), KeyOf(TaggingCardCommands.CardKeys));
}
