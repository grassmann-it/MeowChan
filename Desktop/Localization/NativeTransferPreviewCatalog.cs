namespace MeowChan.Desktop.Localization;

internal static class NativeTransferPreviewCatalog
{
    internal static readonly (string Key, string English, string German)[] Entries =
    {
        ("transfer.preview.open", "Import archive…", "Archiv importieren…"),
        ("transfer.preview.title", "Inspect archive", "Archiv prüfen"),
        // Obsolete since modals MD2: the title stands in the title bar only (E1); kept so the language packs keep one key set.
        ("transfer.preview.heading", "Archive inventory", "Archiv-Inventar"),
        ("transfer.preview.choose", "Choose archive…", "Archiv wählen…"),
        ("transfer.preview.fileType", "MeowChan archive (ZIP)", "MeowChan-Archiv (ZIP)"),
        ("transfer.preview.noSource", "No package selected.", "Kein Paket ausgewählt."),
        ("transfer.preview.source", "Selected package: {0}", "Gewähltes Paket: {0}"),
        ("transfer.preview.choosing", "Choosing package…", "Paket wird gewählt…"),
        ("transfer.preview.reading", "Reading package…", "Paket wird gelesen…"),
        ("transfer.preview.ready", "Inventory ready", "Inventar bereit"),
        ("transfer.preview.canceled", "Inspection cancelled", "Prüfung abgebrochen"),
        ("transfer.preview.closed", "Inspection closed", "Prüfung geschlossen"),
        ("transfer.preview.rejected", "Package rejected", "Paket abgelehnt"),
        ("transfer.preview.idle", "Choose an exported MeowChan archive (transfer package) to inspect.", "Ein exportiertes MeowChan-Archiv (Transferpaket) zum Prüfen wählen."),
        ("transfer.preview.inventoryOnly", "Inventory only — nothing is imported, applied, or merged.", "Nur Inventar — nichts wird importiert, übernommen oder zusammengeführt."),
        ("transfer.preview.cleanupRetained", "Cleanup did not finish; temporary inspection files may remain. No archive data was changed.", "Die Bereinigung wurde nicht abgeschlossen; temporäre Prüfdateien können zurückbleiben. Archivdaten wurden nicht geändert."),
        ("transfer.preview.summary", "Threads: {0} · posts: {1} · unique payloads: {2} · {3:N0} bytes", "Threads: {0} · Posts: {1} · eindeutige Payloads: {2} · {3:N0} Bytes"),
        ("transfer.preview.thread", "{0} /{1}/ {2} · posts: {3}\n{4}\n{5}\n{6}", "{0} /{1}/ {2} · Posts: {3}\n{4}\n{5}\n{6}"),
        ("transfer.preview.originals", "Originals: contained {0} · omitted {1} · unknown {2} · unavailable {3} · failed {4} · cancelled {5}", "Originale: enthalten {0} · ausgelassen {1} · unbekannt {2} · nicht verfügbar {3} · fehlgeschlagen {4} · abgebrochen {5}"),
        ("transfer.preview.previews", "Previews: contained {0} · omitted {1} · unknown {2} · unavailable {3} · failed {4} · cancelled {5}", "Vorschauen: enthalten {0} · ausgelassen {1} · unbekannt {2} · nicht verfügbar {3} · fehlgeschlagen {4} · abgebrochen {5}"),
        ("transfer.preview.retained", "Retained bindings: contained {0} · omitted {1} · unknown {2} · unavailable {3} · failed {4} · cancelled {5}", "Retained Bindungen: enthalten {0} · ausgelassen {1} · unbekannt {2} · nicht verfügbar {3} · fehlgeschlagen {4} · abgebrochen {5}"),
        ("transfer.preview.derivedPreviews", "Derived previews: contained {0} · omitted {1} · unknown {2} · unavailable {3} · failed {4} · cancelled {5}", "Abgeleitete Vorschauen: enthalten {0} · ausgelassen {1} · unbekannt {2} · nicht verfügbar {3} · fehlgeschlagen {4} · abgebrochen {5}"),
        ("transfer.preview.videoPosters", "Video posters (still images): contained {0} · omitted {1} · unknown {2} · unavailable {3} · failed {4} · cancelled {5}", "Videostandbilder: enthalten {0} · ausgelassen {1} · unbekannt {2} · nicht verfügbar {3} · fehlgeschlagen {4} · abgebrochen {5}"),
        ("transfer.preview.derivedNotice", "Derived previews and video posters do not replace original files. A video poster is a still image, not a playable video.", "Abgeleitete Vorschauen und Videostandbilder ersetzen keine Originaldateien. Ein Videostandbild ist ein Einzelbild, kein abspielbares Video."),
        ("transfer.preview.error.source", "The selected package source is invalid or unreadable.", "Die ausgewählte Paketquelle ist ungültig oder nicht lesbar."),
        ("transfer.preview.error.workDirectory", "The temporary inspection directory is invalid or cannot be used safely.", "Der temporäre Prüfordner ist ungültig oder kann nicht sicher verwendet werden."),
        ("transfer.preview.error.platform", "Archive inspection is not supported on this platform.", "Das Prüfen von Archiven wird auf dieser Plattform nicht unterstützt."),
        ("transfer.preview.error.package", "The archive file is invalid or unsupported.", "Die Archivdatei ist ungültig oder wird nicht unterstützt."),
        ("transfer.preview.error.private", "Private transfer metadata is not yet supported.", "Private Transfermetadaten werden noch nicht unterstützt."),
        ("transfer.preview.error.limit", "The archive file exceeds a safety limit.", "Die Archivdatei überschreitet ein Sicherheitslimit."),
        ("transfer.preview.error.read", "The archive file could not be read.", "Die Archivdatei konnte nicht gelesen werden.")
    };
}
