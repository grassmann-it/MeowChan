namespace MeowChan.Desktop.Localization;
internal static class ExportCatalog
{
    internal static readonly (string Key, string English, string German)[] Entries =
    {
        ("export.older", "Older exports without metadata", "Ältere Exporte ohne Metadaten"),
        ("export.legacyFind", "Find older exports in target parent folder", "Ältere Exporte im Ziel-Elternordner suchen"),
        ("export.legacyAssociate", "Associate selected older export with this thread", "Gewählten älteren Export diesem Thread zuordnen"),
        ("export.legacyFound", "Found {0} source-matching candidates (at most 500 folders checked). Verify the folder before explicitly associating it.", "{0} Kandidaten mit passender Quelle gefunden (höchstens 500 Ordner geprüft). Ordner vor dem ausdrücklichen Zuordnen prüfen."),
        ("export.legacyAssociated", "Older export associated. Updating it will preserve its previous content.", "Älterer Export zugeordnet. Beim Aktualisieren bleibt sein bisheriger Inhalt erhalten."),
        ("export.hint", "Export the latest local capture to an independent HTML folder. Nothing is downloaded.", "Letzte lokale Erfassung als eigenständigen HTMLordner exportieren. Es wird nichts heruntergeladen."),
        ("export.associatedDate", "Associated", "Zugeordnet"),
        ("export.known", "This thread has previous exports. Choose a target and how to export.", "Dieser Thread wurde bereits exportiert. Ziel und Exportart wählen."),
        ("export.new", "Create new folder", "Neuen Ordner erstellen"),
        ("export.update", "Update selected export", "Gewählten Export aktualisieren"),
        ("export.previous", "Open previous export", "Früheren Export öffnen"),
        ("export.associate", "Associate export folder…", "Exportordner zuordnen …"),
        ("export.missing", "missing or unavailable", "fehlt oder nicht verfügbar"),
        ("export.preserve", "Keep the previous folder if content changes or is reduced", "Bisherigen Ordner bei Änderungen oder reduziertem Inhalt erhalten"),
        ("export.backupHint", "Updating replaces the selected export. Modified files, extra files or content missing from the new capture are preserved in a separate backup folder.", "Die Aktualisierung ersetzt den gewählten Export. Geänderte Dateien, Zusatzdateien oder im neuen Stand fehlende Inhalte bleiben in einem separaten Sicherungsordner erhalten."),
        ("export.openHint", "Available after a successful export in this dialog", "Nach erfolgreichem Export in diesem Dialog verfügbar"),
        ("export.confirm", "Select an available previous export and confirm preserving its changed or reduced content.", "Verfügbaren früheren Export wählen und den Erhalt geänderter oder reduzierter Inhalte bestätigen."),
        ("export.backup", "Previous content preserved: {0}", "Bisheriger Inhalt erhalten: {0}"),
        ("export.historyError", "Export history could not be saved or read: {0}", "Exporthistorie konnte nicht gespeichert oder gelesen werden: {0}"),
        ("export.openFailed", "Could not open the export folder: {0}", "Exportordner konnte nicht geöffnet werden: {0}"),
        ("export.changed", "The selected export changed. Close and reopen this dialog to review it.", "Der gewählte Export hat sich geändert. Dialog schließen und zur erneuten Prüfung öffnen."),
        ("export.failed", "Export did not complete. Review the error and any preserved folder path below.", "Export wurde nicht abgeschlossen. Fehler und gegebenenfalls erhaltenen Ordnerpfad unten prüfen.")
    };
}
