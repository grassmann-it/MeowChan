namespace MeowChan.Desktop.Localization;

internal static class UnifiedArchiveCatalog
{
    internal static readonly (string Key, string English, string German)[] Entries =
    {
        ("archive.appliedFilters", "Applied:", "Angewandt:"),
        ("archive.unappliedShort", "Unapplied draft", "Nicht angewandter Entwurf"),
        ("archive.unappliedHint", "Search fields have changed. Search to apply them; the current results still use the applied search.", "Suchfelder wurden geändert. Zum Anwenden suchen; die aktuellen Treffer verwenden weiterhin die angewandte Suche."),
        ("archive.applyFilters", "Apply filters", "Filter anwenden"),
        ("archive.filterDraftNote", "Unapplied draft: the results still use the applied search.", "Nicht angewandter Entwurf: Die Treffer verwenden weiterhin die angewandte Suche."),
        ("archive.selectionPage", "{0} marked on this page · anchor: #{1}", "{0} auf dieser Seite markiert · Anker: #{1}"),
    };
}
