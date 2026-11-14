namespace MeowChan.Desktop.Localization;

/// <summary>The merged Plugins page (sources, image filters, all plugins). Its footer label is plain "Plugins" (Tom, 14.11.); the command ids stay navigation.sources/navigation.plugins.</summary>
internal static class SourcesPluginsCatalog
{
    internal static readonly (string Key, string English, string German)[] Entries =
    [
        ("nav.sources", "Plugins", "Plugins"),
        ("command.navigation.sources", "Plugins", "Plugins"),
        ("command.navigation.plugins", "Show all plugins", "Alle Plugins anzeigen"),
        ("sources.show", "Show", "Anzeigen"),
        ("sources.subtitle", "Websites, what you watch there and the plugins behind them. Status describes this session, not website availability.", "Websites, was du dort beobachtest, und die Plugins dahinter. Der Status beschreibt diese Sitzung, nicht die Erreichbarkeit der Websites."),
        ("sources.mediaFoldersLink", "Media folders are in Settings › Storage and limits", "Medienordner stehen unter Einstellungen › Speicher und Grenzen"),
        ("sources.pluginDetails", "Plugin details", "Plugin-Details"),
        ("sources.pluginSummary", "Plugin: {0} · {1}", "Plugin: {0} · {1}"),
        ("sources.backToSource", "Back to source", "Zurück zur Quelle"),
        ("sources.inactiveHint", "Not activated. Manage the plugin below; changes may require a restart.", "Nicht aktiviert. Verwalte das Plugin unten; Änderungen können einen Neustart erfordern."),
        ("sources.appliesTogether", "Applies together with: {0}", "Gilt zusammen mit: {0}"),
        ("sources.countActive", "Sources: {0} · Active: {1}", "Quellen: {0} · Aktiv: {1}"),
    ];
}
