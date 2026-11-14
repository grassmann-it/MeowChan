namespace MeowChan.Desktop.Localization;

/// <summary>
/// Texts of the shared context menus of the rule overview, the statistics table, the filter chips and tag list, the notifications and the
/// settings rows: the labels of their catalog commands (<c>command.*</c>) and a few sentences of their own (<c>ctxmenu.*</c>). English and
/// German are built in; the other ten languages are in the bundled packs.
/// </summary>
internal static class ContextMenuTexts
{
    internal static readonly (string Key, string English, string German)[] Entries =
    [
        ("command.rules.toggle-enabled", "Enable or disable rule", "Regel aktivieren oder deaktivieren"),
        ("command.rules.delete", "Delete rule…", "Regel löschen…"),
        ("command.rules.copy-summary", "Copy rule summary", "Regelzusammenfassung kopieren"),
        ("command.statistics.copy-row", "Copy row", "Zeile kopieren"),
        ("command.statistics.copy-source", "Copy source name", "Quellennamen kopieren"),
        ("command.statistics.reset", "Reset statistics…", "Statistik zurücksetzen…"),
        ("command.archive.copy-filter", "Copy filter text", "Filtertext kopieren"),
        ("command.tags.filter-toggle", "Switch include / exclude", "Zwischen Einschließen und Ausschließen wechseln"),
        ("command.tags.copy-name", "Copy tag name", "Tagnamen kopieren"),
        ("command.tags.filter-only", "Show only this", "Nur diesen anzeigen"),
        ("command.feedback.copy-text", "Copy message text", "Meldungstext kopieren"),
        ("command.feedback.dismiss-all", "Close all notifications", "Alle Benachrichtigungen schließen"),
        ("command.settings.reset-default", "Reset to default", "Auf Standard zurücksetzen"),
        ("command.settings.copy-id", "Copy setting id", "Einstellungs-ID kopieren"),
        ("ctxmenu.copyFailed", "The text could not be copied.", "Der Text konnte nicht kopiert werden."),
        ("ctxmenu.setting.noDefault", "No default is known for this setting.", "Für diese Einstellung ist kein Standardwert hinterlegt."),
        ("ctxmenu.setting.atDefault", "The setting already has its default value.", "Die Einstellung hat bereits den Standardwert."),
        ("ctxmenu.name.setting", "Setting", "Einstellung"),
        ("ctxmenu.name.notification", "Notification", "Benachrichtigung"),
    ];
}
