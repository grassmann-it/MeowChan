namespace MeowChan.Desktop.Localization;

/// <summary>Texts of tag merging, aliases (other names), duplicate suggestions and multi-tag entry. Registered in BuiltInCatalog.</summary>
internal static class TagMergeCatalog
{
    internal static readonly (string Key, string English, string German)[] Entries =
    {
        ("tags.merge.open", "Merge…", "Zusammenführen…"),
        ("tags.merge.selectHint", "To merge, select two or more tags with Ctrl+click or Shift+click.", "Zum Zusammenführen zwei oder mehr Tags mit Strg+Klick oder Umschalt+Klick wählen."),
        ("tags.merge.target", "Merge into", "Zusammenführen zu"),
        ("tags.merge.quoted", "“{0}”", "„{0}“"),
        ("tags.merge.preview", "Media ({0}) and threads ({1}) will get “{2}”; {3} will be removed.", "Medien ({0}) und Threads ({1}) bekommen „{2}“; {3} wird entfernt."),
        ("tags.merge.note", "The removed names stay as other names of “{0}”: typing them gives “{0}”, and saved searches and the stamp keep finding it. You can undo the merge here until the tags change again; until then the tag backup file also keeps the previous state.",
            "Die entfernten Namen bleiben als weitere Namen von „{0}“: Wer sie tippt, bekommt „{0}“, und gespeicherte Suchen und der Stempel finden ihn weiter. Rückgängig machen geht hier, bis sich die Tags wieder ändern; bis dahin hält auch die Tag-Sicherungsdatei den vorigen Stand."),
        ("tags.merge.confirm", "Merge", "Zusammenführen"),
        ("tags.merge.done", "Merged into “{0}”.", "Zu „{0}“ zusammengeführt."),
        ("tags.merge.undo", "Undo merge", "Zusammenführen rückgängig"),
        ("tags.merge.undone", "Merge undone. The tags are back as before.", "Zusammenführen rückgängig gemacht. Die Tags sind wieder wie vorher."),
        ("tags.merge.error.undoStale", "The merge can no longer be undone: the tags changed in the meantime. Nothing was changed.", "Das Zusammenführen lässt sich nicht mehr rückgängig machen: Die Tags haben sich inzwischen geändert. Es wurde nichts geändert."),
        ("tags.merge.error.invalid", "Choose at least two existing tags to merge. Nothing was changed.", "Mindestens zwei vorhandene Tags zum Zusammenführen wählen. Es wurde nichts geändert."),
        ("tags.alias.title", "Other names of “{0}” (typing one gives this tag):", "Weitere Namen von „{0}“ (wer einen tippt, bekommt diesen Tag):"),
        ("tags.alias.titleNone", "“{0}” has no other names yet. Add one so that typing it gives this tag.", "„{0}“ hat noch keine weiteren Namen. Einen hinzufügen, damit er beim Tippen diesen Tag ergibt."),
        ("tags.alias.list", "Other names", "Weitere Namen"),
        ("tags.alias.name", "Other name", "Weiterer Name"),
        ("tags.alias.add", "Add name", "Namen hinzufügen"),
        ("tags.alias.remove", "Remove name", "Namen entfernen"),
        ("tags.alias.added", "Other name added.", "Weiterer Name hinzugefügt."),
        ("tags.alias.removed", "Other name removed.", "Weiterer Name entfernt."),
        ("tags.alias.error.collision", "This name already belongs to a tag or is another name of a tag. Nothing was changed.", "Dieser Name gehört schon einem Tag oder ist ein weiterer Name eines Tags. Es wurde nichts geändert."),
        ("tags.duplicates.title", "Probable duplicates (suggestions only; nothing is merged by itself)", "Wahrscheinliche Dubletten (nur Vorschläge; nichts wird von selbst zusammengeführt)"),
        ("tags.duplicates.spelling", "{0} · same name, other spelling", "{0} · gleicher Name, andere Schreibweise"),
        ("tags.duplicates.similar", "{0} · similar names", "{0} · ähnliche Namen"),
        ("tags.duplicates.merge", "Merge these…", "Diese zusammenführen…"),
        ("tags.entry.added", "Added: {0}", "Hinzugefügt: {0}"),
        ("tags.entry.mapped", "{0} (from {1})", "{0} (aus {1})"),
        ("tags.suggest.alias", "{0} → {1}", "{0} → {1}"),
        ("tags.suggest.aliasName", "{0} is another name of {1}. Choosing it adds {1}.", "{0} ist ein weiterer Name von {1}. Die Auswahl fügt {1} hinzu.")
    };
}
