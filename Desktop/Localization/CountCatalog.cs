namespace MeowChan.Desktop.Localization;

/// <summary>
/// Singular forms for Texts.Count. The base key (in the catalog that owns the text) is the general plural;
/// <c>key.one</c> is used for a count of exactly one. Languages with more forms add optional
/// <c>key.few</c>-style keys in their packs; a missing form falls back to the base key.
/// </summary>
internal static class CountCatalog
{
    internal static readonly (string Key, string English, string German)[] Entries =
    {
        ("archive.postsCount.one", "{0} captured post", "{0} erfasster Post"),
        ("archive.captured.one", "Last capture: {0} · {1} post", "Letzte Erfassung: {0} · {1} Post"),
        ("archive.delete.deleted.one", "Deleted {0} thread.", "{0} Thread gelöscht."),
        ("archive.delete.ratings.one", "{0} media rating will be removed.", "{0} Medienbewertung wird entfernt."),
        ("archive.delete.summary.one", "Permanently delete {0} thread and {1} local files ({2:N0} bytes)?", "{0} Thread und {1} lokale Dateien ({2:N0} Bytes) endgültig löschen?"),
        ("media.filters.countName.one", "Filters, {0} active", "Filter, {0} aktiv"),
        ("media.count.one", "{0} media in {1} thread", "{0} Medien in {1} Thread"),
        ("media.countMissing.one", "{0} media in {1} thread, {2} missing", "{0} Medien in {1} Thread, davon {2} fehlend"),
        ("downloads.searchCount.one", "{0} result", "{0} Treffer"),
        ("downloads.searchActive.one", "{0} result · {1}", "{0} Treffer · {1}"),
        ("search.count.one", "{0} result", "{0} Treffer"),
        ("priority.highAccepted.one", "High priority set for {0} thread. It affects the next eligible slots; existing files are not restarted and source limits still apply.", "Hohe Priorität für {0} Thread gesetzt. Sie gilt für nächste freie Plätze; laufende Dateien werden nicht neu gestartet und Quellenlimits gelten weiter."),
        ("priority.normalAccepted.one", "Normal priority restored for {0} thread.", "Normale Priorität für {0} Thread wiederhergestellt."),
        ("work.retryAccepted.one", "Requested one retry for {0} media file. Queue and source limits still apply.", "Ein Wiederholungsversuch für {0} Mediendatei angefordert. Warteschlange und Quellenlimits gelten weiterhin."),
        ("work.retryPaused.one", "Requested one retry for {0} media file. Transfers wait until the session resumes; source limits still apply.", "Ein Wiederholungsversuch für {0} Mediendatei angefordert. Transfers warten auf das Fortsetzen der Sitzung; Quellenlimits gelten weiterhin."),
        ("work.errorSummary.one", "{0} media error", "{0} Medienfehler"),
        ("drop.files.title.one", "Drop to import {0} file or folder", "Loslassen, um {0} Datei oder Ordner zu importieren"),
        ("import.start.one", "Import {0} file", "{0} Datei importieren"),
        ("import.done.one", "Imported {0} file into “{1}”.", "{0} Datei in „{1}“ importiert."),
        ("import.done.linked.one", "{0} was already there and is only linked.", "{0} war schon vorhanden und ist nur verknüpft."),
        ("import.done.failed.one", "{0} could not be copied (changed or unreadable).", "{0} konnte nicht kopiert werden (geändert oder nicht lesbar)."),
        ("tags.dialog.addedMany.one", "Added “{0}” to {1} occurrence.", "„{0}“ an {1} Fundstelle hinzugefügt."),
        ("tags.dialog.removedMany.one", "Removed “{0}” from {1} occurrence.", "„{0}“ von {1} Fundstelle entfernt.")
    };
}
