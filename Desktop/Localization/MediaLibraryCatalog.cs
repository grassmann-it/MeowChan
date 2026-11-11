namespace MeowChan.Desktop.Localization;

/// <summary>Texts of the media page. Registered in BuiltInCatalog.</summary>
internal static class MediaLibraryCatalog
{
    internal static readonly (string Key, string English, string German)[] Entries =
    {
        ("nav.media", "Media", "Medien"),
        ("media.title", "Media", "Medien"),
        ("media.subtitle", "All saved media of all archived threads in one place.", "Alle gespeicherten Medien aller archivierten Threads an einem Ort."),
        ("media.sort", "Order", "Reihenfolge"),
        ("media.sort.thread", "By thread", "Nach Thread"),
        ("media.sort.newest", "Newest first", "Neueste zuerst"),
        ("media.sort.random", "Random", "Zufällig"),
        ("media.shuffle", "Shuffle again", "Neu mischen"),
        ("media.showMissing", "Show captured but missing files", "Erfasste, aber fehlende Dateien zeigen"),
        ("media.refresh", "Refresh", "Aktualisieren"),
        ("media.indexing", "Reading archive… {0} of {1} threads", "Archiv wird gelesen … {0} von {1} Threads"),
        ("media.count", "{0} media in {1} threads", "{0} Medien in {1} Threads"),
        ("media.countMissing", "{0} media in {1} threads, {2} missing", "{0} Medien in {1} Threads, davon {2} fehlend"),
        ("media.incomplete", "Some threads could not be read completely; their media may be missing here.", "Einige Threads konnten nicht vollständig gelesen werden; ihre Medien fehlen hier eventuell."),
        ("media.failed", "The archive could not be read. Use Refresh to try again.", "Das Archiv konnte nicht gelesen werden. Mit Aktualisieren erneut versuchen."),
        ("media.empty", "No saved media yet.", "Noch keine gespeicherten Medien."),
        ("media.notReady", "Media can be shown once the session is ready.", "Medien lassen sich anzeigen, sobald die Sitzung bereit ist."),
        ("media.tile.missing", "Not saved", "Nicht gespeichert"),
        ("media.tile.thread", "Thread: {0}", "Thread: {0}"),
        ("media.tile.unassigned", "No post", "Ohne Beitrag"),
        ("media.open", "Open in gallery", "In der Galerie öffnen"),
        ("media.showInThread", "Show in thread", "Im Thread zeigen"),
        ("media.galleryFailed", "These media could not be opened. Use Refresh and try again.", "Diese Medien konnten nicht geöffnet werden. Aktualisieren und erneut versuchen.")
    };
}
