namespace MeowChan.Desktop.Localization;
internal static class WorkCatalog
{
    internal static readonly (string Key, string English, string German)[] Entries =
    {
        ("work.missingAndErrors", "{0} / {1} errors", "{0} / {1} Fehler"),
        ("work.errorSummary", "{0} media errors", "{0} Medienfehler"),
        ("work.sourceArchived", "The source thread is archived. Remaining media keep their existing retry limits.", "Der Quellthread ist archiviert. Für verbleibende Medien gelten die bisherigen Wiederholungsgrenzen."),
        ("work.sourceEnded", "The source thread is no longer available. Missing media may remain after the retry deadline.", "Der Quellthread ist nicht mehr verfügbar. Nach Ende der Wiederholungsfrist können Medien fehlen."),
        ("work.watching", "Watching", "Wird beobachtet"),
        ("work.runout", "Finishing remaining media", "Restliche Medien werden geladen"),
        ("work.intervention", "Needs a retry", "Erneuter Versuch nötig"),
        ("work.completed", "Completed", "Abgeschlossen"),
        ("work.stopped", "Manually stopped", "Manuell beendet"),
        ("work.repairing", "Retrying missing media", "Fehlende Medien werden erneut versucht"),
        ("work.paused", "Globally paused", "Pausiert (global)"),
        ("work.saved", "{0}/{1} saved / selected", "{0}/{1} gespeichert / ausgewählt"),
        ("work.waitingCount", "{0} waiting ({1})", "{0} warten ({1})"),
        ("work.interventionCount", "{0} need a retry ({1})", "{0} brauchen einen neuen Versuch ({1})"),
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
        ("work.retryNone", "Nothing to retry: all missing media are already queued, or this thread has no missing media left.", "Nichts zu wiederholen: Alle fehlenden Medien sind schon eingereiht oder dieser Thread hat keine fehlenden Medien mehr."),
        ("work.retryFailed", "The media retry could not be scheduled: {0}", "Der Medienversuch konnte nicht geplant werden: {0}")
    };
}
