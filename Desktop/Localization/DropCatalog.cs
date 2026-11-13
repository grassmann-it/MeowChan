namespace MeowChan.Desktop.Localization;

internal static class DropCatalog
{
    public static readonly (string Key, string English, string German)[] Entries =
    {
        ("drop.links.title", "Drop thread links to download", "Thread-Links zum Herunterladen hier ablegen"),
        ("drop.links.detail", "They are added exactly like links typed into the URL field.", "Sie werden genauso hinzugefügt wie Links, die ins URL-Feld eingegeben werden."),
        ("drop.files.title", "Drop to import {0} files or folders", "Loslassen, um {0} Dateien oder Ordner zu importieren"),
        ("drop.files.detail", "They are copied into your media library; the originals stay where they are.", "Sie werden in deine Medienverwaltung kopiert; die Originale bleiben, wo sie sind."),
        ("drop.files.unreadable", "The dropped files have no local path. Choose them with Import files… instead.", "Die abgelegten Dateien haben keinen lokalen Pfad. Wähle sie stattdessen über „Dateien importieren…“."),
        ("drop.empty.title", "Nothing to add", "Nichts zum Hinzufügen"),
        ("drop.empty.detail", "Drop thread or board links as text.", "Thread- oder Board-Links als Text ablegen.")
    };
}
