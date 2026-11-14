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
    };
}
