namespace MeowChan.Desktop.Localization;

// Texts of the tagging queue (PT1, PLAN-POWER-TAGGEN): "Add to tagging queue" on the media page, the archive and the gallery, the
// sentence after an add, the walk-through entry, and the four settings of its limits. Prefix reserved for this catalog: queue.*;
// the command, selection, setting and limit texts use the keys of their existing families. Wording is count-neutral on purpose, so
// no plural variants are needed in any language.
internal static class TagQueueCatalog
{
    internal static readonly (string Key, string English, string German)[] Entries =
    {
        ("media.selection.queue", "Add to tagging queue", "Zur Tagging-Warteschlange"),
        ("media.selection.queue.hint", "Adds the selected media files to the tagging queue. Nothing is tagged by adding.", "Fügt die markierten Mediendateien der Tagging-Warteschlange hinzu. Beim Hinzufügen wird nichts getaggt."),
        ("media.queue.all", "Add all shown to the tagging queue…", "Alle angezeigten zur Tagging-Warteschlange hinzufügen…"),
        ("command.media.queue.add", "Add to tagging queue", "Zur Tagging-Warteschlange hinzufügen"),
        ("command.media.queue.open", "Walk through tagging queue", "Tagging-Warteschlange durchgehen"),
        ("queue.title", "Tagging queue", "Tagging-Warteschlange"),
        ("queue.ask", "Add {0} media files to the tagging queue?", "{0} Mediendateien zur Tagging-Warteschlange hinzufügen?"),
        ("queue.ask.yes", "Add them", "Hinzufügen"),
        ("queue.ask.no", "Cancel", "Abbrechen"),
        ("queue.result", "Added: {0} · Already in the queue: {1}", "Hinzugefügt: {0} · Schon in der Warteschlange: {1}"),
        ("queue.result.limit", "Added: {0} · Already in the queue: {1} · Not added, the queue is full: {2}. Raise “{3}” under {4}.", "Hinzugefügt: {0} · Schon in der Warteschlange: {1} · Nicht hinzugefügt, die Warteschlange ist voll: {2}. Erhöhe „{3}“ unter {4}."),
        ("queue.result.none", "Nothing was added: none of these files can be queued.", "Nichts hinzugefügt: Keine dieser Dateien lässt sich vormerken."),
        ("queue.result.skipped", "Not queueable (no saved file, or a provider medium): {0}.", "Nicht vormerkbar (keine gespeicherte Datei oder ein Anbieter-Medium): {0}."),
        ("queue.error.newer", "The tagging queue was written by a newer version of the app and stays unchanged.", "Die Tagging-Warteschlange stammt von einer neueren App-Version und bleibt unverändert."),
        ("queue.error.busy", "The app is busy with another change. Try again in a moment.", "Die App ist mit einer anderen Änderung beschäftigt. Versuche es gleich noch einmal."),
        ("queue.error.unreadable", "The tagging queue file could not be read or written. Nothing was changed.", "Die Datei der Tagging-Warteschlange ließ sich nicht lesen oder schreiben. Es wurde nichts geändert."),
        ("queue.empty", "The tagging queue is empty. Add media from the media page, the archive or the gallery.", "Die Tagging-Warteschlange ist leer. Füge Medien auf der Medienseite, im Archiv oder in der Galerie hinzu."),
        ("queue.allDone", "Nothing left to do: every entry of the tagging queue is finished or skipped.", "Nichts mehr offen: Alle Einträge der Tagging-Warteschlange sind erledigt oder übersprungen."),
        ("queue.unavailable", "None of the open entries has a saved file available right now.", "Von den offenen Einträgen ist gerade keine gespeicherte Datei verfügbar."),
        ("settings.limits.tagQueue", "Tagging queue: entries", "Tagging-Warteschlange: Einträge"),
        ("settings.limits.tagQueue.hint", "How many media files the tagging queue may hold. Lowering the limit keeps every entry and only stops adding.", "Wie viele Mediendateien die Tagging-Warteschlange aufnehmen darf. Eine niedrigere Grenze behält alle Einträge und stoppt nur das Hinzufügen."),
        ("settings.limits.tagQueueFile", "Tagging queue: file size (MiB)", "Tagging-Warteschlange: Dateigröße (MiB)"),
        ("settings.limits.tagQueueFile.hint", "Size limit of the tagging queue file. A change that would make it larger is refused; removing entries always works.", "Größengrenze der Datei der Tagging-Warteschlange. Eine Änderung, die sie größer machen würde, wird abgelehnt; Einträge entfernen geht immer."),
        ("settings.limits.tagQueueHistory", "Tagging queue: finished entries kept", "Tagging-Warteschlange: erledigte Einträge behalten"),
        ("settings.limits.tagQueueHistory.hint", "How many finished entries stay in the queue for review and undo; older finished ones drop out. 0 keeps none. Open and skipped entries are never dropped.", "Wie viele erledigte Einträge zum Nachsehen und Rückgängigmachen in der Warteschlange bleiben; ältere erledigte fallen heraus. 0 behält keinen. Offene und übersprungene Einträge fallen nie heraus."),
        ("settings.limits.tagQueueConfirm", "Tagging queue: ask above (files)", "Tagging-Warteschlange: nachfragen ab (Dateien)"),
        ("settings.limits.tagQueueConfirm.hint", "Adding more files than this at once asks first. After you confirm, every file is added.", "Werden auf einmal mehr Dateien hinzugefügt, wird vorher nachgefragt. Nach der Bestätigung werden alle Dateien hinzugefügt."),
        ("limit.tagQueueFile", "The tagging queue file has reached its size limit of {0}, so nothing new can be added. Raise “{1}” under {2}; removing entries still works.", "Die Datei der Tagging-Warteschlange hat ihre Größengrenze von {0} erreicht, es kann nichts Neues hinzukommen. Erhöhe „{1}“ unter {2}; Einträge entfernen geht weiterhin."),
        ("limit.tagQueueEntries", "The tagging queue is full (limit: {0} entries). Raise “{1}” under {2}; removing entries still works.", "Die Tagging-Warteschlange ist voll (Grenze: {0} Einträge). Erhöhe „{1}“ unter {2}; Einträge entfernen geht weiterhin.")
    };
}
