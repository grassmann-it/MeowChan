namespace MeowChan.Desktop.Localization;

internal static class DropCatalog
{
    public static readonly (string Key, string English, string German)[] Entries =
    {
        ("drop.links.title", "Drop thread links to download", "Thread-Links zum Herunterladen hier ablegen"),
        ("drop.links.detail", "They are added exactly like links typed into the URL field.", "Sie werden genauso hinzugefügt wie Links, die ins URL-Feld eingegeben werden."),
        ("drop.files.title", "Importing your own files is coming later", "Eigene Dateien importieren kommt später"),
        ("drop.files.detail", "For now, drop thread or board links, or paste them into the URL field.", "Bis dahin Thread- oder Board-Links ablegen oder ins URL-Feld einfügen."),
        ("drop.empty.title", "Nothing to add", "Nichts zum Hinzufügen"),
        ("drop.empty.detail", "Drop thread or board links as text.", "Thread- oder Board-Links als Text ablegen.")
    };
}
