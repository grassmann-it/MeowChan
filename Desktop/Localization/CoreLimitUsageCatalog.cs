namespace MeowChan.Desktop.Localization;

// Data only; the parent registers these entries in BuiltInCatalog and coordinates the language packs.
internal static class CoreLimitUsageCatalog
{
    internal static readonly (string Key, string English, string German)[] Entries =
    {
        ("coreLimits.tags.tooMany", "Maximum tags in a tag filter list: {0}. Remove a tag from this list before adding another.", "Höchstzahl der Tags in einer Tagfilter-Liste: {0}. Entferne einen Tag aus dieser Liste, bevor du einen weiteren hinzufügst."),
        ("coreLimits.tags.invalid", "This tag filter change is invalid. The existing filter has been kept.", "Diese Tagfilteränderung ist ungültig. Der bisherige Filter bleibt erhalten."),
        ("urltext.export.error.outputLimit", "The URL text exceeds the configured output limit of {0} MiB. No file was created.", "Der URL-Text überschreitet die eingestellte Ausgabegrenze von {0} MiB. Es wurde keine Datei erstellt."),
        ("urltext.export.error.pathLimit", "The destination path exceeds the supported path length or folder depth. Choose a shorter path. No file was created.", "Der Zielpfad überschreitet die unterstützte Pfadlänge oder Ordnertiefe. Wähle einen kürzeren Pfad. Es wurde keine Datei erstellt."),
    };
}
