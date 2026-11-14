namespace MeowChan.Desktop.Localization;

/// <summary>The settings page More action and portable export UI. No paths or exception details are displayed.</summary>
internal static class SettingsTransferUiCatalog
{
    internal static readonly (string Key, string English, string German)[] Entries =
    [
        ("settings.transfer.more", "More settings actions", "Weitere Einstellungsaktionen"),
        ("settings.transfer.export", "Export saved settings…", "Gespeicherte Einstellungen exportieren…"),
        ("settings.transfer.exportTitle", "Export saved settings (unsaved changes are not included)", "Gespeicherte Einstellungen exportieren (ohne ungespeicherte Änderungen)"),
        ("settings.transfer.exportFileType", "Settings JSON", "Einstellungen als JSON"),
        ("settings.transfer.exportFailed", "Settings could not be exported. Check the destination and try again. Your settings draft has not been saved or changed.", "Die Einstellungen konnten nicht exportiert werden. Prüfe das Ziel und versuche es erneut. Dein Einstellungsentwurf wurde weder gespeichert noch verändert."),
    ];
}
