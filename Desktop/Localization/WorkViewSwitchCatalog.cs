using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using MeowChan.Desktop.WorkViews;

namespace MeowChan.Desktop.Localization;

/// <summary>WV12 texts. Parent registers Entries in BuiltInCatalog and coordinates the language packs/settings row.</summary>
internal static class WorkViewSwitchCatalog
{
    internal static readonly (string Key, string English, string German)[] Entries =
    {
        ("workview.switch.title", "Work view changed", "Arbeitsansicht gewechselt"),
        // Labelled counters avoid singular/plural inflections; columns are a separate, disjoint set of policy rows.
        ("workview.switch.summary", "{0} → {1}. Changed defaults: direct {2} · under More {3} · collapsed {4} · hidden {5}. Column defaults: on {6} · off/selectable {7} · not offered {8}. Saved choices and active state may differ.",
            "{0} → {1}. Geänderte Vorgaben: direkt {2} · unter Mehr {3} · eingeklappt {4} · ausgeblendet {5}. Spaltenvorgaben: an {6} · aus/wählbar {7} · nicht angeboten {8}. Gespeicherte Wahlen und aktive Zustände können abweichen."),
        // The toast uses one honest policy-row count; the full nine-argument matrix remains the Details/legacy contract.
        ("workview.switch.compactSummary", "{0} → {1} · {2} policy entries changed", "{0} → {1} · {2} Vorgaben geändert"),
        ("workview.switch.undo", "Undo", "Rückgängig"),
        ("workview.switch.details", "Details", "Details"),
        ("workview.switch.overviewUnavailable", "The work-view overview is not connected in this build.", "Die Arbeitsansichten-Übersicht ist in dieser Version noch nicht angebunden."),
        ("settings.workViewSwitchNoticeSeconds", "Work-view switch notice (seconds)", "Hinweis beim Ansichtswechsel (Sekunden)"),
        ("settings.workViewSwitchNoticeSecondsHint", "Default 8; allowed 0–60. Zero disables the notice and its Undo/Details actions. Information popups must be enabled.",
            "Standard 8; erlaubt 0–60. Null schaltet den Hinweis und seine Aktionen Rückgängig/Details aus. Informations-Popups müssen eingeschaltet sein."),
    };

    private static readonly IReadOnlyDictionary<string, (string English, string German)> Fallback =
        Entries.ToDictionary(e => e.Key, e => (e.English, e.German), StringComparer.Ordinal);

    // Standalone worker delivery remains usable before Parent registers the entries. Packs win once registered.
    internal static string T(string key, params object?[] arguments)
    {
        string translated = Texts.T(key, arguments);
        if (translated != key || !Fallback.TryGetValue(key, out var text)) return translated;
        bool german = Texts.Language.StartsWith("de", StringComparison.OrdinalIgnoreCase);
        return string.Format(CultureInfo.GetCultureInfo(german ? "de" : "en"), german ? text.German : text.English, arguments);
    }

    internal static string Summary(DiffSummary diff) => T("workview.switch.summary",
        WorkViewOverview.ViewLabel(diff.From), WorkViewOverview.ViewLabel(diff.To),
        diff.Counts.ToDirect, diff.Counts.ToOverflow, diff.Counts.ToCollapsed, diff.Counts.ToHidden,
        diff.Counts.ToColumnOn, diff.Counts.ToColumnOff, diff.Counts.ToColumnNotOffered);

    internal static string CompactSummary(DiffSummary diff) => T("workview.switch.compactSummary",
        WorkViewOverview.ViewLabel(diff.From), WorkViewOverview.ViewLabel(diff.To), diff.Counts.Total);
}
