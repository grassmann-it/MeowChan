namespace MeowChan.Desktop.Localization;

/// <summary>
/// Shared texts of the work views (plan AP5, WV0): "More" overflow with its counted accessible name, collapsed-group
/// counters, the new view descriptions (R9), the one-time notice for existing profiles and the view-switch command.
/// Page packages use these keys; a page that needs its own text adds it in all 12 packs.
/// </summary>
internal static class WorkViewCatalog
{
    internal static readonly (string Key, string English, string German)[] Entries =
    {
        ("workview.more", "More", "Mehr"),
        // R7: the accessible name announces how many actions the overflow holds.
        ("workview.moreCount", "More, {0} actions", "Mehr, {0} Aktionen"),
        ("workview.moreCount.one", "More, {0} action", "Mehr, {0} Aktion"),
        // Header counters of a collapsed section or settings group (R6).
        ("workview.collapsedChanged", "{0} changed", "{0} geändert"),
        ("workview.activeCount", "{0} active", "{0} aktiv"),
        ("workview.moreSettings", "More settings", "Weitere Einstellungen"),
        ("workview.moreFilters", "More filters", "Weitere Filter"),
        ("workview.moreOptions", "More options", "Weitere Optionen"),
        ("workview.developerArea", "Developer area", "Entwicklerbereich"),
        // R8: label of the bindable view-switch command (no default shortcut).
        ("workview.viewCycle", "Switch work view", "Arbeitsansicht wechseln"),
        // R9: F1 marks shortcuts whose button this view keeps under More.
        ("workview.underMore", "In this view under More", "In dieser Ansicht unter Mehr"),
        ("workview.description.barebones", "Downloads first; the other tools are under More.", "Downloads im Vordergrund; Werkzeuge stehen unter Mehr."),
        ("workview.description.simple", "Adds reading and browsing; technical tools are under More.", "Ergänzt Lesen und Stöbern; technische Werkzeuge stehen unter Mehr."),
        ("workview.description.advanced", "Shows everything directly, including technical columns and maintenance tools.", "Zeigt alles direkt, auch technische Spalten und Wartungswerkzeuge."),
        // One-time notice for profiles that used Barebones or Simple before the views reduced every page.
        ("workview.notice.text", "This view now shows fewer tools; everything is under More.", "Diese Ansicht zeigt jetzt weniger Werkzeuge; alles steht unter Mehr."),
        ("workview.notice.switchAdvanced", "Switch to Advanced", "Zu Erweitert wechseln"),
        ("workview.notice.dismiss", "Got it", "Verstanden")
    };
}
