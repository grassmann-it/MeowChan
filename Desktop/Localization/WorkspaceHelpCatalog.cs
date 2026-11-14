namespace MeowChan.Desktop.Localization;

internal static class WorkspaceHelpCatalog
{
    public static readonly (string Key, string English, string German)[] Entries =
    {
        ("help.escape", "Esc  Close shortcut help", "Esc  Tastenkürzelhilfe schließen"),
        ("help.singleKeysOff", "Single-key shortcuts are turned off (Settings → Keyboard shortcuts).", "Einzeltasten-Kürzel sind ausgeschaltet (Einstellungen → Tastenkürzel)."),
        ("help.scope.generalNote", "Shortcuts without a note apply outside inputs and overlays, when the action is available.", "Kürzel ohne Zusatz gelten außerhalb von Eingaben und Overlays, wenn die Aktion verfügbar ist."),
        ("help.scope.current", "Current area", "Aktueller Bereich"),
        ("help.scope.application", "Application and navigation", "Anwendung und Navigation"),
        ("macmenu.file", "File", "Ablage"),
        ("macmenu.edit", "Edit", "Bearbeiten"),
        ("macmenu.find", "Find", "Suchen"),
        ("macmenu.find.entry", "Find…", "Suchen…"),
        ("macmenu.view", "View", "Darstellung"),
        ("macmenu.go", "Go", "Gehe zu"),
        ("macmenu.window", "Window", "Fenster"),
        ("macmenu.help", "Help", "Hilfe"),
        ("macmenu.back", "Back", "Zurück"),
        ("macmenu.pagePrevious", "Previous page", "Vorherige Seite"),
        ("macmenu.pageNext", "Next page", "Nächste Seite"),
    };
}
