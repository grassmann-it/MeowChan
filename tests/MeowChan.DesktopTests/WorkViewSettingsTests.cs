using System.Xml.Linq;
using Avalonia.Controls;
using MeowChan.Desktop.WorkViews;

namespace MeowChan.DesktopTests;

/// <summary>
/// WV8: the settings page under the work views. The rows of the policy, the groups of the markup and the pure decisions
/// (flat or collapsed, when a change opens a group, the counter header) on windowless controls. The page itself is
/// covered by the smoke cells of work-views-settings.
/// </summary>
// One collection: both register controls in the static work-view registry, which reads them from whichever thread asks.
[Collection("Work views")]
public sealed class WorkViewSettingsTests
{
    private static string RepositoryRoot([System.Runtime.CompilerServices.CallerFilePath] string path = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "../.."));

    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly XNamespace Wv = "using:MeowChan.Desktop.WorkViews";

    private static XDocument Markup() => XDocument.Load(Path.Combine(RepositoryRoot(), "Desktop/Views/Settings/SettingsEditor.axaml"));

    private static string? NameOf(XElement element) => (string?)element.Attribute(X + "Name");

    private static string? IdOf(XElement element) => (string?)element.Attribute(Wv + "WorkViewElement.Id");

    private static bool IsGroup(XElement element) =>
        element.Name.LocalName == "Expander" && IdOf(element)?.StartsWith(WorkViewPolicy.SettingsGroupPrefix, StringComparison.Ordinal) == true;

    private static XElement Named(XDocument markup, string name) => Assert.Single(markup.Descendants(), element => NameOf(element) == name);

    private static string[] GroupRows() => WorkViewPolicy.Entries
        .Where(entry => entry.Page == WorkViewPage.Settings && entry.Id.StartsWith(WorkViewPolicy.SettingsGroupPrefix, StringComparison.Ordinal))
        .Select(entry => entry.Id).ToArray();

    // The shortcut table is built in code (a custom control); its group id is the only one not in the markup.
    private static readonly string[] GroupsBuiltInCode = ["settings.group.shortcuts"];

    [Fact]
    public void GroupsAreCollapsedInBarebonesAndDirectInAdvancedAndFourStayDirectInSimple()
    {
        // Plan §1.13: Simple keeps animations/density, check interval/parallel safety, reading and media and behaviour direct.
        string[] directInSimple = ["settings.group.interface-more", "settings.group.downloads-more", "settings.group.reading-media", "settings.group.behavior"];
        Assert.NotEmpty(GroupRows());
        foreach (string id in GroupRows())
        {
            WorkViewEntry row = WorkViewPolicy.Get(id);
            Assert.Equal(WorkViewKind.Section, row.Kind);
            Assert.Equal(Presence.Collapsed, row.Barebones);
            Assert.Equal(directInSimple.Contains(id) ? Presence.Direct : Presence.Collapsed, row.Simple);
            Assert.Equal(Presence.Direct, row.Advanced);
            Assert.False(row.Safety);
        }
    }

    [Fact]
    public void EveryGroupRowHasAnExpanderAndEveryGroupExpanderHasARow()
    {
        string[] inMarkup = Markup().Descendants().Where(IsGroup).Select(IdOf).OfType<string>().ToArray();
        Assert.Equal(inMarkup.Length, inMarkup.Distinct().Count());
        Assert.Equal(GroupRows().Order(StringComparer.Ordinal), inMarkup.Concat(GroupsBuiltInCode).Order(StringComparer.Ordinal));
        string editor = File.ReadAllText(Path.Combine(RepositoryRoot(), "Desktop/Commands/ShortcutEditor.cs"));
        foreach (string id in GroupsBuiltInCode) Assert.Contains("WorkViewElement.SetId(_more, \"" + id + "\")", editor);
    }

    // Every setting is either in a group or on this list of core fields. A new setting lands in a group unless someone
    // consciously adds it here, so the reduced views cannot silently grow back.
    private static readonly string[] CoreInputs =
    [
        "WorkspaceModeInput", "LanguageInput", "ThemeInput",
        "DownloadPathInput", "BandwidthInput", "ParallelDownloadsInput", "ReserveInput",
        "AutomaticUpdatesInput", "PrereleaseUpdatesInput",
        "InformationNotificationsInput", "WarningNotificationsInput", "ErrorNotificationsInput",
        "WorkViewSwitchNoticeSecondsInput",
    ];

    private static readonly string[] InputElements = ["CheckBox", "ComboBox", "TextBox", "NumericUpDown", "SettingsNumberInput", "AccessibleNumberInput"];

    [Fact]
    public void EverySettingIsCoreOrInAGroupAndCoreFieldsAreNeverInOne()
    {
        XDocument markup = Markup();
        XElement[] inputs = markup.Descendants().Where(element => InputElements.Contains(element.Name.LocalName)
            && NameOf(element)?.EndsWith("Input", StringComparison.Ordinal) == true).ToArray();
        Assert.True(inputs.Length > 60);
        foreach (XElement input in inputs)
        {
            bool grouped = input.Ancestors().Any(IsGroup);
            bool core = CoreInputs.Contains(NameOf(input));
            Assert.True(grouped != core, NameOf(input) + (core ? " is a core field but sits in a group." : " is neither core nor in a group."));
        }
        foreach (string name in CoreInputs) Assert.Contains(inputs, input => NameOf(input) == name);
    }

    [Theory]
    [InlineData("settings.group.interface-more", "SettingsInterfaceMoreGroup", new[] { "AnimationsInput", "CompactDensityInput" })]
    [InlineData("settings.group.downloads-more", "SettingsDownloadsMoreGroup", new[] { "RecheckTimerInput", "ParallelDownloadsSafetyInput" })]
    [InlineData("settings.group.reading-media", "SettingsReadingMediaGroup", new[] { "ReadingFontSizeInput", "GalleryPlaybackModeInput", "VideoVolumeInput", "ShowShortcutHelpInput" })]
    [InlineData("settings.group.behavior", "SettingsBehaviorGroup", new[] { "MonitorClipboardUrlsInput", "CloseToTrayInput", "StatsSampleIntervalMsInput", "StatsMaxSourcesInput" })]
    [InlineData("settings.group.limits", "SettingsLimitsGroup", new[] { "BooruCatalogMiBInput", "ClipboardTextLimitKiBInput", "ProductionWorkersInput", "ImportCatalogLimitMiBInput" })]
    public void GroupsHoldTheSettingsThePlanAssignsToThem(string groupId, string groupName, string[] members)
    {
        XDocument markup = Markup();
        XElement group = Named(markup, groupName);
        Assert.Equal(groupId, IdOf(group));
        foreach (string member in members)
            Assert.Contains(Named(markup, member).Ancestors(), ancestor => ReferenceEquals(ancestor, group));
    }

    // R5: the disk reserve, Save/Discard and the error banner are on the surface in every view and in no group.
    [Fact]
    public void SafetyAndStateStayOutsideEveryGroup()
    {
        XDocument markup = Markup();
        foreach (string id in new[] { "settings.safety.reserve", "settings.footer.actions", "settings.error.banner" })
        {
            WorkViewEntry row = WorkViewPolicy.Get(id);
            Assert.True(row.Safety);
            Assert.All(WorkViewSetting.All, view => Assert.Equal(Presence.Required, row.For(view)));
            XElement element = Assert.Single(markup.Descendants(), candidate => IdOf(candidate) == id);
            Assert.DoesNotContain(element.AncestorsAndSelf(), IsGroup);
        }
        Assert.All(WorkViewSetting.All, view => Assert.Equal(Presence.Required, WorkViewPolicy.Get("settings.search").For(view)));
        // The disk reserve row is the one of the ReserveInput.
        XElement reserve = Named(markup, "ReserveInput");
        Assert.Contains(reserve.Ancestors(), ancestor => IdOf(ancestor) == "settings.safety.reserve");
        // Error lines of grouped fields live inside their group; the group opens when one shows (see the editor's sweep).
        Assert.NotEmpty(markup.Descendants().Where(element => NameOf(element)?.EndsWith("InputError", StringComparison.Ordinal) == true
            && element.Ancestors().Any(IsGroup)));
    }

    [Fact]
    public void GroupsAreFlatOnlyWhereTheViewShowsThemDirectly()
    {
        foreach (string id in GroupRows())
        {
            Assert.False(SettingsGroupRules.IsFlat(id, WorkView.Barebones));
            Assert.Equal(WorkViewPolicy.Get(id).Simple == Presence.Direct, SettingsGroupRules.IsFlat(id, WorkView.Simple));
            Assert.True(SettingsGroupRules.IsFlat(id, WorkView.Advanced));
        }
    }

    [Fact]
    public void ChangedValuesAndErrorsOpenGroupsOnlyWhenTheyAreNew()
    {
        Assert.False(SettingsGroupRules.IsNonDefault(0, false));
        Assert.True(SettingsGroupRules.IsNonDefault(1, false));
        Assert.True(SettingsGroupRules.IsNonDefault(0, true));
        // The first change, a further change and a first error open; the same state again leaves a closed group closed.
        Assert.True(SettingsGroupRules.ShouldOpen(0, 1, false, false));
        Assert.True(SettingsGroupRules.ShouldOpen(1, 2, false, false));
        Assert.True(SettingsGroupRules.ShouldOpen(0, 0, false, true));
        Assert.False(SettingsGroupRules.ShouldOpen(1, 1, false, false));
        Assert.False(SettingsGroupRules.ShouldOpen(1, 1, true, true));
        // A change that is taken back never opens anything.
        Assert.False(SettingsGroupRules.ShouldOpen(2, 1, false, false));
    }

    [Fact]
    public void HeaderCarriesTheCounterAndTheChangedCountOnlyWhenThereIsOne()
    {
        Func<int, string> changed = count => count + " changed";
        Assert.Equal("More settings (29)", SettingsGroupRules.Header("More settings", 29, 0, changed));
        Assert.Equal("More settings (29) · 3 changed", SettingsGroupRules.Header("More settings", 29, 3, changed));
    }

    // R13/R6 on real expanders: a view change collapses or opens, a non-default state opens, a session choice survives without a view change.
    [Fact]
    public void ExpandersFollowTheViewAndOpenOnStateInEveryView()
    {
        foreach (string id in GroupRows())
        {
            Expander group = new();
            WorkViewElement.SetId(group, id);
            foreach (WorkView view in new[] { WorkView.Barebones, WorkView.Simple })
            {
                // A group the view shows directly opens on entering it; only a collapsed one closes.
                bool collapsed = WorkViewPolicy.Resolve(id, view) == Presence.Collapsed;
                group.IsExpanded = true;
                WorkViewPresenter.ApplyExpander(group, view, viewChanged: true);
                Assert.Equal(!collapsed, group.IsExpanded);
                group.IsExpanded = false;
                WorkViewPresenter.ApplyExpander(group, view, viewChanged: true);
                Assert.Equal(!collapsed, group.IsExpanded);
                WorkViewPresenter.ApplyExpander(group, view, viewChanged: true, nonDefaultState: true);
                Assert.True(group.IsExpanded, id + " with a change or an error in " + view);
                group.IsExpanded = false;
                WorkViewPresenter.ApplyExpander(group, view, viewChanged: false);
                Assert.False(group.IsExpanded); // the user's choice stays until the view changes
            }
            group.IsExpanded = false;
            WorkViewPresenter.ApplyExpander(group, WorkView.Advanced, viewChanged: true);
            Assert.True(group.IsExpanded);
        }
    }

    // The editor finds groups by prefix: a group of a later package slots in with a row and an expander, without a list here.
    [Fact]
    public void GroupIdsFollowTheGenericShapeSoLaterGroupsSlotIn()
    {
        Assert.All(GroupRows(), id => Assert.Matches(@"^settings\.group\.[a-z]+(-[a-z]+)*$", id));
        string editor = File.ReadAllText(Path.Combine(RepositoryRoot(), "Desktop/Views/Settings/SettingsEditor.WorkViews.cs"));
        Assert.Contains("WorkViewPolicy.SettingsGroupPrefix", editor);
        Assert.DoesNotContain("\"settings.group.", editor);
    }
}
