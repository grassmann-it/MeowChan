namespace MeowChan.Desktop.Localization;

/// <summary>Texts of the shared search field (Controls/SearchBox.cs). Registered in BuiltInCatalog.</summary>
internal static class SearchBoxCatalog
{
    internal static readonly (string Key, string English, string German)[] Entries =
    {
        ("search.clear", "Clear input", "Eingabe löschen"),
        ("search.count", "{0} results", "{0} Treffer"),
        ("settings.search.placeholder", "Search settings", "Einstellungen durchsuchen"),
        ("settings.search.results", "Settings search results", "Suchergebnisse in den Einstellungen"),
        ("settings.search.keys", "↑ ↓ choose · Enter go to setting · Esc clear", "↑ ↓ wählen · Eingabe öffnet die Einstellung · Esc leert"),
        ("settings.search.none", "No setting matches “{0}”.", "Keine Einstellung passt zu „{0}“."),
        ("settings.search.noneHint", "Check the spelling or try another word, such as “cache” or “limit”.", "Prüfe die Schreibweise oder versuche ein anderes Wort, etwa „Cache“ oder „Limit“."),
        ("command.scope.input.focus-media-search", "Media · file name filter", "Medien · Dateinamenfilter"),
    };
}
