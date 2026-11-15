namespace MeowChan.Desktop.Localization;

// Texts of the favorite and rating row in the gallery tag panel (PLAN-POWER-TAGGEN-2026-11-14 PT6, decision E2): the row's names, the
// live lines ("Favorite on some", "Ratings differ") and the sentences the polite status line says after a rating or favorite change,
// its undo, a refusal, and how many marked media a change reached. Counts are numbers in fixed phrases, so no language needs plural forms.
internal static class GalleryDecisionCatalog
{
    internal static readonly (string Key, string English, string German)[] Entries =
    {
        ("gallery.decision.name", "Favorite and rating", "Favorit und Bewertung"),
        ("gallery.decision.favorite", "Favorite", "Favorit"),
        ("gallery.post.tags.edit", "Edit tags", "Tags bearbeiten"),
        ("gallery.post.tags.more", "+{0} more", "+{0} weitere"),
        ("gallery.decision.favorite.tip", "Add to or remove from favorites", "Zu den Favoriten hinzufügen oder daraus entfernen"),
        ("gallery.decision.favorite.some", "Favorite on some", "Bei einigen Favorit"),
        ("gallery.decision.rating.mixed", "Ratings differ", "Bewertungen unterschiedlich"),
        ("gallery.decision.status.ratingSaved", "Rating {0} of 5 saved.", "Bewertung {0} von 5 gespeichert."),
        ("gallery.decision.status.ratingRemoved", "Rating removed.", "Bewertung entfernt."),
        ("gallery.decision.status.favoriteSaved", "Saved as a favorite.", "Als Favorit gespeichert."),
        ("gallery.decision.status.favoriteRemoved", "Removed from favorites.", "Aus den Favoriten entfernt."),
        ("gallery.decision.status.undoneRating", "Undone: rating change.", "Rückgängig: Bewertungsänderung."),
        ("gallery.decision.status.undoneFavorite", "Undone: favorite change.", "Rückgängig: Favoritenänderung."),
        ("gallery.decision.status.failed", "The change could not be saved.", "Die Änderung konnte nicht gespeichert werden."),
        ("gallery.decision.status.unavailable", "Favorites and ratings are not available for this medium.", "Favoriten und Bewertungen sind für dieses Medium nicht verfügbar."),
        ("gallery.decision.status.allMarked", "Applies to all marked media: {0}.", "Gilt für alle markierten Medien: {0}."),
        ("gallery.decision.status.skipped", "Not changed: {0}.", "Nicht geändert: {0}."),
        // GL6: the info line's transient confirmation after the gallery's rating button or keys saved a rating (a polite live region).
        ("gallery.rating.saved", "Rating saved", "Bewertung gespeichert"),
    };
}
