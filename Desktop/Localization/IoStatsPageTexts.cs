namespace MeowChan.Desktop.Localization;

/// <summary>Texts of the statistics page and the header graph; keys start with "stats." (plus the page's navigation label).</summary>
internal static class IoStatsPageTexts
{
    internal static readonly (string Key, string English, string German)[] Entries =
    [
        ("stats.graph.name", "Network download {0}, disk {1}, active downloads: {2}. Opens the statistics.", "Netzwerk herunter {0}, Datenträger {1}, aktive Downloads: {2}. Öffnet die Statistik."),
        ("stats.source.other", "Other sources", "Sonstige Quellen"),
        ("stats.source.local", "Local files", "Lokale Dateien"),
        ("stats.source.unknown", "Unknown source", "Unbekannte Quelle"),
    ];
}
