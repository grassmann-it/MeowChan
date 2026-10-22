namespace MeowChan.Desktop.Localization;

internal static class UpdateCatalog
{
    internal static readonly (string Key, string English, string German)[] Entries =
    [
        ("updates.title", "Updates", "Updates"),
        ("updates.automatic", "Automatically check for updates", "Automatisch nach Updates suchen"),
        ("updates.prereleases", "Include prereleases", "Vorabversionen einbeziehen"),
        ("updates.privacy", "Checks GitHub at most once every 24 hours, sending only the MeowChan version as User-Agent; no IDs or telemetry.", "Prüft GitHub höchstens alle 24 Stunden und sendet nur die MeowChan-Version als User-Agent; keine IDs oder Telemetrie."),
        ("updates.disabled", "Update checking is disabled by MEOWCHAN_DISABLE_UPDATE_CHECK=1 for this installation.", "Die Updateprüfung ist für diese Installation durch MEOWCHAN_DISABLE_UPDATE_CHECK=1 deaktiviert."),
        ("updates.check", "Check for updates", "Nach Updates suchen"),
        ("updates.checking", "Checking for updates…", "Suche nach Updates …"),
        ("updates.available", "Version {0} available", "Version {0} verfügbar"),
        ("updates.current", "You are using the latest version.", "Du verwendest die aktuelle Version."),
        ("updates.unchecked", "No update check yet.", "Noch keine Updateprüfung."),
        ("updates.lastChecked", "Last check: {0}", "Letzte Prüfung: {0}"),
        ("updates.releaseNotes", "Open release notes", "Release Notes öffnen"),
        ("updates.download", "Open download page", "Downloadseite öffnen"),
        ("updates.skip", "Skip this version", "Diese Version überspringen"),
        ("updates.browserOnly", "Opens your browser. MeowChan does not download or install updates.", "Öffnet den Browser. MeowChan lädt keine Updates herunter und installiert sie nicht."),
        ("updates.error.network", "Could not reach GitHub. Check your connection and try again.", "GitHub ist nicht erreichbar. Verbindung prüfen und erneut versuchen."),
        ("updates.error.rateLimited", "GitHub's request limit has been reached. Please try again later.", "Das Anfragelimit von GitHub ist erreicht. Bitte später erneut versuchen."),
        ("updates.error.http", "GitHub could not complete the update check. Please try again later.", "GitHub konnte die Updateprüfung nicht abschließen. Bitte später erneut versuchen."),
        ("updates.error.invalidResponse", "GitHub returned an unreadable release response. Please try again later.", "Die Releaseantwort von GitHub ist nicht lesbar. Bitte später erneut versuchen."),
        ("updates.error.timeout", "The update check timed out. Please try again later.", "Zeitüberschreitung bei der Updateprüfung. Bitte später erneut versuchen."),
        ("updates.error.persistence", "Update information could not be saved in this profile. Check its access permissions.", "Updateinformationen konnten nicht in diesem Profil gespeichert werden. Zugriffsrechte prüfen."),
        ("updates.error.skip", "This version could not be skipped. Check the profile's access permissions.", "Diese Version konnte nicht übersprungen werden. Zugriffsrechte des Profils prüfen."),
        ("budgets.reserve", "Free-space reserve (GiB)", "Speicherplatzreserve (GiB)"),
        ("budgets.reserveHint", "Downloads wait when a target has less free space. Unknown file sizes can still exhaust the disk; no files are deleted.", "Downloads warten, wenn am Ziel weniger Platz frei ist. Bei unbekannter Dateigröße kann der Platz trotzdem ausgehen; keine Dateien werden gelöscht."),
        ("budgets.advanced", "Advanced: plugin process limits", "Erweitert: Pluginprozess-Grenzen"),
        ("budgets.nextSession", "Changes take effect next time MeowChan starts. Increase limits only after measuring resource use.", "Änderungen gelten beim nächsten Start von MeowChan. Grenzen nur nach Messung des Ressourcenbedarfs erhöhen."),
        ("budgets.workers", "Active production workers", "Aktive Produktionsworker"),
        ("budgets.idle", "Idle timeout (minutes)", "Leerlauf bis Beenden (Minuten)"),
        ("budgets.sourceHeap", "Source worker heap limit (MiB)", "Heapgrenze für Quellenworker (MiB)"),
        ("budgets.filterHeap", "Filter worker heap limit (MiB)", "Heapgrenze für Filterworker (MiB)"),
        ("budgets.range", "{0} must be a whole number from {1} to {2}.", "{0} muss eine ganze Zahl von {1} bis {2} sein.")
    ];
}
