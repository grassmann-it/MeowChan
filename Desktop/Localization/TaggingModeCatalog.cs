namespace MeowChan.Desktop.Localization;

// Texts of the tagging mode in the gallery (PT3, PLAN-POWER-TAGGEN §1.4/§1.6): the decision strip, the header position, the progress,
// the sentences after a decision or an undo, and the F1 section. Prefix reserved for this catalog: tagging.*; the command labels use
// the command.* family. Keys appear through placeholders ({0}) taken from the effective shortcuts, so a rebound key is named correctly.
// Counts stand in fixed phrases ("Open: 194"), so no language needs plural variants.
internal static class TaggingModeCatalog
{
    internal static readonly (string Key, string English, string German)[] Entries =
    {
        ("command.tags.queue.done-next", "Done and next", "Fertig und weiter"),
        ("command.tags.queue.skip", "Skip and next", "Überspringen und weiter"),
        ("command.tags.queue.back", "Back: undo the last decision", "Zurück: letzte Entscheidung zurücknehmen"),
        ("command.tags.queue.play-pause", "Play / pause in tagging mode", "Wiedergabe / Pause im Tagging-Modus"),
        ("tagging.strip.name", "Tagging decisions", "Tagging-Entscheidungen"),
        ("tagging.back", "Back", "Zurück"),
        ("tagging.skip", "Skip", "Überspringen"),
        ("tagging.done", "Done & next", "Fertig & weiter"),
        ("tagging.stamp", "Stamp & next", "Stempel & weiter"),
        ("tagging.position", "Queue {0} of {1}", "Warteschlange {0} von {1}"),
        ("tagging.progress", "Done: {0} · Skipped: {1} · Open: {2}", "Fertig: {0} · Übersprungen: {1} · Offen: {2}"),
        ("tagging.status.start", "Tagging mode: {0} finishes the medium, {1} skips it, {2} takes back the last decision.", "Tagging-Modus: {0} schließt das Medium ab, {1} überspringt es, {2} nimmt die letzte Entscheidung zurück."),
        ("tagging.status.unavailable", "Not shown because the file is missing right now (they stay open in the queue): {0}.", "Nicht gezeigt, weil die Datei gerade fehlt (sie bleiben in der Warteschlange offen): {0}."),
        ("tagging.status.done", "Done: “{0}”. Next: “{1}”.", "Fertig: „{0}“. Nächstes: „{1}“."),
        ("tagging.status.skipped", "Skipped: “{0}”. Next: “{1}”.", "Übersprungen: „{0}“. Nächstes: „{1}“."),
        ("tagging.status.doneLast", "Done: “{0}”. Every medium of this walk is decided.", "Fertig: „{0}“. Alle Medien dieses Durchgangs sind entschieden."),
        ("tagging.status.skippedLast", "Skipped: “{0}”. Every medium of this walk is decided.", "Übersprungen: „{0}“. Alle Medien dieses Durchgangs sind entschieden."),
        ("tagging.status.moreOpen", "Still open in the queue outside this walk: {0}. Walk through the tagging queue again to continue.", "Außerhalb dieses Durchgangs noch offen: {0}. Gehe die Tagging-Warteschlange erneut durch, um weiterzumachen."),
        ("tagging.status.undone.open", "Taken back: “{0}” is open again.", "Zurückgenommen: „{0}“ ist wieder offen."),
        ("tagging.status.undone.done", "Taken back: “{0}” is done again.", "Zurückgenommen: „{0}“ ist wieder fertig."),
        ("tagging.status.undone.skipped", "Taken back: “{0}” is skipped again.", "Zurückgenommen: „{0}“ ist wieder übersprungen."),
        ("tagging.status.undoGone", "Nothing taken back: that entry is no longer in the tagging queue.", "Nichts zurückgenommen: Dieser Eintrag ist nicht mehr in der Tagging-Warteschlange."),
        ("tagging.status.missing", "“{0}” is no longer in the tagging queue; nothing was changed.", "„{0}“ ist nicht mehr in der Tagging-Warteschlange; es wurde nichts geändert."),
        ("tagging.status.failed", "The decision was not saved: {0}", "Die Entscheidung wurde nicht gespeichert: {0}"),
        ("tagging.help.heading", "Tagging mode", "Tagging-Modus"),
        ("tagging.help.note", "{0} and {1} move without deciding. Esc closes the tag panel first, then the tagging mode; the next walk continues at the medium shown last. {2} takes back tags, favorite, rating and decisions, newest first.", "{0} und {1} blättern, ohne zu entscheiden. Esc schließt zuerst das Tag-Panel, dann den Tagging-Modus; der nächste Durchgang setzt beim zuletzt gezeigten Medium fort. {2} nimmt Tags, Favorit, Bewertung und Entscheidungen zurück, die neueste zuerst.")
    };
}
