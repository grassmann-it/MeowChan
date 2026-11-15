namespace MeowChan.Desktop.Localization;

internal static class MediaRatingCatalog
{
    internal static readonly (string Key, string English, string German)[] Entries =
    {
        ("media.rating.row", "{0} · {1}", "{0} · {1}"),
        ("media.rating.accessibleValue", "{0}: {1}", "{0}: {1}"),
        ("media.rating.label", "Rating", "Bewertung"),
        ("media.rating.value", "{0} of 5", "{0} von 5"),
        ("media.rating.unrated", "Unrated", "Unbewertet"),
        ("media.rating.unavailable", "Unavailable", "Nicht verfügbar"),
        ("media.rating.filter.all", "All ratings", "Alle Bewertungen"),
        ("media.rating.filter.atLeast", "At least {0}", "Mindestens {0}"),
        ("media.rating.filter.label", "Rating filter", "Bewertungsfilter"),
        ("media.rating.filter.noLongerMatches", "The media no longer matches the rating filter.", "Das Medium passt nicht mehr zum Bewertungsfilter."),
        ("media.rating.filter.sortDescending", "Rating descending", "Bewertung absteigend"),
        ("media.rating.identity", "{0} /{1}/ {2} · {3} · {4}\nSHA-256: {5}", "{0} /{1}/ {2} · {3} · {4}\nSHA-256: {5}"),
        ("media.rating.search", "Search identities", "Bewertungen durchsuchen"),
        ("media.rating.noSaved", "No saved ratings.", "Keine gespeicherten Bewertungen."),
        ("media.rating.noMatching", "No matching ratings.", "Keine passenden Bewertungen."),
        ("media.rating.summary", "Shown: {0} · saved ratings: {1}", "Angezeigt: {0} · gespeicherte Bewertungen: {1}"),
        ("media.rating.confirmRemove", "Remove rating for this exact identity?", "Bewertung für genau diese Identität entfernen?"),
        ("media.rating.removeThis", "Remove this rating", "Diese Bewertung entfernen"),
        ("media.rating.cancel", "Cancel", "Abbrechen"),
        ("media.rating.error.save", "Could not save the rating.", "Bewertung konnte nicht gespeichert werden."),
        ("media.rating.error.remove", "Could not remove the rating.", "Bewertung konnte nicht entfernt werden."),
        ("media.rating.error.load", "Could not load the ratings.", "Bewertungen konnten nicht geladen werden."),
        ("media.rating.error.details", "Details: ", "Details: ")
    };
}
