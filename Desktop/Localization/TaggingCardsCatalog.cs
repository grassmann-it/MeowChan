namespace MeowChan.Desktop.Localization;

// Texts of the card mode in the gallery's tagging mode (PT4, PLAN-POWER-TAGGEN §1.5): the K toggle and the five card keys, the cards'
// labels and descriptions, and the sentences of the live region. Prefix reserved for this catalog: cards.*; the command labels use the
// command.* family. Counts stand in fixed phrases, so no language needs plural variants; keys appear through placeholders ({0}).
internal static class TaggingCardsCatalog
{
    internal static readonly (string Key, string English, string German)[] Entries =
    {
        ("command.tags.queue.cards", "Card mode on / off", "Kartenmodus ein / aus"),
        ("command.tags.queue.card-1", "Choose card 1", "Karte 1 wählen"),
        ("command.tags.queue.card-2", "Choose card 2", "Karte 2 wählen"),
        ("command.tags.queue.card-3", "Choose card 3", "Karte 3 wählen"),
        ("command.tags.queue.card-4", "Choose card 4", "Karte 4 wählen"),
        ("command.tags.queue.card-5", "Choose card 5", "Karte 5 wählen"),
        ("cards.strip.name", "Decision cards", "Entscheidungskarten"),
        ("cards.name", "{0}, key {1}", "{0}, Taste {1}"),
        ("cards.action.back", "Back", "Zurück"),
        ("cards.action.skip", "Skip", "Überspringen"),
        ("cards.action.done", "Done & next", "Fertig & weiter"),
        ("cards.action.favorite", "Favorite & next", "Favorit & weiter"),
        ("cards.action.stamp", "Stamp & next", "Stempel & weiter"),
        ("cards.action.rate", "Rate {0} & next", "Bewertung {0} & weiter"),
        ("cards.desc.back", "Takes back the last decision and goes one medium back.", "Nimmt die letzte Entscheidung zurück und geht ein Medium zurück."),
        ("cards.desc.skip", "Skips the medium and goes to the next open one.", "Überspringt das Medium und geht zum nächsten offenen."),
        ("cards.desc.done", "Marks the medium as done and goes to the next open one.", "Markiert das Medium als fertig und geht zum nächsten offenen."),
        ("cards.desc.favorite", "Makes the medium a favorite, marks it as done and goes on.", "Macht das Medium zum Favoriten, markiert es als fertig und geht weiter."),
        ("cards.desc.stamp", "Gives the stamp tags to the medium, marks it as done and goes on.", "Gibt dem Medium die Stempel-Tags, markiert es als fertig und geht weiter."),
        ("cards.desc.rate", "Rates the medium {0} of 5, marks it as done and goes on.", "Bewertet das Medium mit {0} von 5, markiert es als fertig und geht weiter."),
        ("cards.status.on", "Card mode on: the keys {0} choose a card, {1} leaves the card mode.", "Kartenmodus an: Die Tasten {0} wählen eine Karte, {1} verlässt den Kartenmodus."),
        ("cards.status.off", "Card mode off.", "Kartenmodus aus."),
        ("cards.help.note", "{0} turns the card mode on or off. In the card mode the keys {1} to {2} choose the cards under the picture; a card applies its decision, finishes the medium and moves on.", "{0} schaltet den Kartenmodus ein oder aus. Im Kartenmodus wählen die Tasten {1} bis {2} die Karten unter dem Bild; eine Karte setzt ihre Entscheidung, schließt das Medium ab und geht weiter.")
    };
}
