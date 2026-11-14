namespace MeowChan.Desktop.Localization;

internal static class ReaderLiveCatalog
{
    public static readonly (string Key, string English, string German)[] Entries =
    {
        ("reader.anchorUnavailable", "The previous post is no longer in this saved view. Showing the nearest locally saved post.", "Der vorherige Post ist in dieser gespeicherten Ansicht nicht mehr vorhanden. Der nächste lokal verfügbare Post wird angezeigt."),
        ("reader.liveHint", "Shows newly saved posts of this watched thread as they arrive, without a click. Requires post capture. Does not start downloads or source checks.", "Zeigt neu gespeicherte Posts dieses beobachteten Threads automatisch an, ohne Klick. Benötigt Post-Erfassung. Startet keine Downloads oder Quellenabrufe."),
        ("reader.live.stateOn", "Showing new posts automatically", "Neue Posts werden automatisch angezeigt"),
        ("reader.newPostsCount", "{0} new saved posts", "{0} neue gespeicherte Posts"),
        ("reader.savedUpdate", "Saved thread updated", "Gespeicherter Thread aktualisiert"),
        ("reader.updateFailed", "Could not check saved posts: {0}", "Gespeicherte Posts konnten nicht geprüft werden: {0}")
    };
}
