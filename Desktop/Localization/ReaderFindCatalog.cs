namespace MeowChan.Desktop.Localization;

internal static class ReaderFindCatalog
{
    public static readonly (string Key, string English, string German)[] Entries =
    {
        ("readerFind.open", "Find", "Suchen"),
        ("readerFind.query", "Find text (up to 512 characters)", "Text suchen (bis 512 Zeichen)"),
        ("readerFind.previous", "Previous hit", "Voriger Treffer"),
        ("readerFind.next", "Next hit", "Nächster Treffer"),
        ("readerFind.close", "Close find", "Suche schließen"),
        ("readerFind.ready", "Search the saved thread. Enter: next · Shift+Enter: previous · Esc: close", "Gespeicherten Thread durchsuchen. Enter: weiter · Umschalt+Enter: zurück · Esc: schließen"),
        ("readerFind.invalid", "Use at most 512 UTF-16 code units; NUL is not supported.", "Höchstens 512 UTF-16-Codeeinheiten verwenden; NUL wird nicht unterstützt."),
        ("readerFind.searching", "Searching saved posts…", "Gespeicherte Posts werden durchsucht…"),
        ("readerFind.count", "Selected {0} · {1} hits", "Ausgewählt {0} · {1} Treffer"),
        ("readerFind.truncated", "Search limit reached; results are incomplete", "Suchgrenze erreicht; Ergebnisse sind unvollständig"),
        ("readerFind.body", "body", "Post-Text"),
        ("readerFind.subject", "subject", "Betreff"),
        ("readerFind.location", "Post {0} · {1} · UTF-16 position {2}, length {3}. Navigation selects the post; exact hit shown below.", "Post {0} · {1} · UTF-16-Position {2}, Länge {3}. Navigation wählt den Post; genaue Fundstelle unten."),
        ("readerFind.beyondDisplay", "This hit extends beyond the reader body display limit. The full saved-text hit is shown here, not marked in the post.", "Dieser Treffer reicht über das Anzeigelimit des Post-Texts hinaus. Die Fundstelle im vollständigen gespeicherten Text steht hier, nicht als Markierung im Post."),
        ("readerFind.failed", "Saved thread search failed. Use Search again to retry with the same text.", "Suche im gespeicherten Thread fehlgeschlagen. Mit „Erneut suchen“ mit demselben Text wiederholen."),
        ("readerFind.retry", "Search again", "Erneut suchen")
    };
}
