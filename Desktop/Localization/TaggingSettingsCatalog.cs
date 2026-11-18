namespace MeowChan.Desktop.Localization;

// Texts of the tagging group on the settings page (PT7, PLAN-POWER-TAGGEN): the stamp size, the tagging queue walk window, the undo depth, the card count, the
// card mode start choice and the five card slots. Prefix reserved for this catalog: settings.tagging.*. Counts stand in placeholders or in the
// range sentence of the page (settings.reader.range), so no language needs plural variants.
internal static class TaggingSettingsCatalog
{
    internal static readonly (string Key, string English, string German)[] Entries =
    {
        ("settings.tagging.group", "Tagging", "Taggen"),
        ("settings.tagging.stampSize", "Stamp: tags it holds", "Stempel: enthaltene Tags"),
        ("settings.tagging.stampSize.hint", "How many tags the stamp of quick tagging holds. A smaller number shortens the stamp from its end; nothing else changes.", "Wie viele Tags der Stempel des Schnell-Taggens aufnimmt. Eine kleinere Zahl kürzt den Stempel von hinten; sonst ändert sich nichts."),
        ("settings.tagging.walkWindow", "Tagging queue: media per walk", "Tagging-Warteschlange: Medien je Durchgang"),
        ("settings.tagging.walkWindow.hint", "How many open entries of the tagging queue one walk opens at once. The next walk continues behind them; nothing is removed from the queue.", "Wie viele offene Einträge der Tagging-Warteschlange ein Durchgang auf einmal öffnet. Der nächste Durchgang macht dahinter weiter; aus der Warteschlange wird nichts entfernt."),
        ("settings.tagging.undoDepth", "Tagging queue: decisions to take back", "Tagging-Warteschlange: rücknehmbare Entscheidungen"),
        ("settings.tagging.undoDepth.hint", "How many decisions of a walk (finish or skip) the undo key can take back, newest first. A smaller number forgets the oldest steps; nothing in the queue changes.", "Wie viele Entscheidungen eines Durchgangs (abschließen oder überspringen) die Rückgängig-Taste zurücknehmen kann, die neueste zuerst. Eine kleinere Zahl vergisst die ältesten Schritte; an der Warteschlange ändert sich nichts."),
        ("settings.tagging.cardCount", "Cards in the card mode", "Karten im Kartenmodus"),
        ("settings.tagging.cardCount.hint", "How many of the cards the card mode shows, counted from the left. Every card has its own key, 1 to 5. Applies from the next walk.", "Wie viele Karten der Kartenmodus zeigt, von links gezählt. Jede Karte hat eine eigene Taste, 1 bis 5. Gilt ab dem nächsten Durchgang."),
        ("settings.tagging.cardsStart", "Start walks in the card mode", "Durchgänge im Kartenmodus beginnen"),
        ("settings.tagging.cardsStart.hint", "A walk through the tagging queue opens with the cards instead of the tag panel.", "Ein Durchgang durch die Tagging-Warteschlange beginnt mit den Karten statt mit dem Tag-Panel."),
        ("settings.tagging.cardsStart.keyHint", "The {0} key during a walk changes this choice.", "Die Taste {0} im Durchgang ändert diese Auswahl."),
        ("settings.tagging.cards", "Decision cards", "Entscheidungskarten"),
        ("settings.tagging.cards.hint", "What each card does, from the left (keys 1 to 5). Choosing a decision that another card already has swaps the two cards. Applies from the next walk.", "Was jede Karte tut, von links (Tasten 1 bis 5). Wer eine Entscheidung wählt, die eine andere Karte schon hat, tauscht die beiden Karten. Gilt ab dem nächsten Durchgang."),
        ("settings.tagging.cardSlot", "Card {0}", "Karte {0}"),
    };
}
