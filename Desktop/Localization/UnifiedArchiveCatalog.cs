namespace MeowChan.Desktop.Localization;

internal static class UnifiedArchiveCatalog
{
    internal static readonly (string Key, string English, string German)[] Entries =
    {
        ("archive.appliedFilters", "Applied:", "Angewandt:"),
        ("archive.unappliedShort", "Unapplied draft", "Nicht angewandter Entwurf"),
        ("archive.unappliedHint", "Search fields have changed. Search to apply them; the current results still use the applied search.", "Suchfelder wurden geändert. Zum Anwenden suchen; die aktuellen Treffer verwenden weiterhin die angewandte Suche."),
        ("archive.selectionPage", "{0} marked on page · Lead: #{1}", "{0} auf Seite markiert · Lead: #{1}"),
    };
}
