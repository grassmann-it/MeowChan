namespace MeowChan.Desktop.Localization;

internal static class ReaderLiveCatalog
{
    public static readonly (string Key, string English, string German)[] Entries =
    {
        ("reader.anchorUnavailable", "The previous post is no longer in this saved view. Showing the nearest locally saved post.", "Der vorherige Post ist in dieser gespeicherten Ansicht nicht mehr vorhanden. Der nächste lokal verfügbare Post wird angezeigt."),
        ("reader.liveHint", "Follow newly saved posts in this watched thread. Requires post capture. Does not start downloads or source checks.", "Neu gespeicherten Posts dieses beobachteten Threads folgen. Benötigt Post-Erfassung. Startet keine Downloads oder Quellenabrufe."),
        ("reader.newPostsCount", "{0} new saved posts", "{0} neue gespeicherte Posts"),
        ("reader.savedUpdate", "Saved thread updated", "Gespeicherter Thread aktualisiert"),
        ("reader.updateFailed", "Could not check saved posts: {0}", "Gespeicherte Posts konnten nicht geprüft werden: {0}")
    };
}
