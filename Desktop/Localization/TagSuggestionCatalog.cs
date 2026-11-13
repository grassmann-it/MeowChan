namespace MeowChan.Desktop.Localization;

// Texts of the tag field suggestions (reader tag bar, media tag dialog, selection tag dialog): own tags first, Danbooru
// source tags as a labelled group below them. Prefix reserved for this catalog: tags.suggest.*. The es/fr/ru pack entries are machine translations that no human has proofread.
internal static class TagSuggestionCatalog
{
    internal static readonly (string Key, string English, string German)[] Entries =
    {
        ("tags.suggest.label", "{0} ({1})", "{0} ({1})"),
        ("tags.suggest.used", "{0}, number of uses: {1}", "{0}, Anzahl Verwendungen: {1}"),
        ("tags.suggest.unused", "{0}, not used yet", "{0}, noch nicht verwendet"),
        ("tags.suggest.count", "{0} tag suggestions shown, at most {1}. Up and Down choose, Enter adds, Tab completes, Escape closes the list.",
            "{0} Tagvorschläge sichtbar, höchstens {1}. Pfeil hoch und runter wählen, Enter fügt hinzu, Tab vervollständigt, Escape schließt die Liste."),
        ("tags.suggest.create", "New tag: “{0}”", "Neu anlegen: „{0}“"),
        ("tags.suggest.createName", "Create the new own tag {0}", "Neuen eigenen Tag {0} anlegen"),
        ("tags.suggest.sourceGroup", "Danbooru source tags · choosing one creates an own tag", "Danbooru-Quelltags · Auswahl legt einen eigenen Tag an"),
        ("tags.suggest.sourceLabel", "{0} · Danbooru ({1})", "{0} · Danbooru ({1})"),
        ("tags.suggest.sourceName", "{0}, Danbooru source tag, saved posts with it: {1}. Choosing it creates an own tag with this name.",
            "{0}, Danbooru-Quelltag, gespeicherte Beiträge damit: {1}. Die Auswahl legt einen eigenen Tag mit diesem Namen an."),
        ("tags.suggest.hint", "Type to see matching own tags. Enter or a comma at the end adds the tag; Tab completes the highlighted suggestion.",
            "Tippen zeigt passende eigene Tags. Enter oder ein Komma am Ende fügt den Tag hinzu; Tab vervollständigt den markierten Vorschlag."),
    };
}
