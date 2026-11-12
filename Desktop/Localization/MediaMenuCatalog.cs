namespace MeowChan.Desktop.Localization;

// Texts of the one shared media menu (archive, media page, gallery): why a common action is unavailable and the note
// about single-item versus selection actions. Prefix reserved for this catalog: media.menu.*. Labels of the actions
// themselves come from the command catalog, tag reasons from TagBarCatalog. The es/fr/ru pack entries are machine
// translations that no human has proofread.
internal static class MediaMenuCatalog
{
    internal static readonly (string Key, string English, string German)[] Entries =
    {
        ("media.menu.reason.notSaved", "No saved version of this media file is available.", "Für diese Mediendatei ist kein gespeicherter Dateistand verfügbar."),
        ("media.menu.reason.unavailable", "Not available here right now.", "Hier gerade nicht verfügbar."),
        ("media.menu.reason.notInSequence", "This file is hidden by the current filter, so the gallery cannot open on it.", "Diese Datei ist durch den aktuellen Filter ausgeblendet; die Galerie kann nicht darauf öffnen."),
        ("media.menu.reason.severalSelected", "Several items are selected. Select a single item to use this action.", "Mehrere Einträge sind markiert. Für diese Aktion einen einzelnen Eintrag markieren."),
        ("media.menu.reason.severalPosts", "This saved file belongs to several posts. Use this action from the post you mean.", "Diese gespeicherte Datei gehört zu mehreren Beiträgen. Die Aktion beim gemeinten Beitrag verwenden."),
        ("media.menu.reason.providerPost", "The provider's post address is not available.", "Die Beitragsadresse des Anbieters ist nicht verfügbar."),
        ("media.menu.note.selection", "These actions apply to the item you clicked. The selection actions below apply to all {0} selected items.", "Diese Aktionen gelten für den angeklickten Eintrag. Die Auswahlaktionen darunter gelten für alle {0} markierten Einträge."),
        ("media.menu.details.posts", "Posts: {0}", "Beiträge: {0}")
    };
}
