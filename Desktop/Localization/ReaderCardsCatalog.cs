namespace MeowChan.Desktop.Localization;

// Reader post cards (study 2026-11-13): header line, file line and inline references.
internal static class ReaderCardsCatalog
{
    public static readonly (string Key, string English, string German)[] Entries =
    {
        ("reader.postNumber", "No. {0}", "Nr. {0}"),
        ("reader.copyPostNumber", "Copy >>{0} to quote this post", ">>{0} zum Zitieren dieses Posts kopieren"),
        ("reader.postNumberCopied", "Copied >>{0} to the clipboard.", ">>{0} in die Zwischenablage kopiert."),
        ("reader.copyFailed", "The clipboard is unavailable; nothing was copied.", "Die Zwischenablage ist nicht verfügbar; nichts wurde kopiert."),
        ("reader.replyCount", "Replies: {0}", "Antworten: {0}"),
        ("reader.quotedPosts", "Quoted posts", "Zitierte Posts"),
        ("reader.quoteNotCaptured", "(not captured)", "(nicht erfasst)"),
        ("reader.unreadLine", "{0} new posts since last time", "{0} neue Posts seit zuletzt"),
        ("reader.unreadReturn", "Back to last read", "Zurück zum Lesestand"),
    };
}
