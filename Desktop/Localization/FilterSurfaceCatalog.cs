namespace MeowChan.Desktop.Localization;

/// <summary>
/// Texts of the filter surfaces of Archive and Media (S1/S2/S7): the tag popover ("Add tag…"), the include/exclude toggle of a
/// tag chip and the filter toggle's accessible name on the archive; since M2 also the "+ Filter" picker and the term chips of the media page. Registered in BuiltInCatalog; the plural form of
/// <c>archive.filters.countName</c> is in CountCatalog.
/// </summary>
internal static class FilterSurfaceCatalog
{
    internal static readonly (string Key, string English, string German)[] Entries =
    {
        ("tags.filter.add", "Add tag…", "Tag hinzufügen…"),
        ("tags.filter.add.search", "Search your tags", "Eigene Tags suchen"),
        ("tags.filter.add.mode", "Add as", "Hinzufügen als"),
        ("tags.filter.add.hint", "Enter adds the tag. Shift+Enter or a leading “-” excludes it.", "Enter fügt den Tag hinzu. Umschalt+Enter oder ein führendes „-“ schließt ihn aus."),
        ("tags.filter.add.noMatch", "No own tag matches “{0}”", "Kein eigener Tag passt zu „{0}“"),
        ("tags.filter.add.allUsed", "Every own tag is already in the filter", "Alle eigenen Tags sind schon im Filter"),
        ("tags.filter.toExclude", "Switch to exclude: {0}", "Auf Ausschließen umstellen: {0}"),
        ("tags.filter.toInclude", "Switch to include: {0}", "Auf Einschließen umstellen: {0}"),
        ("archive.filters.countName", "Filters and sorting, {0} active", "Filter und Sortierung, {0} aktiv"),
        // M2: the "+ Filter" picker, the term chips and the section combos' summaries.
        ("media.filter.add", "Add filter", "Filter hinzufügen"),
        ("media.filter.addChip", "Filter", "Filter"),
        ("media.filter.add.help", "Opens a list of every filter. Type to search; Enter includes, Shift+Enter or a leading “-” excludes, a comma adds and keeps the list open.", "Öffnet eine Liste aller Filter. Tippen sucht; Enter schließt ein, Umschalt+Enter oder ein führendes „-“ schließt aus, ein Komma fügt hinzu und lässt die Liste offen."),
        ("media.filter.add.search", "Search filters", "Filter suchen"),
        ("media.filter.add.hint", "Enter includes. Shift+Enter or a leading “-” excludes. A comma adds and keeps the list open.", "Enter schließt ein. Umschalt+Enter oder ein führendes „-“ schließt aus. Ein Komma fügt hinzu und lässt die Liste offen."),
        ("media.filter.add.noMatch", "No filter matches “{0}”", "Kein Filter passt zu „{0}“"),
        ("media.filter.add.allUsed", "Every filter is already set", "Alle Filter sind schon gesetzt"),
        ("media.filter.add.cannotExclude", "“{0}” cannot be excluded", "„{0}“ lässt sich nicht ausschließen"),
        ("media.filter.add.refused", "This filter cannot be combined with the current ones.", "Dieser Filter lässt sich nicht mit den aktuellen kombinieren."),
        ("media.filter.group.tag", "Own tags", "Eigene Tags"),
        ("media.filter.group.flags", "Other filters", "Weitere Filter"),
        ("media.filter.kindExcludeChip", "Without type: {0}", "Ohne Typ: {0}"),
        ("media.filter.sourceExcludeChip", "Without source: {0}", "Ohne Quelle: {0}"),
        ("media.filter.boardExcludeChip", "Without board: {0}", "Ohne Board: {0}"),
        ("media.filter.kind.count", "{0} types", "{0} Typen"),
        ("media.filter.combo.excluded", "Excluded: {0}", "Ausgeschlossen: {0}"),
        ("media.filter.chip.help", "A click or Enter switches between include and exclude; Delete removes the filter.", "Klick oder Enter wechselt zwischen Ein- und Ausschließen; Entf entfernt den Filter."),
        ("media.filter.chip.remove", "Remove", "Entfernen"),
        ("media.filter.chip.edit", "Edit…", "Bearbeiten…"),
        // M3: the query syntax of the media search field (Desktop/MediaFilterQuery.cs), its hints and the field aliases.
        ("media.query.converted", "Added from the search field: {0}", "Aus dem Suchfeld übernommen: {0}"),
        ("media.query.kept", "Kept as text: {0}", "Als Text belassen: {0}"),
        ("media.query.kept.item", "“{0}” ({1})", "„{0}“ ({1})"),
        ("media.query.reason.unknownField", "unknown field", "unbekanntes Feld"),
        ("media.query.reason.unknownValue", "unknown value", "unbekannter Wert"),
        ("media.query.reason.needsValue", "needs a value", "braucht einen Wert"),
        ("media.query.reason.cannotExclude", "cannot be excluded", "lässt sich nicht ausschließen"),
        ("media.query.reason.ambiguous", "ambiguous, name the source and board", "mehrdeutig, Quelle und Board nennen"),
        ("media.query.reason.refused", "does not fit the other filters", "passt nicht zu den anderen Filtern"),
        ("media.query.reason.tooManyTags", "too many tags", "zu viele Tags"),
        ("media.query.tip", "Words: file name, thread, board, source, tag.\nin:tag,title looks only in those fields.\ntype:video, -tag:name, sound:none\nsource:4chan, board:g, rating:>=3\nfav, missing, untagged, thread:1234,\nqueue:yes, queue:no, booru:tag, -booru:tag, tag:\"x\" keeps spaces.", "Wörter: Dateiname, Thread, Board, Quelle, Tag.\nin:tag,title sucht nur in diesen Feldern.\ntype:video, -tag:name, sound:none\nsource:4chan, board:g, rating:>=3\nfav, missing, untagged, thread:1234,\nqueue:yes, queue:no, booru:tag, -booru:tag, tag:\"x\" hält Leerzeichen zusammen."),
        ("media.query.copied", "Filters copied as text", "Filter als Text kopiert"),
        ("media.query.copiedLong", "Filters copied as text. The text is longer than the search field takes (character limit: {0}).", "Filter als Text kopiert. Der Text ist länger, als das Suchfeld aufnimmt (Zeichengrenze: {0})."),
        ("media.query.copyFailed", "The filters could not be copied: the clipboard is not available.", "Die Filter konnten nicht kopiert werden: Die Zwischenablage ist nicht verfügbar."),
        ("media.filter.chip.copyText", "Copy filters as text", "Filter als Text kopieren"),
        ("media.query.alias.tag", "tags label", "tags schlagwort"),
        ("media.query.alias.type", "kind filetype", "typ art"),
        ("media.query.alias.sound", "audio", "ton audio"),
        ("media.query.alias.source", "site", "quelle"),
        ("media.query.alias.board", "boards", "boards brett"),
        ("media.query.alias.rating", "stars", "bewertung sterne"),
        ("media.query.alias.fav", "favorite favourite", "favorit favoriten"),
        ("media.query.alias.missing", "lost", "fehlend"),
        ("media.query.alias.untagged", "notags", "ungetaggt"),
        ("media.query.alias.thread", "threads", "threads faden"),
        ("media.query.alias.name", "file filename", "datei dateiname"),
        // Where the free text is looked for (in:tag,title): the field's own names and the names of its values. The English keys always work.
        ("media.query.alias.in", "within where", "in wo"),
        ("media.query.scope.alias.name", "file filename files", "datei dateiname dateien"),
        ("media.query.scope.alias.title", "titles thread threads", "titel threadtitel thread faden"),
        ("media.query.scope.alias.board", "boards", "boards brett"),
        ("media.query.scope.alias.source", "sources site", "quelle quellen"),
        ("media.query.scope.alias.tag", "tags label", "tags schlagwort"),
        // M4: the focus mode of the media page (MediaPage.Focus.cs). The command label itself is command.media.focus-mode (CommandLabelCatalog).
        ("media.focus.label", "Focus", "Fokus"),
        ("media.focus.exit", "Exit focus", "Fokus beenden"),
        ("media.focus.exit.tip", "Exit focus mode and show the toolbar and filters again · Esc", "Fokusmodus beenden und Werkzeugleiste und Filter wieder zeigen · Esc"),
        ("media.focus.on", "Focus mode on", "Fokusmodus an"),
        ("media.focus.off", "Focus mode off", "Fokusmodus aus"),
        // Focus mode, full screen: the hover strip at the top edge and the setting that lets the mode hide the application header too.
        ("media.focus.headerStrip", "Show the app header", "App-Kopfzeile anzeigen"),
        ("settings.mediaFocusHidesHeader", "Hide the app header in the Media focus mode", "App-Kopfzeile im Fokusmodus der Medienseite ausblenden"),
        ("settings.mediaFocusHidesHeaderHint", "The focus mode of the Media page hides the page title, the toolbar and the footer navigation. With this option it also hides the app header with its monitor and buttons. Move the pointer to the thin strip at the top edge to show the header and the window buttons. Esc or “Exit focus” brings everything back.",
            "Der Fokusmodus der Medienseite blendet Seitentitel, Werkzeugleiste und Fußnavigation aus. Mit dieser Option blendet er auch die App-Kopfzeile mit Monitor und Knöpfen aus. Bewege den Zeiger an den dünnen Streifen am oberen Rand, um Kopfzeile und Fensterknöpfe zu zeigen. Esc oder „Fokus beenden ×“ bringt alles zurück."),
        // M5: the two states of the tag mode switch (the sentences are tags.filter.matchAll / matchAny); the section labels reuse the field texts.
        ("media.filter.match.all", "all", "alle"),
        ("media.filter.match.any", "any", "einer"),
    };
}
