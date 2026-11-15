namespace MeowChan.Desktop.Localization;

// Texts of the quick settings popover (PLAN-READER-MEDIEN-UND-LEISTE §2.6, R7): the chrome shared by every surface and the
// title and "all settings" link of the reader's popover. The rows reuse the labels, options and hints of the settings page by
// key, so they are not repeated here. Prefix reserved for this catalog: quicksettings.* (the one command label excepted).
// A gallery or media popover adds its own quicksettings.<surface>.title and .all.
internal static class QuickSettingsCatalog
{
    internal static readonly (string Key, string English, string German)[] Entries =
    {
        ("command.reader.quick-settings", "Reader settings…", "Reader-Einstellungen…"),
        ("quicksettings.reader.title", "Reader settings", "Reader-Einstellungen"),
        ("quicksettings.reader.all", "All reader settings", "Alle Reader-Einstellungen"),
        ("quicksettings.reader.section.view", "View (this session only)", "Ansicht (nur diese Sitzung)"),
        ("quicksettings.reader.section.media", "Media", "Medien"),
        ("quicksettings.reader.section.postVideo", "Videos in posts", "Videos in Posts"),
        ("quicksettings.reader.section.sidebarVideo", "Videos in the media sidebar", "Videos in der Medienleiste"),
        ("quicksettings.reader.section.text", "Text and layout", "Text und Layout"),
        ("quicksettings.reader.section.thread", "Thread", "Thread"),
        ("quicksettings.all.tip", "Open the settings page at these settings. The search there finds every setting.", "Öffnet die Einstellungsseite an diesen Einstellungen. Die dortige Suche findet jede Einstellung."),
        ("quicksettings.close", "Close", "Schließen"),
        ("quicksettings.close.tip", "Close · Esc", "Schließen · Esc"),
        ("quicksettings.failed", "This change could not be saved: {0}", "Diese Änderung konnte nicht gespeichert werden: {0}"),
    };
}
