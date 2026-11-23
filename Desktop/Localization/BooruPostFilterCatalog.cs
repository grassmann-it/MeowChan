namespace MeowChan.Desktop.Localization;

internal static class BooruPostFilterCatalog
{
    internal static readonly (string Key, string English, string German)[] Entries =
    {
        ("booru.filter.title", "Local filters", "Lokale Filter"),
        ("booru.filter.hint", "Only the loaded page is filtered. No provider request is sent. Watchers and “Download all of this search” keep using the full search.", "Gefiltert wird nur die geladene Seite. Es wird keine Anbieteranfrage gesendet. Suchbeobachtungen und „Alles aus dieser Suche laden“ verwenden weiterhin die vollständige Suche."),
        ("booru.filter.rating", "Rating", "Einstufung"),
        ("booru.filter.media", "Media type", "Medientyp"),
        ("booru.filter.all", "All", "Alle"),
        ("booru.filter.score", "Minimum score", "Mindest-Score"),
        ("booru.filter.tags", "Tag filters", "Tagfilter"),
        ("booru.filter.tagsHint", "Checked: require tag. Unchecked: exclude tag. Dash: ignore tag. All conditions must match; missing scores do not match a minimum score.", "Angehakt: Tag erforderlich. Leer: Tag ausschließen. Strich: Tag ignorieren. Alle Bedingungen müssen passen; fehlende Scores erfüllen keinen Mindest-Score."),
        ("booru.filter.count", "{0} of {1} visible", "{0} von {1} sichtbar"),
        ("booru.filter.empty", "No loaded results match these local filters. Reset filters to show the loaded page.", "Keine geladenen Treffer passen zu diesen lokalen Filtern. Setze die Filter zurück, um die geladene Seite anzuzeigen."),
        ("booru.filter.audio", "Audio", "Ton"),
        ("booru.filter.animation", "Animated media", "Animationen"),
        ("settings.booruPostFilterScore", "Booru: starting minimum score", "Booru: anfänglicher Mindest-Score"),
        ("settings.booruPostFilterScore.hint", "Starting score when you enable a local minimum-score filter. Default: {0}. Filtering starts disabled and is kept separately per search tab for this session.", "Anfänglicher Score beim Einschalten eines lokalen Mindest-Score-Filters. Standard: {0}. Filter sind zunächst ausgeschaltet und gelten in dieser Sitzung getrennt pro Suchtab."),
    };
}
