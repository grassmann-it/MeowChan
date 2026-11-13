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
        ("reader.link.title", "Open this link in your browser?", "Diesen Link im Browser öffnen?"),
        ("reader.link.hint", "The address is shown as your browser reads it. Only open links you trust.", "Die Adresse steht so da, wie dein Browser sie liest. Öffne nur Links, denen du vertraust."),
        ("reader.link.remember", "Don't ask again this session", "Für diese Sitzung nicht mehr fragen"),
        ("reader.link.open", "Open", "Öffnen"),
        ("reader.repliesToggle", "Show or hide the replies", "Antworten ein- oder ausblenden"),
        ("reader.quotedPosts", "Quoted posts", "Zitierte Posts"),
        ("reader.quoteNotCaptured", "(not captured)", "(nicht erfasst)"),
        ("reader.unreadLine", "{0} new posts since your last visit", "{0} neue Posts seit deinem letzten Besuch"),
        ("reader.unreadReturn", "Back to last read", "Zurück zum Lesestand"),
        ("reader.unreadReturnTip", "Jumps to where you stopped reading, before the new posts. The button of the same name in the toolbar returns to where you followed a quote or link from.", "Springt zu der Stelle, an der du aufgehört hast zu lesen, vor den neuen Posts. Der gleichnamige Button in der Werkzeugleiste führt dorthin zurück, von wo du einem Zitat oder Link gefolgt bist."),
        ("reader.foldMore", "Show more ({0} lines)", "Mehr anzeigen ({0} Zeilen)"),
        ("reader.foldLess", "Show less", "Weniger anzeigen"),
        ("reader.spoilerHidden", "Spoiler, hidden", "Spoiler, verborgen"),
    };
}
