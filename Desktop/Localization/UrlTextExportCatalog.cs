namespace MeowChan.Desktop.Localization;

// Register Entries through BuiltInCatalog. Keep normal Texts fallback and live refresh;
// this data-only catalog neither grants export consent nor starts a client.
internal static class UrlTextExportCatalog
{
    internal static readonly (string Key, string English, string German)[] Entries =
    {
        ("urltext.export.open", "Export page URL as text…", "Seiten-URL als Text exportieren…"),
        ("urltext.export.title", "Export archived thread page URL as text", "Seiten-URL eines archivierten Threads als Text exportieren"),
        ("urltext.export.scope", "Scope: one saved archive thread, one page URL. The confirmed target stays fixed even if the selection changes.", "Umfang: ein gespeicherter Archivthread, eine Seiten-URL. Das bestätigte Ziel bleibt auch bei geänderter Auswahl fest."),
        ("urltext.export.source", "Source: {0}", "Quelle: {0}"),
        ("urltext.export.board", "Board: {0}", "Board: {0}"),
        ("urltext.export.thread", "Thread ID: {0}", "Thread-ID: {0}"),
        ("urltext.export.url", "Page URL: {0}", "Seiten-URL: {0}"),
        ("urltext.export.excluded", "Only the page URL is exported: no media, tags, favorites, notes, jobs or credentials.", "Exportiert wird nur die Seiten-URL: keine Medien, Tags, Favoriten, Notizen, Jobs oder Zugangsdaten."),
        ("urltext.export.privacy", "A URL file can reveal your reading interests. Share it only with recipients you trust.", "Eine URL-Datei kann deine Leseinteressen offenlegen. Teile sie nur mit vertrauenswürdigen Empfängern."),
        ("urltext.export.noClient", "MeowChan starts no client, creates no jobs and downloads nothing. The target program decides whether to accept the URL and how to authenticate or download.", "MeowChan startet keinen Client, erstellt keine Jobs und lädt nichts herunter. Das Zielprogramm entscheidet über URL-Annahme, Authentifizierung und Download."),
        ("urltext.export.availability", "This file contains no offline content and does not guarantee that the page is reachable or supported by the target program.", "Diese Datei enthält keine Offlineinhalte und garantiert weder die Erreichbarkeit der Seite noch deren Unterstützung durch das Zielprogramm."),
        ("urltext.export.save", "Save URL file…", "Linkdatei speichern…"),
        ("urltext.export.cancel", "Cancel", "Abbrechen"),
        ("urltext.export.close", "Close", "Schließen"),
        ("urltext.export.fileType", "Page URL text file (UTF-8)", "Seiten-URL-Textdatei (UTF-8)"),
        ("urltext.export.choosing", "Choosing a new local file… Existing files will not be overwritten.", "Neue lokale Datei wählen… Bestehende Dateien werden nicht überschrieben."),
        ("urltext.export.writing", "Saving the confirmed page URL… Cancellation after writing starts is best effort; a file may remain.", "Bestätigte Seiten-URL wird gespeichert… Abbruch nach Schreibbeginn erfolgt nach bestem Bemühen; eine Datei kann zurückbleiben."),
        ("urltext.export.canceling", "Cancellation requested; waiting for the write outcome. A file may remain or saving may still complete.", "Abbruch angefordert; das Schreibergebnis wird abgewartet. Eine Datei kann zurückbleiben oder das Speichern noch erfolgreich enden."),
        ("urltext.export.saved", "Page URL file saved. Page reachability and target program support have not been verified.", "Seiten-URL-Datei gespeichert. Erreichbarkeit und Unterstützung durch das Zielprogramm wurden nicht geprüft."),
        ("urltext.export.canceled", "Export canceled before file creation; no file was written.", "Export vor der Dateierstellung abgebrochen; es wurde keine Datei geschrieben."),
        ("urltext.export.partialRetained", "Saving did not complete. A possibly incomplete file may remain; it was not automatically deleted. Check the chosen destination before trying again.", "Speichern nicht abgeschlossen. Eine möglicherweise unvollständige Datei kann zurückgeblieben sein; sie wurde nicht automatisch gelöscht. Prüfe das gewählte Ziel vor einem neuen Versuch."),
        ("urltext.export.creationUncertain", "File creation could not be confirmed. A file may exist at the chosen destination; it was not automatically deleted. Check it before trying again.", "Die Dateierstellung konnte nicht bestätigt werden. Am gewählten Ziel kann eine Datei existieren; sie wurde nicht automatisch gelöscht. Prüfe sie vor einem neuen Versuch."),
        ("urltext.export.error.selection", "Select exactly one saved archive thread to export its page URL.", "Wähle genau einen gespeicherten Archivthread aus, um seine Seiten-URL zu exportieren."),
        ("urltext.export.error.summary", "The saved thread summary could not be read. No page URL was reconstructed.", "Die gespeicherte Threadübersicht konnte nicht gelesen werden. Es wurde keine Seiten-URL rekonstruiert."),
        ("urltext.export.error.invalidUrl", "The saved page URL is invalid, unsupported or does not match the selected source, board and thread. Nothing was exported.", "Die gespeicherte Seiten-URL ist ungültig, nicht unterstützt oder passt nicht zu gewählter Quelle, Board und Thread. Es wurde nichts exportiert."),
        ("urltext.export.error.target", "Choose a new local file in an existing permitted folder. This destination cannot be used safely.", "Wähle eine neue lokale Datei in einem vorhandenen zulässigen Ordner. Dieses Ziel kann nicht sicher verwendet werden."),
        ("urltext.export.error.exists", "A file already exists at this destination. It was not overwritten; choose a new file name.", "Am Ziel existiert bereits eine Datei. Sie wurde nicht überschrieben; wähle einen neuen Dateinamen."),
        ("urltext.export.error.write", "The page URL file could not be saved. Check the reported file outcome before trying again.", "Die Seiten-URL-Datei konnte nicht gespeichert werden. Prüfe das angezeigte Dateiergebnis vor einem neuen Versuch."),
        ("urltext.export.error.busy", "Another archive operation or URL export dialog is still active. Finish or close it first.", "Eine andere Archivoperation oder ein URL-Exportdialog ist noch aktiv. Beende oder schließe diese zuerst.")
    };
}
