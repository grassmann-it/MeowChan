using System.Collections.Generic;
using static MeowChan.Desktop.WorkViews.Presence;

namespace MeowChan.Desktop.WorkViews;

// Plan AP5 §1.13, owner default (WV8): Barebones and Simple show the core fields (work view, language, colour scheme,
// download folder, bandwidth, parallel downloads, disk reserve, updates, notifications); every other setting sits in a
// group "More settings" that is collapsed and carries a counter. Advanced shows every group open and without the extra
// header. A changed value or a field with an error opens its group in every view (R6), the settings search always finds
// everything and opens what it jumps to, and nothing safety-relevant (disk reserve, Save/Discard, errors) is in a group.
//
// Group ids are generic ("settings.group.<name>", Kind Section): the editor finds every expander with such an id on its
// pages, so a later group (for example Reader settings) only adds an expander and a row here. WV8 owns this file.
public static partial class WorkViewPolicy
{
    /// <summary>Prefix of every collapsible settings group row.</summary>
    public const string SettingsGroupPrefix = "settings.group.";

    private static IEnumerable<WorkViewEntry> SettingsEntries() =>
    [
        // Interface: animations and density; the view, language and colour scheme stay direct.
        SettingsGroup("interface-more"),
        // Downloads: check interval and parallel safety limit; folder, bandwidth and parallel downloads stay direct.
        SettingsGroup("downloads-more"),
        // Whole categories behind their heading: reading and media, behaviour (with the statistics block), storage limits
        // (except the disk reserve) and the shortcut table (the single-key switch stays direct).
        SettingsGroup("reading-media"),
        SettingsGroup("behavior"),
        SettingsGroup("limits"),
        SettingsGroup("shortcuts"),
        // Safety and state (R5): never in a group, never reduced.
        Same("settings.safety.reserve", WorkViewPage.Settings, WorkViewKind.Element, Required, safety: true),
        Same("settings.footer.actions", WorkViewPage.Settings, WorkViewKind.Element, Required, safety: true),
        Same("settings.error.banner", WorkViewPage.Settings, WorkViewKind.Element, Required, safety: true),
        // The search (AP12) is reachable in every view and always covers every group.
        Same("settings.search", WorkViewPage.Settings, WorkViewKind.Element, Required),
    ];

    private static WorkViewEntry SettingsGroup(string name) =>
        new(SettingsGroupPrefix + name, WorkViewPage.Settings, WorkViewKind.Section, Collapsed, Collapsed, Direct);
}
