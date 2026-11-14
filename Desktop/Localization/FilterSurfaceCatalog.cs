namespace MeowChan.Desktop.Localization;

/// <summary>
/// Texts of the filter surfaces of Archive and Media (S1/S2/S7): the tag popover ("Add tag…"), the include/exclude toggle of a
/// tag chip and the filter toggle's accessible name on the archive. Registered in BuiltInCatalog; the plural form of
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
    };
}
