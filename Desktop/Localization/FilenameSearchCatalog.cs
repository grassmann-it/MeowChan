using System;
using System.Globalization;

namespace MeowChan.Desktop.Localization;

// Root may register Entries in BuiltInCatalog later; this standalone dialog also works before that.
internal static class FilenameSearchCatalog
{
    internal static readonly (string Key, string English, string German)[] Entries =
    {
        ("filename.search.title", "Search captured filenames", "Erfasste Dateinamen suchen"),
        ("filename.search.query", "Literal filename text (0–512 characters; spaces are preserved)", "Wörtlicher Dateinamentext (0–512 Zeichen; Leerzeichen bleiben erhalten)"),
        ("filename.search.search", "Search", "Suchen"),
        ("filename.search.cancel", "Cancel search", "Suche abbrechen"),
        ("filename.search.previous", "Previous page", "Vorherige Seite"),
        ("filename.search.next", "Next page", "Nächste Seite"),
        ("filename.search.select", "Go to post of selected hit", "Zum Post des gewählten Treffers"),
        ("filename.search.close", "Close", "Schließen"),
        ("filename.search.idle", "Enter text and choose Search. Empty text matches all known names.", "Text eingeben und Suchen wählen. Leerer Text findet alle bekannten Namen."),
        ("filename.search.reading", "Reading catalog…", "Katalog wird gelesen…"),
        ("filename.search.invalid", "Use at most 512 UTF-16 characters and no NUL. Input is not trimmed.", "Höchstens 512 UTF-16-Zeichen und kein NUL verwenden. Die Eingabe wird nicht getrimmt."),
        ("filename.search.failed", "The catalog could not be read. Search again to retry.", "Der Katalog konnte nicht gelesen werden. Zum Wiederholen erneut suchen."),
        ("filename.search.canceled", "Search cancelled.", "Suche abgebrochen."),
        ("filename.search.ready", "{0} matches · {1} rows · offset {2}. Each page is a separate catalog snapshot.", "{0} Treffer · {1} Zeilen · Offset {2}. Jede Seite ist ein eigener Katalogsnapshot."),
        ("filename.search.scope", "Searches the whole local archive for captured original names, independent of the open thread and any archive filters; locally renamed files are not searched. Catalog metadata only. No files are opened. A hit does not prove current media bytes. Deleted observations remain searchable.", "Durchsucht das gesamte lokale Archiv nach erfassten Originalnamen, unabhängig vom geöffneten Thread und von Archivfiltern; lokal umbenannte Dateien werden nicht durchsucht. Nur Katalogmetadaten. Keine Dateien werden geöffnet. Ein Treffer belegt keine aktuellen Medienbytes. Gelöschte Beobachtungen bleiben suchbar."),
        ("filename.search.results", "Captured filename matches", "Treffer erfasster Dateinamen"),
        ("filename.search.unknown", "unknown", "unbekannt"),
        ("filename.search.deleted", "deleted", "gelöscht"),
        ("filename.search.notDeleted", "not marked deleted", "nicht als gelöscht markiert"),
        ("filename.search.row", "Original name claim: {0}\nSource: {1} · Board: {2} · Thread: {3} · Post: {4}\nVariant: {5} · Position: {6}\nAttachment revision: {7} · Catalog thread revision: {8}\nPost: {9} · Attachment: {10} · Current bytes: unknown", "Ursprünglicher Namensclaim: {0}\nQuelle: {1} · Board: {2} · Thread: {3} · Post: {4}\nVariante: {5} · Position: {6}\nAnhangrevision: {7} · Katalogthreadrevision: {8}\nPost: {9} · Anhang: {10} · Aktuelle Bytes: unbekannt")
    };

    internal static string Resolve(string key, string language, params object?[] args)
    {
        foreach (var entry in Entries)
            if (entry.Key == key)
                return string.Format(CultureInfo.InvariantCulture, language == "de" ? entry.German : entry.English, args);
        return key;
    }

    internal static string T(string key, params object?[] args)
    {
        // Normal Texts/custom-pack fallback once registered; own EN/DE until Root wiring exists.
        string translated = Texts.T(key, args);
        return translated == key ? Resolve(key, Texts.Language, args) : translated;
    }
}
