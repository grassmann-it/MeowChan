using System.Collections.Generic;
using System.Linq;

namespace MeowChan.Desktop.Booru
{
    /// <summary>
    /// Every machine code the Danbooru window can show: the codes the watcher scheduler and domain, the watcher search
    /// port, the production ports and the window models surface in command results, check summaries, watcher error
    /// codes and list faults. The core has no constants for them, so they are collected here (the source of truth for
    /// the window) and <c>BooruCodeTextTests</c> checks both that each has catalog text and that the core sources
    /// produce no code missing from this list. Host/plugin failure codes (<c>http-*</c>, <c>access-denied</c>, …) have
    /// their texts in <c>BooruStatusCatalog</c> (<c>booru.failure.*</c>) and are not repeated here.
    /// </summary>
    internal static class BooruCodes
    {
        // Scheduler (DanbooruWatcherScheduler): faults, command rejections and check summaries.
        internal const string CatalogUnreadable = "catalog_unreadable";
        internal const string WatcherRecordInvalid = "watcher_record_invalid";
        internal const string PersistFailed = "persist_failed";
        internal const string SchedulerStopped = "scheduler_stopped";
        internal const string SessionPaused = "session_paused";
        internal const string ProfileSuspended = "profile_suspended";
        internal const string SchedulerNotStarted = "scheduler_not_started";
        internal const string InvalidQuery = "invalid_query";
        internal const string WatcherExists = "watcher_exists";
        internal const string WatcherNotFound = "watcher_not_found";
        internal const string WatcherRemoved = "watcher_removed";
        internal const string Rejected = "rejected";
        internal const string Superseded = "superseded";
        internal const string Cancelled = "cancelled";
        // No longer produced since cursor paging (a stored watcher may still carry it); the text and "Restart from now" stay.
        internal const string ContinuationBlocked = "continuation_blocked";
        internal const string SearchResultMissing = "search_result_missing";
        internal const string SearchPortFault = "search_port_fault";
        internal const string ForeignPost = "foreign_post";
        internal const string PageRejected = "page_rejected";
        internal const string ProviderDeferred = "provider_deferred";
        internal const string SourceUnavailable = "source_unavailable";
        internal const string ProviderError = "provider_error";
        internal const string SearchResultUnknown = "search_result_unknown";
        internal const string AdmissionFailed = "admission_failed";
        internal const string ProviderCooldown = "provider_cooldown";
        internal const string ProviderCooldownUnavailable = "provider_cooldown_unavailable";
        internal const string TransportResetFailed = "transport_reset_failed";
        internal const string TransportResetUnavailable = "transport_reset_unavailable";
        internal const string WatcherError = "watcher_error";
        internal const string CheckFaulted = "check_faulted";
        internal const string CheckAlreadyRunning = "check_already_running";
        internal const string CooldownActive = "cooldown_active";

        // Domain (DanbooruWatcherDomain) rejections and error codes without an existing text.
        internal const string WatcherNotResumable = "watcher_not_resumable";
        internal const string CooldownNotActive = "cooldown_not_active";
        internal const string MissingReason = "missing_reason";
        internal const string MissingError = "missing_error";
        internal const string InvalidReason = "invalid_reason";
        internal const string InvalidError = "invalid_error";

        // Watcher search port (BooruWatcherSearchPort) and the production ports.
        internal const string WorkerUnavailable = "worker_unavailable";
        internal const string MalformedPost = "malformed_post";
        internal const string NotAdmitted = "not_admitted";
        internal const string BooruSource = "booru-source";

        // Host deferral reasons that end up as watcher codes.
        internal const string BooruCooldown = "booru-cooldown";
        internal const string ProviderThrottled = "provider-throttled";
        internal const string ProviderUnavailable = "provider-unavailable";

        // Window models and the search adapter.
        internal const string Busy = "busy";
        internal const string Canceled = "canceled";
        internal const string Closed = "closed";
        internal const string Exception = "exception";
        internal const string Error = "error";
        internal const string NoWorker = "no-worker";
        internal const string UnknownResult = "unknown_result";
        internal const string PageBinding = "page_binding";
        internal const string WrongPost = "wrong_post";
        internal const string CancelNotPending = "cancel_not_pending";
        internal const string CancelFailed = "cancel_failed";

        /// <summary>Codes whose text lives under <c>booru.watcher.problem.*</c> (domain texts on goal plus <see cref="BooruCodeCatalog"/>).</summary>
        internal static readonly IReadOnlyList<string> All =
        [
            CatalogUnreadable, WatcherRecordInvalid, PersistFailed, SchedulerStopped, SessionPaused, ProfileSuspended, SchedulerNotStarted, InvalidQuery,
            WatcherExists, WatcherNotFound, WatcherRemoved, Rejected, Superseded, Cancelled, ContinuationBlocked,
            SearchResultMissing, SearchPortFault, ForeignPost, PageRejected, ProviderDeferred, SourceUnavailable, ProviderError,
            SearchResultUnknown, AdmissionFailed, ProviderCooldown, ProviderCooldownUnavailable, TransportResetFailed,
            TransportResetUnavailable, WatcherError, CheckFaulted, CheckAlreadyRunning, CooldownActive,
            WatcherNotResumable, CooldownNotActive, MissingReason, MissingError, InvalidReason, InvalidError, WorkerUnavailable, MalformedPost, NotAdmitted, BooruSource,
            BooruCooldown, ProviderThrottled, ProviderUnavailable,
            Busy, Canceled, Closed, Exception, Error, NoWorker, UnknownResult, PageBinding, WrongPost, CancelNotPending, CancelFailed,
            // Domain codes whose texts already exist on goal (BooruStatusCatalog).
            "check_not_running", "watcher_paused", "watcher_already_paused", "watcher_already_active", "page_binding_mismatch",
            "page_limit_exceeded", "invalid_post_id", "page_not_descending", "cursor_did_not_advance", "empty_page_did_not_advance",
            "cursor_missing"
        ];

        internal static string Key(string code) => "booru.watcher.problem." + code;

        internal static bool IsKnown(string code) => All.Contains(code);
    }
}

namespace MeowChan.Desktop.Localization
{
    /// <summary>
    /// Texts for the scheduler, port and window codes collected in <see cref="Booru.BooruCodes"/>. They complete the
    /// <c>booru.watcher.problem.*</c> texts of <c>BooruStatusCatalog</c> (which keeps the domain codes) and are shown
    /// through <c>BooruText.CodeText</c>; es/fr/ru are unreviewed machine translations in the bundled packs.
    /// </summary>
    internal static class BooruCodeCatalog
    {
        private const string P = "booru.watcher.problem.";

        internal static readonly (string Key, string English, string German)[] Entries =
        {
            (P + "catalog_unreadable", "The Danbooru data file of this profile could not be read, so no watcher is running. Your files are unchanged; check the profile folder and restart MeowChan.",
                "Die Danbooru-Datendatei dieses Profils konnte nicht gelesen werden, daher läuft keine Suchbeobachtung. Deine Dateien sind unverändert; prüfe den Profilordner und starte MeowChan neu."),
            (P + "watcher_record_invalid", "A stored watcher could not be restored, so no watcher is running. Nothing was deleted; restart MeowChan after checking the profile folder.",
                "Eine gespeicherte Suchbeobachtung konnte nicht wiederhergestellt werden, daher läuft keine Suchbeobachtung. Es wurde nichts gelöscht; prüfe den Profilordner und starte MeowChan neu."),
            (P + "persist_failed", "The change could not be saved in this profile. Check free disk space and folder permissions, then try again.",
                "Die Änderung konnte in diesem Profil nicht gespeichert werden. Prüfe freien Speicherplatz und Ordnerrechte und versuche es erneut."),
            (P + "scheduler_stopped", "MeowChan is closing; watchers are no longer checked.", "MeowChan wird beendet; Suchbeobachtungen werden nicht mehr geprüft."),
            (P + "session_paused", "Downloads are paused for this session; watchers do not check automatically until you resume. Check now still works.",
                "Die Sitzung ist pausiert; Suchbeobachtungen prüfen erst nach dem Fortsetzen wieder automatisch. „Jetzt prüfen“ funktioniert weiterhin."),
            (P + "profile_suspended", "The profile is busy with a maintenance step; watchers continue afterwards.",
                "Das Profil führt gerade einen Wartungsschritt aus; die Suchbeobachtungen laufen danach weiter."),
            (P + "scheduler_not_started", "Watchers are still starting. Try again in a moment.", "Die Suchbeobachtungen starten noch. Versuche es gleich noch einmal."),
            (P + "invalid_query", "This search cannot be watched. Use one or two Danbooru tags.", "Diese Suche kann nicht beobachtet werden. Verwende ein oder zwei Danbooru-Tags."),
            (P + "watcher_exists", "A watcher for exactly this search already exists.", "Für genau diese Suche gibt es bereits eine Suchbeobachtung."),
            (P + "watcher_not_found", "This watcher no longer exists.", "Diese Suchbeobachtung gibt es nicht mehr."),
            (P + "rejected", "The watcher cannot do this in its current state.", "Die Suchbeobachtung kann das in ihrem aktuellen Zustand nicht."),
            (P + "superseded", "The check was stopped because the watcher was changed meanwhile.", "Die Prüfung wurde abgebrochen, weil die Suchbeobachtung inzwischen geändert wurde."),
            (P + "cancelled", "The check was stopped before it finished. Nothing was lost; the next check continues.", "Die Prüfung wurde vor dem Ende abgebrochen. Es ging nichts verloren; die nächste Prüfung setzt fort."),
            (P + "continuation_blocked", "More new posts arrived than one check can read, and the rest cannot be reached. Choose \"Restart from now\" to accept the gap, or remove the watcher.",
                "Es kamen mehr neue Posts hinzu, als eine Prüfung lesen kann, und der Rest ist nicht erreichbar. Wähle „Neu ab jetzt“, um die Lücke zu akzeptieren, oder entferne die Suchbeobachtung."),
            (P + "search_result_missing", "The search returned no answer. The check was stopped.", "Die Suche lieferte keine Antwort. Die Prüfung wurde abgebrochen."),
            (P + "search_port_fault", "The search failed inside MeowChan. The check was stopped; try again later.", "Die Suche ist innerhalb von MeowChan fehlgeschlagen. Die Prüfung wurde abgebrochen; versuche es später erneut."),
            (P + "foreign_post", "The provider's answer contained a post of another source. The check was stopped.", "Die Antwort des Anbieters enthielt einen Post einer anderen Quelle. Die Prüfung wurde abgebrochen."),
            (P + "page_rejected", "The provider's answer was not accepted. The check was stopped.", "Die Antwort des Anbieters wurde nicht angenommen. Die Prüfung wurde abgebrochen."),
            (P + "provider_deferred", "Danbooru asked MeowChan to wait. The watcher checks again later.", "Danbooru hat MeowChan um Wartezeit gebeten. Die Suchbeobachtung prüft später erneut."),
            (P + "source_unavailable", "The Danbooru plugin is not available (disabled or not running). Nothing was requested; the watcher is unchanged.",
                "Das Danbooru-Plugin ist nicht verfügbar (deaktiviert oder nicht gestartet). Es wurde nichts angefragt; die Suchbeobachtung ist unverändert."),
            (P + "provider_error", "Danbooru answered with an error. Resume the watcher to try again.", "Danbooru hat mit einem Fehler geantwortet. Setze die Suchbeobachtung fort, um es erneut zu versuchen."),
            (P + "search_result_unknown", "The search returned an answer MeowChan does not understand. The check was stopped.", "Die Suche lieferte eine Antwort, die MeowChan nicht versteht. Die Prüfung wurde abgebrochen."),
            (P + "admission_failed", "New posts could not be added to the download queue. Resume the watcher to try again.", "Neue Posts konnten nicht in die Downloadwarteschlange übernommen werden. Setze die Suchbeobachtung fort, um es erneut zu versuchen."),
            (P + "provider_cooldown", "Danbooru asked MeowChan to wait; no request is sent until the pause ends.", "Danbooru hat MeowChan um Wartezeit gebeten; bis zum Ende der Pause wird nichts angefragt."),
            (P + "provider_cooldown_unavailable", "MeowChan could not verify the provider cooldown. Watcher scheduling is paused for safety; no new request is sent while the cooldown is unknown.",
                "MeowChan konnte die Anbieterpause nicht prüfen. Die Planung der Suchbeobachtungen ist vorsichtshalber angehalten; solange die Pause unbekannt ist, wird keine neue Anfrage gesendet."),
            (P + "transport_reset_failed", "The provider cooldown reset failed. The existing pause remains active; MeowChan did not confirm recovery.",
                "Das Zurücksetzen der Anbieterpause ist fehlgeschlagen. Die bestehende Pause bleibt aktiv; MeowChan hat keine Wiederherstellung bestätigt."),
            (P + "transport_reset_unavailable", "This session cannot reset the provider cooldown. The existing pause remains active; MeowChan did not confirm recovery.",
                "In dieser Sitzung kann die Anbieterpause nicht zurückgesetzt werden. Die bestehende Pause bleibt aktiv; MeowChan hat keine Wiederherstellung bestätigt."),
            (P + "watcher_error", "The watcher stopped after an error. Resume it to check again.", "Die Suchbeobachtung wurde nach einem Fehler angehalten. Setze sie fort, um wieder zu prüfen."),
            (P + "check_faulted", "The watcher check failed unexpectedly. Resume it to try again.", "Die Prüfung der Suchbeobachtung ist unerwartet fehlgeschlagen. Setze sie fort, um es erneut zu versuchen."),
            (P + "watcher_not_resumable", "The provider cooldown cannot be applied to this watcher in its current state. The watcher is unchanged.",
                "Die Anbieterpause kann im aktuellen Zustand nicht für diese Suchbeobachtung übernommen werden. Die Suchbeobachtung ist unverändert."),
            (P + "cooldown_not_active", "This watcher has no active provider cooldown to reset. The watcher is unchanged.",
                "Diese Suchbeobachtung hat keine aktive Anbieterpause zum Zurücksetzen. Die Suchbeobachtung ist unverändert."),
            (P + "missing_reason", "The provider pause came without a reason. The check was stopped.", "Die Anbieterpause kam ohne Grund. Die Prüfung wurde abgebrochen."),
            (P + "missing_error", "The check failed without an error code.", "Die Prüfung ist ohne Fehlercode fehlgeschlagen."),
            (P + "invalid_reason", "The provider pause supplied an invalid reason code. The check was stopped.", "Die Anbieterpause enthielt einen ungültigen Grundcode. Die Prüfung wurde abgebrochen."),
            (P + "invalid_error", "The watcher check supplied an invalid error code. The check was stopped.", "Die Prüfung der Suchbeobachtung enthielt einen ungültigen Fehlercode. Die Prüfung wurde abgebrochen."),
            (P + "worker_unavailable", "The Danbooru plugin could not be started. Nothing was requested.", "Das Danbooru-Plugin konnte nicht gestartet werden. Es wurde nichts angefragt."),
            (P + "malformed_post", "The provider's answer contained a post MeowChan cannot store.", "Die Antwort des Anbieters enthielt einen Post, den MeowChan nicht speichern kann."),
            (P + "not_admitted", "The download queue did not accept the original (it may be stopping). Try again.", "Die Downloadwarteschlange hat das Original nicht angenommen (sie wird eventuell beendet). Versuche es erneut."),
            (P + "booru-source", "This post belongs to another source.", "Dieser Post gehört zu einer anderen Quelle."),
            (P + "booru-cooldown", "Danbooru limited requests (HTTP 429). MeowChan waits before asking again.", "Danbooru hat Anfragen begrenzt (HTTP 429). MeowChan wartet, bevor es erneut anfragt."),
            (P + "provider-throttled", "Danbooru is limiting requests. MeowChan waits before asking again.", "Danbooru begrenzt Anfragen. MeowChan wartet, bevor es erneut anfragt."),
            (P + "provider-unavailable", "Danbooru is temporarily unavailable. The watcher tries again later.", "Danbooru ist vorübergehend nicht verfügbar. Die Suchbeobachtung versucht es später erneut."),
            (P + "busy", "Another action is still running.", "Eine andere Aktion läuft noch."),
            (P + "canceled", "The action was cancelled.", "Die Aktion wurde abgebrochen."),
            (P + "closed", "The window was closed.", "Das Fenster wurde geschlossen."),
            (P + "exception", "Something went wrong inside MeowChan. The log file has details.", "In MeowChan ist ein Fehler aufgetreten. Die Protokolldatei enthält Details."),
            (P + "error", "An unknown error occurred.", "Ein unbekannter Fehler ist aufgetreten."),
            (P + "no-worker", "The Danbooru plugin is not available (disabled or not running).", "Das Danbooru-Plugin ist nicht verfügbar (deaktiviert oder nicht gestartet)."),
            (P + "unknown_result", "The search returned an answer MeowChan does not understand.", "Die Suche lieferte eine Antwort, die MeowChan nicht versteht."),
            (P + "page_binding", "The provider's answer did not match the search that was sent.", "Die Antwort des Anbieters passte nicht zur gesendeten Suche."),
            (P + "wrong_post", "The provider returned a different post than the one requested.", "Der Anbieter hat einen anderen Post als den angefragten geliefert."),
            (P + "cancel_not_pending", "This original is no longer pending, so it could not be cancelled.", "Dieses Original wartet nicht mehr, daher konnte es nicht abgebrochen werden."),
            (P + "cancel_failed", "The original download could not be cancelled. Try again.", "Der Download des Originals konnte nicht abgebrochen werden. Versuche es erneut.")
        };
    }
}
