namespace MeowChan.Desktop.Localization;

/// <summary>Texts of the archive's relevance order, "Search in" scope and match groups (ArchivePage.Relevance.cs). Registered in BuiltInCatalog.</summary>
internal static class ArchiveRelevanceCatalog
{
    internal static readonly (string Key, string English, string German)[] Entries =
    {
        ("archive.relevanceSort", "Relevance", "Relevanz"),
        ("savedSearch.sort.Relevance", "Relevance", "Relevanz"),
        ("archive.filter.textScope", "Search in", "Suchen in"),
        ("archive.filter.textScopeHint", "Where the search text must occur. The title also covers the board and the thread number.", "Wo der Suchtext vorkommen muss. Der Titel umfasst auch Board und Threadnummer."),
        ("archive.textScope.title", "Title", "Titel"),
        ("archive.textScope.titleAndOpening", "Title and opening post", "Titel und Eröffnungspost"),
        ("archive.textScope.allPosts", "All posts", "Alle Posts"),
        ("archive.textScopeChip", "Search in: {0}", "Suchen in: {0}"),
        ("archive.matchGroup.title", "In title · {0}", "Im Titel · {0}"),
        ("archive.matchGroup.opening", "In opening post · {0}", "Im Eröffnungspost · {0}"),
        ("archive.matchGroup.other", "In other posts · {0}", "In anderen Posts · {0}"),
        ("archive.foundIn.opening", "Opening post", "Eröffnungspost"),
        ("archive.foundIn.other", "Other posts", "Andere Posts"),
        ("archive.columns.foundIn", "Found in", "Gefunden in"),
    };
}
