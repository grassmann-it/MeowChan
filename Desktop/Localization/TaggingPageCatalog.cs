namespace MeowChan.Desktop.Localization;

// Texts of the tagging page (PT2, PLAN-POWER-TAGGEN §1.3): progress, stamp set editor, the queue list with its states and the
// commands of the page. Prefix reserved for this catalog: tagging.page.*; the command labels use the command.* family.
// The sentences of an add, the empty queue and the queue errors stay with TagQueueCatalog (queue.*), the stamp sentences of a
// tag surface with QuickTagCatalog (tags.quick.stamp.*). Wording is count-neutral on purpose, so no plural variants are needed.
internal static class TaggingPageCatalog
{
    internal static readonly (string Key, string English, string German)[] Entries =
    {
        ("command.navigation.tagging", "Tagging", "Taggen"),
        ("command.tagging.start", "Start tagging", "Taggen starten"),
        ("command.tagging.continue-here", "Continue here", "Hier fortsetzen"),
        ("command.tagging.mark-done", "Mark as finished", "Als erledigt markieren"),
        ("command.tagging.mark-open", "Mark as open", "Als offen markieren"),
        ("command.tagging.mark-skipped", "Mark as skipped", "Als übersprungen markieren"),
        ("command.tagging.move-up", "Move up", "Nach oben"),
        ("command.tagging.move-down", "Move down", "Nach unten"),
        ("command.tagging.remove", "Remove from queue", "Aus der Warteschlange entfernen"),
        ("command.tagging.add", "Add…", "Hinzufügen…"),
        ("command.tagging.clear-done", "Remove finished", "Erledigte entfernen"),
        ("command.tagging.clear", "Clear queue…", "Warteschlange leeren…"),
        ("tagging.page.title", "Tagging", "Taggen"),
        ("tagging.page.subtitle", "Work through the tagging queue: the gallery shows the open entries one after the other.", "Arbeite die Tagging-Warteschlange ab: Die Galerie zeigt dir die offenen Einträge nacheinander."),
        ("tagging.page.progress", "Open: {0} · Finished: {1} · Skipped: {2}", "Offen: {0} · Erledigt: {1} · Übersprungen: {2}"),
        ("tagging.page.percent", "{0:P0} worked through", "{0:P0} abgearbeitet"),
        ("tagging.page.progressName", "Progress of the tagging queue", "Fortschritt der Tagging-Warteschlange"),
        ("tagging.page.continue", "Continue at {0} of {1}", "Fortsetzen bei {0} von {1}"),
        ("tagging.page.filter", "Show", "Anzeigen"),
        ("tagging.page.filter.all", "All entries", "Alle Einträge"),
        ("tagging.page.state.open", "Open", "Offen"),
        ("tagging.page.state.done", "Finished", "Erledigt"),
        ("tagging.page.state.skipped", "Skipped", "Übersprungen"),
        ("tagging.page.col.kind", "Type", "Typ"),
        ("tagging.page.col.name", "File", "Datei"),
        ("tagging.page.col.source", "Thread or source", "Thread oder Quelle"),
        ("tagging.page.col.state", "State", "Zustand"),
        ("tagging.page.row", "Entry {0}: {1}, {2}", "Eintrag {0}: {1}, {2}"),
        ("tagging.page.row.next", "Entry {0}: {1}, {2}, the walk continues here", "Eintrag {0}: {1}, {2}, hier geht es weiter"),
        ("tagging.page.next", "The walk continues here.", "Hier geht es weiter."),
        ("tagging.page.listName", "Entries of the tagging queue", "Einträge der Tagging-Warteschlange"),
        ("tagging.page.filterEmpty", "No entry has this state.", "Kein Eintrag hat diesen Zustand."),
        ("tagging.page.keys", "{0} continues at the selected entry · {1} removes it · {2} and {3} move it", "{0} setzt beim markierten Eintrag fort · {1} entfernt ihn · {2} und {3} verschieben ihn"),
        ("tagging.page.loading", "Reading the tagging queue…", "Tagging-Warteschlange wird gelesen…"),
        ("tagging.page.stamp", "Stamp set", "Stempel-Set"),
        ("tagging.page.stamp.hint", "The stamp tags come first in every tag panel; {0} gives all of them to the shown medium at once.", "Die Stempel-Tags stehen in jedem Tag-Panel vorn; {0} gibt sie alle auf einmal an das gezeigte Medium."),
        ("tagging.page.stamp.empty", "The stamp is empty. Add the tags you give most media of this queue.", "Der Stempel ist leer. Füge die Tags hinzu, die du den meisten Medien dieser Warteschlange gibst."),
        ("tagging.page.stamp.input", "Tag for the stamp", "Tag für den Stempel"),
        ("tagging.page.stamp.add", "Add to stamp", "Zum Stempel"),
        ("tagging.page.stamp.remove", "Remove “{0}” from the stamp", "„{0}“ aus dem Stempel entfernen"),
        ("tagging.page.stamp.invalid", "“{0}” cannot be a tag name.", "„{0}“ kann kein Tagname sein."),
        ("tagging.page.stamp.already", "“{0}” is already in the stamp.", "„{0}“ ist schon im Stempel."),
        ("tagging.page.clear.ask", "Clear the tagging queue? All its entries leave it ({0}); tags, favorites and ratings stay as they are.", "Tagging-Warteschlange leeren? Alle Einträge verlassen sie ({0}); Tags, Favoriten und Bewertungen bleiben, wie sie sind."),
        ("tagging.page.clear.yes", "Clear queue", "Leeren"),
        ("tagging.page.cleared", "The tagging queue is empty now.", "Die Tagging-Warteschlange ist jetzt leer."),
        ("tagging.page.clearedDone", "Finished entries removed: {0}.", "Erledigte Einträge entfernt: {0}."),
        ("tagging.page.removed", "Removed from the queue: {0}.", "Aus der Warteschlange entfernt: {0}."),
        ("tagging.page.marked.open", "Marked as open: {0}.", "Als offen markiert: {0}."),
        ("tagging.page.marked.done", "Marked as finished: {0}.", "Als erledigt markiert: {0}."),
        ("tagging.page.marked.skipped", "Marked as skipped: {0}.", "Als übersprungen markiert: {0}."),
        ("tagging.page.moved", "Moved to position {0}.", "Auf Position {0} verschoben."),
        ("tagging.page.add.how", "Select media here and choose “Add to tagging queue” in the selection bar or the context menu.", "Markiere hier Medien und wähle in der Auswahlleiste oder im Kontextmenü „Zur Tagging-Warteschlange“.")
    };
}
