namespace MeowChan.Desktop.Localization;

/// <summary>Texts of the one search field with a scope (Text, File name, SHA-256) in Archive and the reader,
/// and of the saved-searches split button. Registered in BuiltInCatalog.</summary>
internal static class UnifiedSearchCatalog
{
    internal static readonly (string Key, string English, string German)[] Entries =
    {
        ("search.scope.name", "Search scope: {0}", "Suchbereich: {0}"),
        ("search.scope.tip", "Choose what the search field searches: text, captured file names or SHA-256 checksums.", "Wählen, was das Suchfeld durchsucht: Text, erfasste Dateinamen oder SHA-256-Prüfsummen."),
        ("search.scope.fieldHelp", "Search scope: {0}. {1} Open the scope menu with {2}.", "Suchbereich: {0}. {1} Bereichsmenü öffnen mit {2}."),
        ("search.scope.text", "Text", "Text"),
        ("search.scope.filename", "File name", "Dateiname"),
        ("search.scope.hash", "SHA-256", "SHA-256"),
        ("search.scope.media", "All", "Alle"),
        ("search.scope.filename.placeholder", "Captured file name…", "Erfasster Dateiname…"),
        ("search.scope.hash.placeholder", "SHA-256 (64 hex characters)…", "SHA-256 (64 Hexzeichen)…"),
        ("search.scope.filename.hint", "Searches captured original file names in the whole local archive. Enter shows the matches.", "Durchsucht erfasste Originaldateinamen im ganzen lokalen Archiv. Enter zeigt die Treffer."),
        ("search.scope.hash.hint", "Searches the saved files of the open thread for an exact SHA-256. Enter shows the matches.", "Durchsucht die gespeicherten Dateien des geöffneten Threads nach einem exakten SHA-256. Enter zeigt die Treffer."),
        ("search.scope.hash.noThread", "Open a thread with saved media to search its SHA-256 checksums.", "Einen Thread mit gespeicherten Medien öffnen, um seine SHA-256-Prüfsummen zu durchsuchen."),
        ("savedSearch.split.more", "More saved search actions", "Weitere Aktionen für gespeicherte Suchen"),
        ("search.carry.toMedia", "Search “{0}” in Media", "„{0}“ in Medien suchen"),
        ("search.carry.toArchive", "Search “{0}” in Archive", "„{0}“ im Archiv suchen"),
        ("search.carry.toThread", "Find “{0}” in the open thread", "„{0}“ im geöffneten Thread suchen"),
        ("search.carry.toMediaPlain", "Search in Media", "In Medien suchen"),
        ("search.carry.toArchivePlain", "Search in Archive", "Im Archiv suchen"),
        ("search.carry.toThreadPlain", "Find in the open thread", "Im geöffneten Thread suchen"),
        ("search.carry.empty", "Type a search first.", "Zuerst einen Suchtext eingeben."),
        ("search.carry.noThread", "Open a thread first.", "Zuerst einen Thread öffnen."),
        ("search.carry.noHash", "Media does not search SHA-256 checksums.", "Medien durchsucht keine SHA-256-Prüfsummen."),
        ("search.carry.reader", "Reader", "Reader"),
        ("search.carry.offerText", "From {0}: “{1}”. This field already has “{2}”.", "Aus {0}: „{1}“. Dieses Feld enthält schon „{2}“."),
        ("search.carry.offerUse", "Use it", "Übernehmen"),
        ("search.carry.offerKeep", "Keep current", "Aktuelle behalten"),
        ("command.scope.input.focus-diagnostics-search", "Diagnostics · message filter", "Diagnose · Meldungsfilter"),
    };
}
