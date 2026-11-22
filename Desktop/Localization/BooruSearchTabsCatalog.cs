namespace MeowChan.Desktop.Localization;

/// <summary>
/// EN/DE texts of the Danbooru search tabs, of "Download all of this search" and of the setting that limits the tabs. The counts of the
/// bulk result are labels (not sentences with a counted noun), so no plural forms are needed.
/// </summary>
internal static class BooruSearchTabsCatalog
{
    internal static readonly (string Key, string English, string German)[] Entries =
    {
        ("booru.tabs.watchers", "Watchers", "Suchbeobachtungen"),
        ("booru.tabs.searchNew", "Search in new tab", "In neuem Tab suchen"),
        ("booru.tabs.searchCurrent", "Search in current tab", "Im aktuellen Tab suchen"),
        ("booru.tabs.command.submit", "Submit Booru search", "Booru-Suche starten"),
        ("booru.tabs.command.alternate", "Submit Booru search in the other tab mode", "Booru-Suche mit dem anderen Tab-Ziel starten"),
        ("settings.booru.newSearchTarget", "New search opens in", "Neue Suche öffnet"),
        ("settings.booru.newSearchTarget.hint", "Where the Booru search field opens a new search.", "Legt fest, wo eine neue Suche aus dem Booru-Suchfeld geöffnet wird."),
        ("settings.booru.newSearchTarget.new", "New tab", "Neuer Tab"),
        ("settings.booru.newSearchTarget.current", "Current tab", "Aktueller Tab"),
        ("booru.tabs.new", "New search tab", "Neuer Such-Tab"),
        ("booru.tabs.openCurrent", "Open in new tab", "In neuem Tab öffnen"),
        ("booru.tabs.openCurrentHint", "Copies the shown search and its results into a new tab. Nothing is sent.", "Kopiert die angezeigte Suche samt Ergebnissen in einen neuen Tab. Es wird nichts gesendet."),
        ("booru.tabs.close", "Close tab", "Tab schließen"),
        ("booru.tabs.closeFor", "Close tab “{0}”", "Tab „{0}“ schließen"),
        ("booru.tabs.blank", "New search", "Neue Suche"),
        ("booru.tabs.post", "Post #{0}", "Post #{0}"),
        ("booru.tabs.limit", "The limit of search tabs ({0}) is reached. Close a tab first or raise the limit in Settings.", "Das Limit für Such-Tabs ({0}) ist erreicht. Schließe zuerst einen Tab oder erhöhe das Limit in den Einstellungen."),
        ("settings.limits.booruSearchTabs", "Danbooru: most search tabs", "Danbooru: höchstens so viele Such-Tabs"),
        ("settings.limits.booruSearchTabs.hint", "Maximum number of additional search tabs in Danbooru. Tabs are kept for the current session.", "Höchstzahl zusätzlicher Such-Tabs in Danbooru. Tabs bleiben für die aktuelle Sitzung geöffnet."),
        ("booru.bulk.button", "Download all of this search", "Alles aus dieser Suche laden"),
        ("booru.bulk.stop", "Stop collecting", "Sammeln beenden"),
        ("booru.bulk.hint", "Fetches the existing results of the shown search and queues each original, up to the source's page limit (pages {0}, posts per page {1}). The watcher only takes new posts.", "Holt die vorhandenen Ergebnisse der angezeigten Suche und reiht jedes Original ein, höchstens bis zum Seitenlimit der Quelle (Seiten {0}, Posts je Seite {1}). Die Suchbeobachtung übernimmt nur neue Posts."),
        ("booru.bulk.needsSearch", "Run a tag search first; this fetches the results of the search that is shown.", "Führe zuerst eine Tag-Suche aus; geladen werden die Ergebnisse der angezeigten Suche."),
        ("booru.bulk.running", "Collecting results of “{0}”: pages read {1} of at most {2}, posts found {3}, queued {4}.", "Ergebnisse von „{0}“ werden gesammelt: gelesene Seiten {1} von höchstens {2}, gefundene Posts {3}, eingereiht {4}."),
        ("booru.bulk.result", "Download all of “{0}”: posts found {1}, pages read {2} of at most {3}. Queued {4}, already queued {5}, already saved {6}, no original offered {7}, failed {8}.", "Alles aus „{0}“: gefundene Posts {1}, gelesene Seiten {2} von höchstens {3}. Eingereiht {4}, schon in der Warteschlange {5}, schon gespeichert {6}, ohne angebotenes Original {7}, fehlgeschlagen {8}."),
        ("booru.bulk.endLimit", "The source's page limit was reached; the search may have more results that cannot be listed.", "Das Seitenlimit der Quelle wurde erreicht; die Suche kann weitere Ergebnisse haben, die sich nicht auflisten lassen."),
        ("booru.bulk.endEnd", "The source reported no further results after these.", "Die Quelle meldete nach diesen keine weiteren Ergebnisse."),
        ("booru.bulk.endBlocked", "The source stopped answering (pause, rate limit, offline or an error); the rest was not listed. Try again later.", "Die Quelle antwortete nicht mehr (Pause, Ratenlimit, offline oder Fehler); der Rest wurde nicht aufgelistet. Versuche es später erneut."),
        ("booru.bulk.endCanceled", "Stopped by you; the rest was not listed. What was queued stays queued.", "Von dir beendet; der Rest wurde nicht aufgelistet. Was eingereiht wurde, bleibt in der Warteschlange."),
        ("booru.bulk.otherTab", "Collecting results for “{0}”. Open its search tab to stop collecting.", "Ergebnisse für „{0}“ werden gesammelt. Öffne zum Beenden den zugehörigen Such-Tab."),
    };
}
