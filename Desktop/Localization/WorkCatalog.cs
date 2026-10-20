namespace MeowChan.Desktop.Localization;
internal static class WorkCatalog
{
    internal static readonly (string Key, string English, string German)[] Entries =
    {
        ("work.watching", "Watching", "Wird beobachtet"),
        ("work.runout", "Finishing remaining work", "Nachlauf"),
        ("work.intervention", "Intervention required", "Eingriff erforderlich"),
        ("work.completed", "Completed", "Abgeschlossen"),
        ("work.stopped", "Manually stopped", "Manuell beendet"),
        ("work.repairing", "Retrying missing media", "Fehlende Medien werden erneut versucht"),
        ("work.paused", "Globally paused", "Global pausiert"),
        ("work.saved", "{0}/{1} saved", "{0}/{1} gespeichert"),
        ("work.waitingCount", "{0} paused ({1})", "{0} pausiert ({1})"),
        ("work.interventionCount", "{0} need intervention ({1})", "{0} benötigen Eingriff ({1})"),
        ("work.missingCount", "{0} missing ({1})", "{0} fehlen ({1})"),
        ("work.pendingCount", "{0} pending ({1})", "{0} ausstehend ({1})"),
        ("work.transferPending", "awaiting transfer", "wartet auf Transfer"),
        ("work.transferFailure", "transfer failure", "Transferfehler"),
        ("work.nextDue", "Earliest retry: {0}. Queue and source limits still apply.", "Frühester Wiederholungsversuch: {0}. Warteschlange und Quellenlimits gelten weiterhin."),
        ("work.deadline", "Automatic retries end: {0}", "Automatische Wiederholungen enden: {0}"),
        ("work.capturePending", "Saving the final capture is still pending; completion is blocked.", "Die letzte Erfassung ist noch nicht gespeichert; der Abschluss ist blockiert."),
        ("work.unnamedMedia", "Media file", "Mediendatei"),
        ("work.mediaDetail", "{0}: {1} · attempts: {2}", "{0}: {1} · Versuche: {2}"),
        ("work.retry", "Retry missing media", "Fehlende Medien erneut versuchen"),
        ("work.retryAccepted", "Requested one retry for {0} media files. Queue and source limits still apply.", "Ein Wiederholungsversuch für {0} Mediendateien angefordert. Warteschlange und Quellenlimits gelten weiterhin."),
        ("work.retryPaused", "Requested one retry for {0} media files. Transfers wait until the session resumes; source limits still apply.", "Ein Wiederholungsversuch für {0} Mediendateien angefordert. Transfers warten auf das Fortsetzen der Sitzung; Quellenlimits gelten weiterhin."),
        ("work.retryNone", "No new retry was scheduled. The work may already be queued or no longer available.", "Kein neuer Wiederholungsversuch geplant. Die Arbeit ist möglicherweise bereits eingeplant oder nicht mehr verfügbar."),
        ("work.retryFailed", "The media retry could not be scheduled: {0}", "Der Medienversuch konnte nicht geplant werden: {0}")
    };
}
