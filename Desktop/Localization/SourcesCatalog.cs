namespace MeowChan.Desktop.Localization;

internal static class SourcesCatalog
{
    internal static readonly (string Key, string English, string German)[] Entries =
    {
        ("nav.sources", "Sources", "Quellen"),
        ("sources.subtitle", "Supported sources and the threads and boards being watched. This view does not check whether a website is reachable.", "Unterstützte Quellen und beobachtete Threads und Boards. Diese Ansicht prüft nicht, ob eine Website erreichbar ist."),
        ("sources.count", "{0} registered sources", "{0} registrierte Quellen"),
        ("sources.empty", "No sources available.", "Keine Quellen verfügbar."),
        ("sources.select", "Select a source", "Quelle auswählen"),
        ("sources.selected", "Selected", "Ausgewählt"),
        ("sources.details", "Technical details", "Technische Details"),
        ("sources.website", "Open website: {0}", "Website öffnen: {0}"),
        ("sources.identity", "Source ID: {0} · {1}", "Quellen-ID: {0} · {1}"),
        ("sources.version", "Adapter version: {0}", "Adapterversion: {0}"),
        ("sources.hosts", "Supported hosts: {0}", "Unterstützte Hosts: {0}"),
        ("sources.unspecified", "Not specified", "Nicht angegeben"),
        ("sources.supported", "Supported", "Unterstützt"),
        ("sources.unsupported", "Not supported", "Nicht unterstützt"),
        ("sources.threads", "Watch individual threads", "Einzelne Threads beobachten"),
        ("sources.catalog", "Board catalog", "Boardkatalog"),
        ("sources.capture", "Post capture", "Posterfassung"),
        ("sources.originals", "Original media", "Originalmedien"),
        ("sources.previews", "Preview variants", "Vorschauvarianten"),
        ("sources.captureAvailable", "Post capture is available in this session. Existing observation settings still apply.", "Posterfassung ist in dieser Sitzung verfügbar. Die Einstellungen bestehender Beobachtungen gelten weiterhin."),
        ("sources.captureUnavailable", "Post capture is not available in this session.", "Posterfassung ist in dieser Sitzung nicht verfügbar."),
        ("sources.rules", "Source rules", "Quellenregeln"),
        ("sources.diagnostics", "Show diagnostics", "Diagnose anzeigen"),
        ("sources.urlHint", "Paste thread or board URLs. Uses the same admission as Downloads.", "Thread- oder Board-URLs einfügen. Wird wie unter Downloads hinzugefügt."),
        ("sources.urlResult", "See Downloads for the individual URL results.", "Die einzelnen URL-Ergebnisse stehen unter Downloads."),
        ("sources.noObservation", "No active observation", "Keine aktive Beobachtung"),
        ("sources.noProblem", "No currently reported problem", "Aktuell kein gemeldetes Problem"),
        ("sources.archived", "Archived source thread", "Quellthread archiviert"),
        ("sources.stopped", "Session stopped or stopping", "Sitzung gestoppt oder wird beendet"),
        ("sources.watchCounts", "{0} threads · {1} boards · {2} queued · {3} active", "{0} Threads · {1} Boards · {2} wartend · {3} aktiv"),
        ("sources.mediaCounts", "{0} queued · {1} active", "{0} wartend · {1} aktiv"),
        ("sources.watches", "Watched threads and boards", "Beobachtete Threads und Boards"),
        ("sources.work", "Work", "Arbeit"),
        ("sources.observation", "Observation", "Beobachtung")
    };
}
