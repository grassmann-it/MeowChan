namespace MeowChan.Desktop.Localization;

/// <summary>
/// Texts of the unified media badges (MB1): the sentence that describes a thumbnail's badges for assistive technology (one short part per
/// badge, joined by the component) and the three settings that shape them. <c>media.badge.tags.one</c> is the singular of the tag count.
/// </summary>
internal static class MediaBadgeCatalog
{
    internal static readonly (string Key, string English, string German)[] Entries =
    {
        ("media.badge.missing", "File missing", "Datei fehlt"),
        ("media.badge.favorite", "Favorite", "Favorit"),
        ("media.badge.video", "Video", "Video"),
        ("media.badge.videoDuration", "Video {0}", "Video {0}"),
        ("media.badge.gif", "Animated image", "Animiertes Bild"),
        ("media.badge.queued", "In the tagging queue", "In der Tagging-Warteschlange"),
        ("media.badge.tagging.done", "Tagging done", "Tagging erledigt"),
        ("media.badge.tagging.skipped", "Tagging skipped", "Tagging übersprungen"),
        ("media.badge.tags", "{0} tags", "{0} Tags"),
        ("media.badge.tags.one", "{0} tag", "{0} Tag"),
        ("media.badge.untagged", "No tags yet", "Noch keine Tags"),
        ("media.badge.rating", "Rating {0} of 5", "Bewertung {0} von 5"),
        ("media.badge.booru", "Booru source", "Booru-Quelle"),
        ("settings.mediaBadgeCollapseBelow", "Thumbnail size below which badges shrink to dots", "Vorschaugröße, unter der Markierungen zu Punkten schrumpfen"),
        ("settings.mediaBadgeCollapseBelowHint", "Measured on the shorter side of the picture that is shown. Below this size every corner of a thumbnail shows one dot instead of its small badges. Default {0}, from {1} to {2}.", "Gemessen an der kürzeren Seite des gezeigten Bilds. Darunter zeigt jede Ecke eines Vorschaubilds nur einen Punkt statt ihrer kleinen Markierungen. Standard {0}, von {1} bis {2}."),
        ("settings.mediaBadgeHideBelow", "Thumbnail size below which no badges are shown", "Vorschaugröße, unter der keine Markierungen erscheinen"),
        ("settings.mediaBadgeHideBelowHint", "Measured on the shorter side of the picture that is shown. Below this size a thumbnail shows no badges at all, only its name and tooltip. It never exceeds the limit for dots above. Default {0}, from {1} to {2}.", "Gemessen an der kürzeren Seite des gezeigten Bilds. Darunter zeigt ein Vorschaubild gar keine Markierungen, nur Name und Kurzinfo. Der Wert liegt nie über der Grenze für Punkte oben. Standard {0}, von {1} bis {2}."),
        ("settings.mediaBadgeMaxPerCorner", "Badges per thumbnail corner", "Markierungen je Ecke eines Vorschaubilds"),
        ("settings.mediaBadgeMaxPerCornerHint", "How many small badges one corner of a thumbnail holds; further ones with lower priority are left out. Default {0}, from {1} to {2}.", "Wie viele kleine Markierungen eine Ecke eines Vorschaubilds aufnimmt; weitere mit geringerer Wichtigkeit entfallen. Standard {0}, von {1} bis {2}."),
    };
}
