namespace MeowChan.Desktop.Localization;

/// <summary>
/// Texts of the Status page (statistics and diagnostics as two segments of one page): its navigation label, the hint,
/// the segment bar and the shared status line. The segments keep their own keys (nav.statistics, nav.diagnostics, stats.*,
/// diagnostics.*). Keys start with "status." (plus the page's navigation and command labels).
/// </summary>
internal static class StatusCatalog
{
    internal static readonly (string Key, string English, string German)[] Entries =
    [
        ("nav.status", "Status", "Status"),
        ("command.navigation.status", "Status", "Status"),
        ("status.hint", "Network, disk and current problems of MeowChan on this computer.", "Netzwerk, Datenträger und aktuelle Probleme von MeowChan auf diesem Rechner."),
        ("status.segments", "Status sections", "Bereiche der Statusseite"),
        ("status.segment.diagnosticsCount", "Diagnostics ({0})", "Diagnose ({0})"),
        ("status.statisticsUnavailable", "The statistics are available as soon as the session has been restored.", "Die Statistik ist verfügbar, sobald die Sitzung wiederhergestellt ist."),
        ("status.strip", "Active {0} · Waiting {1} · Problems {2}", "Aktiv {0} · Wartend {1} · Probleme {2}"),
        ("status.strip.rates", "↓ {0} · W {1}", "↓ {0} · S {1}"),
    ];
}
