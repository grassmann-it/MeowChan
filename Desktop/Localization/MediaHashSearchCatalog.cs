using System;
using System.Globalization;

namespace MeowChan.Desktop.Localization;

// Root will concatenate Entries into BuiltInCatalog; local fallback keeps this standalone
// dialog usable beforehand. No language mutation or private pack loading here.
internal static class MediaHashSearchCatalog
{
    internal static readonly (string Key, string English, string German)[] Entries =
    {
        ("mediahash.title", "SHA-256 search in captured media inventory", "SHA-256-Suche im erfassten Medieninventar"),
        ("mediahash.scope", "One captured inventory only, not the global library. Matches are recorded SHA-256 metadata, not verification of current files. No files are opened or rehashed; nothing is downloaded.", "Nur ein erfasstes Inventar, nicht die gesamte Bibliothek. Treffer beruhen auf gespeicherten SHA-256-Metadaten, nicht auf einer Prüfung aktueller Dateien. Keine Dateien werden geöffnet oder neu gehasht; es wird nichts heruntergeladen."),
        ("mediahash.input", "Exact SHA-256 (64 ASCII hex characters)", "Exakter SHA-256 (64 ASCII-Hexzeichen)"),
        ("mediahash.search", "Search captured inventory", "Erfasstes Inventar durchsuchen"),
        ("mediahash.close", "Close", "Schließen"),
        ("mediahash.previous", "Previous 100", "Vorherige 100"),
        ("mediahash.next", "Next 100", "Nächste 100"),
        ("mediahash.ready", "Enter a complete SHA-256 and explicitly start the search.", "Vollständigen SHA-256 eingeben und die Suche ausdrücklich starten."),
        ("mediahash.invalid", "Use exactly 64 ASCII hex characters. Spaces, prefixes, MD5 and shortened hashes are not accepted.", "Genau 64 ASCII-Hexzeichen verwenden. Leerzeichen, Präfixe, MD5 und verkürzte Hashes werden nicht akzeptiert."),
        ("mediahash.searching", "Searching this captured inventory…", "Dieses erfasste Inventar wird durchsucht…"),
        ("mediahash.missing", "The captured media manifest is missing. Matches and measurement availability are unknown.", "Das erfasste Medienmanifest fehlt. Treffer und vorhandene Messwerte sind unbekannt."),
        ("mediahash.corrupt", "The captured media manifest is corrupt. Matches and measurement availability are unknown.", "Das erfasste Medienmanifest ist beschädigt. Treffer und vorhandene Messwerte sind unbekannt."),
        ("mediahash.noMeasurements", "No complete SHA-256 measurement is recorded in this captured inventory. No measurement was started.", "In diesem erfassten Inventar ist kein vollständiger SHA-256-Messwert gespeichert. Es wurde keine Messung gestartet."),
        ("mediahash.noMatches", "No exact match in this captured inventory. This says nothing about other inventories or current files.", "Kein exakter Treffer in diesem erfassten Inventar. Dies sagt nichts über andere Inventare oder aktuelle Dateien aus."),
        ("mediahash.matches", "Exact matches: {0}. Select a reference explicitly; no navigation happens automatically.", "Exakte Treffer: {0}. Referenz ausdrücklich auswählen; es erfolgt keine automatische Navigation."),
        ("mediahash.page", "Showing {0}–{1} of {2}", "Anzeige {0}–{1} von {2}"),
        ("mediahash.generation", "Captured generation: {0}", "Erfasste Generation: {0}"),
        ("mediahash.unavailable", "unavailable", "nicht verfügbar"),
        ("mediahash.omitted", "display value omitted", "Anzeigewert ausgelassen"),
        ("mediahash.row", "Select asset {0} | media {1} | variant {2} | captured IsCurrent: {3}", "Asset {0} auswählen | Medium {1} | Variante {2} | erfasstes IsCurrent: {3}"),
        ("mediahash.yes", "yes", "ja"),
        ("mediahash.no", "no", "nein"),
        ("mediahash.failed", "The captured inventory search could not be completed. No file verification was attempted.", "Die Suche im erfassten Inventar konnte nicht abgeschlossen werden. Es wurde keine Dateiprüfung versucht."),
        ("mediahash.canceled", "Search canceled. No result is selected.", "Suche abgebrochen. Kein Ergebnis ist ausgewählt.")
    };

    internal static string T(string key, params object?[] args)
    {
        string registered = Texts.T(key, args);
        if (registered != key) return registered;
        foreach (var entry in Entries)
            if (entry.Key == key)
                return string.Format(CultureInfo.GetCultureInfo(Texts.Language),
                    Texts.Language == "de" ? entry.German : entry.English, args);
        return key;
    }
}
