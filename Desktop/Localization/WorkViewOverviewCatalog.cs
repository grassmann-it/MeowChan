using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace MeowChan.Desktop.Localization;

/// <summary>WV11 texts. Parent registers Entries in BuiltInCatalog and copies them to all language packs.</summary>
internal static class WorkViewOverviewCatalog
{
    internal static readonly (string Key, string English, string German)[] Entries =
    {
        ("settings.section.workViews", "Work views", "Arbeitsansichten"),
        ("workview.overview.description", "Read-only defaults for every work view. Active filters and errors remain visible; saved column choices may override defaults.", "Vorgaben aller Arbeitsansichten, nur zur Information. Aktive Filter und Fehler bleiben sichtbar; gespeicherte Spaltenwahlen können Vorgaben überschreiben."),
        ("workview.overview.page", "Page", "Seite"),
        ("workview.overview.allPages", "All pages", "Alle Seiten"),
        ("workview.overview.filter", "Find an element", "Element suchen"),
        ("workview.overview.element", "Element", "Element"),
        ("workview.overview.active", "{0} (active)", "{0} (aktiv)"),
        ("workview.overview.count", "{0} of {1} elements", "{0} von {1} Elementen"),
        ("workview.overview.empty", "No elements match these filters.", "Keine Elemente passen zu diesen Filtern."),
        ("workview.overview.required", "Always present; not hideable by a work view", "Immer vorhanden; nicht durch eine Arbeitsansicht ausblendbar"),
        ("workview.overview.safety", "Safety or essential state; always present", "Sicherheit oder wesentlicher Zustand; immer vorhanden"),
        ("workview.overview.column", "Column: {0}", "Spalte: {0}"),
        ("workview.overview.otherColumns", "Other columns", "Weitere Spalten"),
        ("workview.presence.direct", "Direct", "Direkt"),
        ("workview.presence.overflow", "Under More", "Unter Mehr"),
        ("workview.presence.collapsed", "Collapsed group", "Gruppe eingeklappt"),
        ("workview.presence.hidden", "Hidden; via {0}", "Ausgeblendet; über {0}"),
        ("workview.presence.required", "Always present", "Immer vorhanden"),
        ("workview.overview.noAlternative", "Hidden; no alternative recorded", "Ausgeblendet; keine Alternative hinterlegt"),
        ("workview.column.on", "On", "An"),
        ("workview.column.off", "Off (selectable)", "Aus (wählbar)"),
        ("workview.column.notOffered", "Not offered", "Nicht angeboten"),
        ("workview.overview.page.shell", "Application frame", "Anwendungsrahmen"),
        ("workview.overview.page.downloads", "Downloads", "Downloads"),
        ("workview.overview.page.archive", "Archive", "Archiv"),
        ("workview.overview.page.reader", "Reader", "Reader"),
        ("workview.overview.page.gallery", "Gallery", "Galerie"),
        ("workview.overview.page.media", "Media", "Medien"),
        ("workview.overview.page.rules", "Rules", "Regeln"),
        ("workview.overview.page.sources", "Sources", "Quellen"),
        ("workview.overview.page.plugins", "Plugins", "Plugins"),
        ("workview.overview.page.diagnostics", "Diagnostics", "Diagnose"),
        ("workview.overview.page.statistics", "Statistics", "Statistik"),
        ("workview.overview.page.status", "Status", "Status"),
        ("workview.overview.page.settings", "Settings", "Einstellungen"),
        ("workview.overview.page.dialogs", "Dialogs", "Dialoge"),
    };

    private static readonly IReadOnlyDictionary<string, (string English, string German)> Fallback =
        Entries.ToDictionary(e => e.Key, e => (e.English, e.German), StringComparer.Ordinal);

    // Keeps this standalone control usable before Parent's registration. Once registered, packs take precedence.
    internal static string T(string key, params object?[] arguments)
    {
        string translated = Texts.T(key, arguments);
        if (translated != key || !Fallback.TryGetValue(key, out var text)) return translated;
        bool german = Texts.Language.StartsWith("de", StringComparison.OrdinalIgnoreCase);
        return string.Format(CultureInfo.GetCultureInfo(german ? "de" : "en"), german ? text.German : text.English, arguments);
    }
}
