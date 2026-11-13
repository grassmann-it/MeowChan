namespace MeowChan.Desktop.Localization;

/// <summary>Texts of the shared search field (Controls/SearchBox.cs). Registered in BuiltInCatalog.</summary>
internal static class SearchBoxCatalog
{
    internal static readonly (string Key, string English, string German)[] Entries =
    {
        ("search.clear", "Clear input", "Eingabe löschen"),
        ("search.count", "{0} results", "{0} Treffer"),
        ("command.scope.input.focus-media-search", "Media · file name filter", "Medien · Dateinamenfilter"),
    };
}
