namespace MeowChan.Desktop.Localization;

internal static class PostBookmarkCatalog
{
    internal static readonly (string Key, string English, string German)[] Entries =
    {
        ("bookmarks.title", "Post bookmarks", "Post-Lesezeichen"),
        ("bookmarks.edit", "Add / edit post bookmark…", "Post-Lesezeichen hinzufügen / bearbeiten …"),
        ("bookmarks.note", "Your note (plain text, up to 8,000 Unicode codepoints)", "Eigene Notiz (Klartext, bis 8.000 Unicode-Codepoints)"),
        ("bookmarks.save", "Save bookmark", "Lesezeichen speichern"),
        ("bookmarks.remove", "Remove bookmark…", "Lesezeichen entfernen …"),
        ("bookmarks.open", "Open exact local post", "Exakten lokalen Post öffnen"),
        ("bookmarks.empty", "No bookmarks. Add one from a local reader post.", "Keine Lesezeichen. Ein Lesezeichen am lokalen Readerpost hinzufügen."),
        ("bookmarks.select", "Select a bookmark to check local availability and edit its note.", "Lesezeichen auswählen, um die lokale Verfügbarkeit zu prüfen und die Notiz zu bearbeiten."),
        ("bookmarks.available", "Locally available · {0}", "Lokal verfügbar · {0}"),
        ("bookmarks.missing", "This exact post is not available locally. Its reference and your note are retained. No source request is started.", "Dieser exakte Post ist lokal nicht verfügbar. Referenz und eigene Notiz bleiben erhalten. Es wird kein Quellenabruf gestartet."),
        ("bookmarks.failed", "Post bookmarks: {0}", "Post-Lesezeichen: {0}"),
        ("bookmarks.retry", "Reload / retry", "Neu laden / wiederholen"),
        ("bookmarks.discard", "Discard your note changes?", "Änderungen an der eigenen Notiz verwerfen?"),
        ("bookmarks.keepEditing", "Continue editing", "Weiter bearbeiten"),
        ("bookmarks.discardConfirm", "Discard changes", "Änderungen verwerfen"),
        ("bookmarks.deleteExplain", "Remove this bookmark, its saved note and any unsaved note changes? The archived post remains. This feature keeps no note backups; external backups and filesystem journals are outside this action.", "Dieses Lesezeichen samt eigener gespeicherter Notiz und ungespeicherten Änderungen entfernen? Der Archivpost bleibt erhalten. Diese Funktion führt keine Notizsicherungen; externe Backups und Dateisystemjournale liegen außerhalb dieser Aktion."),
        ("bookmarks.conflict", "Bookmarks changed. Your draft is retained. Reload before saving. A removed bookmark will not be recreated automatically.", "Lesezeichen wurden geändert. Der Entwurf bleibt erhalten. Vor dem Speichern neu laden. Ein entferntes Lesezeichen wird nicht automatisch neu angelegt."),
        ("bookmarks.removedDraft", "This bookmark was removed elsewhere. Your draft is retained here; it cannot recreate the removed entry.", "Dieses Lesezeichen wurde anderswo entfernt. Der Entwurf bleibt hier erhalten; er kann den entfernten Eintrag nicht neu anlegen."),
        ("bookmarks.retained", "Post bookmarks and your notes remain. Deleted posts can no longer be read locally through them. Remove those entries under Post bookmarks if needed.", "Post-Lesezeichen und eigene Notizen bleiben erhalten; gelöschte Posts sind darüber nicht mehr lokal lesbar. Entfernen Sie diese Einträge bei Bedarf unter Post-Lesezeichen."),
        ("bookmarks.loading", "Checking local post…", "Lokaler Post wird geprüft …"),
        ("bookmarks.invalidNote", "The note must contain valid Unicode, at most 8,000 codepoints, and no control characters except tab and line breaks.", "Die Notiz muss gültiges Unicode enthalten, höchstens 8.000 Codepoints und keine Steuerzeichen außer Tab und Zeilenumbrüchen."),
        ("bookmarks.saved", "Bookmark saved.", "Lesezeichen gespeichert."),
        ("bookmarks.busy", "Saving…", "Wird gespeichert …")
    };
}
