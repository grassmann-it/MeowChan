namespace MeowChan.Desktop.Localization;

/// <summary>Texts for own tags in the archive reader and thread list. Registered in BuiltInCatalog.</summary>
internal static class TagBarCatalog
{
    internal static readonly (string Key, string English, string German)[] Entries =
    {
        ("tags.open", "Tags", "Tags"),
        ("tags.openCount", "Tags ({0})", "Tags ({0})"),
        ("tags.openHint", "Show and edit your own tags for this thread. Tags stay local in this profile.", "Eigene Tags dieses Threads anzeigen und bearbeiten. Tags bleiben lokal in diesem Profil."),
        ("tags.assigned", "Own tags of this thread", "Eigene Tags dieses Threads"),
        ("tags.none", "No own tags yet.", "Noch keine eigenen Tags."),
        ("tags.remove", "Remove tag {0} from this thread", "Tag {0} von diesem Thread entfernen"),
        ("tags.suggestions", "Suggestions from title and first post:", "Vorschläge aus Titel und erstem Beitrag:"),
        ("tags.addSuggestion", "Add suggested tag {0}", "Vorgeschlagenen Tag {0} hinzufügen"),
        ("tags.input", "Tag name", "Tagname"),
        ("tags.add", "Add tag", "Tag hinzufügen"),
        ("tags.close", "Close tags", "Tags schließen"),
        ("tags.rowLabel", "Tags: {0}", "Tags: {0}"),
        ("tags.unavailable", "Own tags could not be loaded. Nothing was changed.", "Eigene Tags konnten nicht geladen werden. Es wurde nichts geändert."),
        ("tags.notReady", "Tags can be changed once the session is ready.", "Tags lassen sich ändern, sobald die Sitzung bereit ist."),
        ("tags.error.invalidName", "This tag name is not allowed. Use 1 to 128 characters without line breaks or control characters.", "Dieser Tagname ist nicht erlaubt. Verwende 1 bis 128 Zeichen ohne Zeilenumbrüche oder Steuerzeichen."),
        ("tags.error.capacity", "The tag limit is reached. Nothing was changed.", "Die Taggrenze ist erreicht. Es wurde nichts geändert."),
        ("tags.error.stale", "Tags changed in the meantime. Nothing was changed. Try again.", "Die Tags haben sich inzwischen geändert. Es wurde nichts geändert. Bitte erneut versuchen."),
        ("tags.error.other", "The tag could not be saved. Nothing was changed.", "Der Tag konnte nicht gespeichert werden. Es wurde nichts geändert.")
    };
}
