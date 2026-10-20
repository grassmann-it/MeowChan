namespace MeowChan.Desktop.Localization;
internal static class CollectionExportCatalog
{
    internal static readonly (string Key, string English, string German)[] Entries =
    {
        ("export.omission.MediaNotIncluded", "Media omitted by export choice", "Medien gemäß Exportauswahl ausgelassen"),
        ("export.omission.ExcludedByRule", "Excluded by download rule", "Durch Downloadregel ausgeschlossen"),
        ("export.omission.Unavailable", "Saved media unavailable", "Gespeicherte Medien nicht verfügbar"),
        ("export.omission.DifferentVersion", "No matching saved version", "Keine passende gespeicherte Version"),
        ("export.omission.VerificationFailed", "Saved media could not be verified or read", "Gespeicherte Medien konnten nicht geprüft oder gelesen werden"),
        ("export.omission.NoMediaIdentity", "No media identity", "Keine Medienidentität"),
        ("export.collectionSelection", "Export {0} selected threads together", "{0} ausgewählte Threads gemeinsam exportieren"),
        ("export.collectionHint", "Create one independent offline folder with a landing page, individual thread pages and shared media. Uses the latest local capture of each thread; nothing is downloaded.", "Ein eigenständiger Offlineordner mit Startseite, einzelnen Threadseiten und gemeinsamen Medien. Verwendet die letzte lokale Erfassung jedes Threads; es wird nichts heruntergeladen."),
        ("export.collectionKnown", "This selection has previous collection exports. Choose a target and how to export.", "Diese Auswahl wurde bereits gemeinsam exportiert. Ziel und Exportart wählen."),
        ("export.collectionProgress", "Threads {0}/{1} · Posts {2} · Shared media files {3}", "Threads {0}/{1} · Beiträge {2} · Gemeinsame Mediendateien {3}"),
        ("export.collectionDone", "Exported {0} threads · {1} posts · {2} shared media files · {3} omissions · {4} warnings", "{0} Threads exportiert · {1} Beiträge · {2} gemeinsame Mediendateien · {3} Auslassungen · {4} Hinweise"),
        ("export.collectionMemberDone", "{0}: {1} posts · {2} omissions", "{0}: {1} Beiträge · {2} Auslassungen")
    };
}
