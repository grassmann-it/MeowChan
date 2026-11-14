namespace MeowChan.Desktop.Localization;

// The reader's "View" menu and its placeholders (plan P0). Command labels of the later packages (conversation view,
// post filters, hide/show a post) are here too, because P0 creates every one of those commands.
internal static class ReaderViewCatalog
{
    public static readonly (string Key, string English, string German)[] Entries =
    {
        ("reader.view.menu", "View", "Ansicht"),
        // R2: the "Thread" menu next to "View" (capture, check now, retry media, media rules, details, folder, source, link, watch).
        ("reader.thread.menu", "Thread", "Thread"),
        ("reader.thread.copyLink", "Copy link", "Link kopieren"),
        ("command.reader.view.hide-media", "Hide media", "Medien ausblenden"),
        ("command.reader.view.media-only", "Only posts with media", "Nur Posts mit Medien"),
        ("command.reader.view.conversation", "Show as conversation", "Als Gespräch zeigen"),
        ("command.reader.view.textless-large", "Larger images in posts without text", "Größere Bilder in Posts ohne Text"),
        ("command.reader.view.focused-enlarge", "Enlarge focused post's media", "Medien des markierten Posts vergrößern"),
        ("command.reader.view.hover-enlarge", "Enlarge image on hover", "Bild beim Überfahren vergrößern"),
        ("reader.view.saveFailed", "Could not save the view setting: {0}", "Die Ansichtseinstellung konnte nicht gespeichert werden: {0}"),
        ("command.reader.post-filters", "Post filters…", "Postfilter…"),
        ("command.post.hide", "Hide post", "Post ausblenden"),
        ("command.post.unhide", "Show post again", "Post wieder anzeigen"),
        ("reader.view.preparing", "Preparing the view…", "Ansicht wird vorbereitet…"),
        ("reader.view.hiddenCount", "{0} hidden in this view", "{0} in dieser Ansicht ausgeblendet"),
        ("reader.view.hiddenCount.one", "{0} hidden in this view", "{0} in dieser Ansicht ausgeblendet"),
        ("reader.view.empty", "No post matches this view.", "Kein Post passt zu dieser Ansicht."),
        ("reader.view.stub", "No. {0} hidden · {1}", "Nr. {0} ausgeblendet · {1}"),
        ("reader.view.show", "Show", "Anzeigen"),
        ("reader.view.revealed", "Shown although this view hides it", "Angezeigt, obwohl diese Ansicht ihn ausblendet"),
        ("reader.view.hideAgain", "Hide again", "Wieder ausblenden"),
        ("reader.view.mediaHidden", "Media hidden ({0})", "Medien ausgeblendet ({0})"),
        ("reader.view.mediaHidden.one", "Media hidden ({0})", "Medien ausgeblendet ({0})"),
        ("reader.view.depthLabel", "Reply to No. {0}, level {1}", "Antwort auf Nr. {0}, Ebene {1}"),
    };
}
