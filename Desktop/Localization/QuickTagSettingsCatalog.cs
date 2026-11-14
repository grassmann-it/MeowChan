namespace MeowChan.Desktop.Localization;

// Texts of the quick tagging limits on the settings page "Storage and limits" (PLAN-SCHNELL-TAGGEN §3.7, TP5): one label and
// one hint per limit and the heading of their group. Prefix: budgets.quickTag* (the page's own budgets.* family).
// Every hint takes the same three arguments: {0} default, {1} minimum, {2} maximum (from the Config constants).
// The twelve language packs add these keys in their own step; until then a language without them shows the English text.
internal static class QuickTagSettingsCatalog
{
    internal const string GroupKey = "budgets.group.quickTag";

    /// <summary>Config property, text key stem (the label is "budgets.&lt;stem&gt;", the hint "budgets.&lt;stem&gt;Hint").</summary>
    internal static readonly (string Property, string Stem)[] Limits =
    {
        (nameof(Config.QuickTagSuggestionCount), "quickTagSuggestions"),
        (nameof(Config.QuickTagRecentCount), "quickTagRecent"),
        (nameof(Config.QuickTagUndoDepth), "quickTagUndo"),
        (nameof(Config.QuickTagNeighbourScan), "quickTagNeighbours"),
        (nameof(Config.QuickTagFilenameTokens), "quickTagFilename"),
        (nameof(Config.QuickTagSourceTagsPerMedium), "quickTagSourceTags"),
        (nameof(Config.QuickTagMarkedMediaLimit), "quickTagMarked"),
    };

    internal static readonly (string Key, string English, string German)[] Entries =
    {
        (GroupKey, "Quick tagging", "Schnell-Taggen"),
        ("budgets.quickTagSuggestions", "Tag suggestions shown", "Angezeigte Tag-Vorschläge"),
        ("budgets.quickTagSuggestionsHint",
            "How many suggestion chips the quick tag field shows while it is empty. Only the first nine have a number key. Default: {0}, allowed: {1} to {2}.",
            "Wie viele Vorschlags-Chips das Schnell-Tag-Feld zeigt, solange es leer ist. Nur die ersten neun haben eine Zifferntaste. Standard: {0}, erlaubt: {1} bis {2}."),
        ("budgets.quickTagRecent", "Recently used tags remembered", "Gemerkte zuletzt verwendete Tags"),
        ("budgets.quickTagRecentHint",
            "How many recently used own tags are remembered until the app is closed and ranked higher in the suggestions. 0 switches this off. Default: {0}, allowed: {1} to {2}.",
            "Wie viele zuletzt verwendete eigene Tags bis zum Beenden der App gemerkt und in den Vorschlägen weiter oben gereiht werden. 0 schaltet das aus. Standard: {0}, erlaubt: {1} bis {2}."),
        ("budgets.quickTagUndo", "Tag undo steps", "Rückgängig-Schritte für Tags"),
        ("budgets.quickTagUndoHint",
            "How many tag changes of one tag panel can be undone one after the other. Older steps are dropped. Default: {0}, allowed: {1} to {2}.",
            "Wie viele Tag-Änderungen eines Tag-Feldes nacheinander rückgängig gemacht werden können. Ältere Schritte fallen weg. Standard: {0}, erlaubt: {1} bis {2}."),
        ("budgets.quickTagNeighbours", "Neighbouring media checked for tags", "Geprüfte Nachbarmedien für Tags"),
        ("budgets.quickTagNeighboursHint",
            "How many media entries around the shown one are checked for tags that fit. A higher value finds more but takes longer in very large lists. Default: {0}, allowed: {1} to {2}.",
            "Wie viele Medien rund um das angezeigte auf passende Tags geprüft werden. Ein höherer Wert findet mehr, braucht in sehr großen Listen aber länger. Standard: {0}, erlaubt: {1} bis {2}."),
        ("budgets.quickTagFilename", "File name words used for tags", "Dateinamen-Wörter für Tags"),
        ("budgets.quickTagFilenameHint",
            "How many words of the file name and title are compared with your own tags. 0 switches word matching off. Default: {0}, allowed: {1} to {2}.",
            "Wie viele Wörter aus Dateiname und Titel mit deinen eigenen Tags verglichen werden. 0 schaltet den Wortvergleich aus. Standard: {0}, erlaubt: {1} bis {2}."),
        ("budgets.quickTagSourceTags", "Source tags per medium", "Quelltags je Medium"),
        ("budgets.quickTagSourceTagsHint",
            "How many of the tags a source such as Danbooru lists for a medium are offered as new own tags. 0 switches them off. Default: {0}, allowed: {1} to {2}.",
            "Wie viele der Tags, die eine Quelle wie Danbooru zu einem Medium nennt, als neue eigene Tags angeboten werden. 0 schaltet sie aus. Standard: {0}, erlaubt: {1} bis {2}."),
        ("budgets.quickTagMarked", "Media marked for tagging", "Zum Taggen markierte Medien"),
        ("budgets.quickTagMarkedHint",
            "How many media can be marked in the gallery and tagged in one step. The tag file keeps its own fixed limit for assignments. Default: {0}, allowed: {1} to {2}.",
            "Wie viele Medien in der Galerie markiert und in einem Schritt getaggt werden können. Die Tag-Datei behält ihre eigene feste Grenze für Zuweisungen. Standard: {0}, erlaubt: {1} bis {2}."),
    };
}
