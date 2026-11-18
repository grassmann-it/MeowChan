namespace MeowChan.Desktop.Localization;

// Texts of the search-first media tag dialogs (one file and the media page selection): the one toggle list, its sections,
// the summary, the use counts, the sort choice (also used by the tag manager) and the one-step undo. Prefix reserved for this catalog: tags.dialog.*. The section "On this media file" reuses
// tags.media.assigned. The es/fr/ru pack entries are machine translations that no human has proofread.
internal static class TagDialogCatalog
{
    internal static readonly (string Key, string English, string German)[] Entries =
    {
        ("tags.dialog.list", "Own tags", "Eigene Tags"),
        ("tags.dialog.listHelp", "Up and Down move between tags, Space adds or removes the highlighted tag, Enter closes the dialog. Typing in the tag field above filters the list.",
            "Pfeil hoch und runter wechseln den Tag, die Leertaste fügt den markierten Tag hinzu oder entfernt ihn, Enter schließt den Dialog. Tippen im Tagfeld darüber filtert die Liste."),
        ("tags.dialog.listHelpMany", "Up and Down move between tags, Space gives the highlighted tag to every ticked occurrence or, if all of them have it, takes it from all. Enter closes the dialog.",
            "Pfeil hoch und runter wechseln den Tag, die Leertaste gibt den markierten Tag allen angehakten Fundstellen oder entfernt ihn von allen, wenn alle ihn schon haben. Enter schließt den Dialog."),
        ("tags.dialog.section.assignedMany", "On ticked occurrences", "An angehakten Fundstellen"),
        ("tags.dialog.section.frequent", "Most used", "Häufig verwendet"),
        ("tags.dialog.section.other", "Other tags (A–Z)", "Weitere Tags (A–Z)"),
        ("tags.dialog.section.otherByUse", "Other tags (most used first)", "Weitere Tags (häufigste zuerst)"),
        ("tags.dialog.sort", "Sort tags", "Tags sortieren"),
        ("tags.dialog.sort.name", "A–Z", "A–Z"),
        ("tags.dialog.sort.uses", "Most used", "Häufig"),
        ("tags.dialog.usesName", "used {0} times", "{0}-mal verwendet"),
        ("tags.dialog.unusedName", "not used yet", "noch nicht verwendet"),
        ("tags.dialog.section.matching", "Matching tags", "Passende Tags"),
        ("tags.dialog.row.on", "{0}: on this media file", "{0}: an diesem Medium"),
        ("tags.dialog.row.off", "{0}: not on this media file", "{0}: nicht an diesem Medium"),
        ("tags.dialog.row.all", "{0}: on all {1} ticked occurrences", "{0}: an allen {1} angehakten Fundstellen"),
        ("tags.dialog.row.some", "{0}: on {1} of {2} ticked occurrences", "{0}: an {1} von {2} angehakten Fundstellen"),
        ("tags.dialog.row.none", "{0}: on none of the ticked occurrences", "{0}: an keiner angehakten Fundstelle"),
        ("tags.dialog.carriers", "{0} of {1}", "{0} von {1}"),
        ("tags.dialog.summary", "On this media file: {0} · own tags: {1}", "An diesem Medium: {0} · eigene Tags: {1}"),
        ("tags.dialog.summaryMany", "On all ticked occurrences: {0}, on some: {1} · own tags: {2}", "An allen angehakten Fundstellen: {0}, an einigen: {1} · eigene Tags: {2}"),
        ("tags.dialog.empty", "No own tags yet. Type a name above and press Enter to create one.", "Noch keine eigenen Tags. Oben einen Namen eingeben und Enter drücken, um einen anzulegen."),
        ("tags.dialog.noMatch", "No own tag contains “{0}”. Enter adds it as a new tag.", "Kein eigener Tag enthält „{0}“. Enter legt ihn als neuen Tag an."),
        ("tags.dialog.added", "Added “{0}”.", "„{0}“ hinzugefügt."),
        ("tags.dialog.removed", "Removed “{0}”.", "„{0}“ entfernt."),
        ("tags.dialog.addedMany", "Added “{0}” to {1} occurrences.", "„{0}“ an {1} Fundstellen hinzugefügt."),
        ("tags.dialog.removedMany", "Removed “{0}” from {1} occurrences.", "„{0}“ von {1} Fundstellen entfernt."),
        ("tags.dialog.undone", "The last change was undone.", "Die letzte Änderung wurde rückgängig gemacht."),
        ("tags.dialog.undo", "Undo", "Rückgängig"),
        ("tags.dialog.undoName", "Undo the last change in this dialog", "Letzte Änderung in diesem Dialog rückgängig machen"),
    };
}
