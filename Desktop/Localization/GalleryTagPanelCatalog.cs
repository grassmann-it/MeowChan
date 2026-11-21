namespace MeowChan.Desktop.Localization;

// Texts of the gallery tag panel (PLAN-SCHNELL-TAGGEN §1.1, TP2): the position counter "Medium 4 of 27", the thread tags section that
// Ctrl+T opens, and the undo status that names another medium. The shared surface texts are in QuickTagCatalog (tags.quick.*).
internal static class GalleryTagPanelCatalog
{
    internal static readonly (string Key, string English, string German)[] Entries =
    {
        ("tags.quick.gallery.position", "Medium {0} of {1}", "Medium {0} von {1}"),
        ("tags.quick.gallery.positionMore", "Medium {0} of at least {1}", "Medium {0} von mindestens {1}"),
        ("tags.quick.gallery.positionShort", "No. {0} / {1}", "Nr. {0} / {1}"),
        ("tags.quick.gallery.positionMoreShort", "No. {0} / {1}+", "Nr. {0} / {1}+"),
        ("tags.quick.status.undoneAddOn", "Undone on {1}: “{0}” taken off again.", "Rückgängig bei {1}: „{0}“ wieder entfernt."),
        ("tags.quick.status.undoneRemoveOn", "Undone on {1}: “{0}” given back.", "Rückgängig bei {1}: „{0}“ wieder zugewiesen."),
        ("tags.quick.thread.heading", "Thread tags ({0})", "Thread-Tags ({0})"),
        ("tags.quick.thread.empty", "This thread has no tags yet. Thread tags mark the thread, not its media files.",
            "Dieser Thread hat noch keine Tags. Thread-Tags markieren den Thread, nicht seine Medien."),
        ("tags.quick.thread.hint", "Thread tags mark the thread, not its media files. Click a tag to remove it.",
            "Thread-Tags markieren den Thread, nicht seine Medien. Ein Klick auf einen Tag entfernt ihn."),
        ("tags.quick.thread.remove", "Remove thread tag {0}", "Thread-Tag {0} entfernen"),
        ("tags.quick.thread.field", "Add a thread tag", "Thread-Tag hinzufügen"),
        ("tags.quick.thread.chips", "Thread tags", "Thread-Tags"),
    };
}
