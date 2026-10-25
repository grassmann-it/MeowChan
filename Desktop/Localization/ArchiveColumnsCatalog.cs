namespace MeowChan.Desktop.Localization;

internal static class ArchiveColumnsCatalog
{
    internal static readonly (string Key, string English, string German)[] Entries =
    [
        ("archive.columns", "Columns", "Spalten"),
        ("archive.columnsReset", "Reset to mode defaults", "Auf Modus-Standard zurücksetzen"),
        ("archive.columns.identity", "Source / board", "Quelle / Board"),
        ("archive.columns.title", "Title / ID", "Titel / ID"),
        ("archive.columns.posts", "Posts", "Posts"),
        ("archive.columns.media", "Saved versions", "Gespeicherte Versionen"),
        ("archive.media.countLoading", "Saved media versions: counting…", "Gespeicherte Medienversionen: werden gezählt…"),
        ("archive.media.savedVersions", "Saved media versions: {0:N0}", "Gespeicherte Medienversionen: {0:N0}"),
        ("archive.media.countUnavailable", "Saved media versions: unavailable", "Gespeicherte Medienversionen: nicht verfügbar"),
        ("archive.media.countHint", "Total archived versions across the thread's recorded media folders, independent of media filters. File presence and length checked; checksums are verified when opened.", "Gesamtbestand in den erfassten Medienordnern des Threads, unabhängig von Medienfiltern. Dateiverfügbarkeit und Länge geprüft; Prüfsummen werden beim Öffnen geprüft."),
        ("archive.media.missingVersions", "Not counted: {0:N0} recorded files are missing or have changed.", "Nicht mitgezählt: {0:N0} erfasste Dateien fehlen oder haben sich verändert."),
        ("archive.media.currentProgress", "Current download work: {0}", "Aktueller Downloadauftrag: {0}"),
        ("archive.columns.source", "Source", "Quelle"),
        ("archive.columns.work", "Work", "Arbeit"),
        ("archive.columns.lastCapture", "Last capture", "Letzte Erfassung"),
        ("archive.columns.name", "File name", "Dateiname"),
        ("archive.columns.size", "Size", "Größe"),
        ("archive.columns.rating", "Rating", "Bewertung"),
        ("archive.columns.version", "Version", "Version"),
        ("archive.columns.sourceActive", "Active", "Aktiv"),
        ("archive.columns.sourceArchived", "Archived", "Archiviert"),
        ("archive.columns.sourceMissing", "Missing", "Fehlt"),
        ("archive.columns.workStopped", "Stopped", "Gestoppt"),
        ("archive.columns.workDone", "Done", "Fertig"),
        ("archive.columns.workRepair", "Repair", "Reparatur"),
        ("archive.columns.workAttention", "Attention", "Achtung"),
        ("archive.columns.workWatching", "Watching", "Beobachtung")
    ];
}
