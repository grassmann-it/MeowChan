using System.Runtime.CompilerServices;
using System.Xml.Linq;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Plugins;
using MeowChan.Desktop.WorkViews;
using MeowChan.PluginHost.Activation;
using MeowChan.PluginSdk;

namespace MeowChan.DesktopTests;

[Collection("Localization")]
public sealed class SourcesPluginsMergeTests
{
    [Fact]
    public void DescribedInactiveSourcesFollowRegisteredOnesAndHaveNoConsentOrCaptureAuthority()
    {
        SourceRow registered = Registered("z-active", "Z active");
        PluginCatalogEntry plugin = Plugin("owner", "candidate", Source("a-inactive", "A inactive"));
        SourceListRow[] rows = SourceListRows.Merge([registered], [plugin]).ToArray();
        Assert.Equal(["z-active", "a-inactive"], rows.Select(row => row.Id));
        Assert.True(rows[0].IsActive);
        Assert.False(rows[1].IsActive);
        Assert.Same(plugin, rows[1].Plugin);
        Assert.Null(rows[1].MediaIntegrityPolicy);
        Assert.False(rows[1].Descriptor.CaptureAvailable);
        Assert.Empty(rows[1].Watches);
        Assert.Contains(rows[1].Badges, badge => badge.Kind == SourceBadgeKind.Status && badge.Deviates);
        Assert.Equal(ProviderCapabilities.ThreadWatch | ProviderCapabilities.OriginalMedia, rows[1].Descriptor.Capabilities);
    }

    [Fact]
    public void APluginWithSeveralSourcesGetsOneRowPerSourceButNeverOnePerFile()
    {
        PluginCatalogEntry first = Plugin("owner", "candidate-a", Source("one", "One"), Source("two", "Two"));
        PluginCatalogEntry second = first with { Key = "candidate-b", CandidateId = "candidate-b" };
        var rows = SourceListRows.Merge([], [second, first]);
        Assert.Equal(["one", "two"], rows.Select(row => row.Id));
        Assert.All(rows, row => Assert.Equal("candidate-a", row.Plugin!.Key));
    }

    [Fact]
    public void ASelectedBuildSuppliesTheMetadataAndActionTargetInsteadOfTheFirstFile()
    {
        PluginCatalogEntry old = Plugin("owner", "a", Source("old", "Old"));
        PluginCatalogEntry selected = Plugin("owner", "b", Source("new", "New"));
        var activation = new PluginActivationSnapshot("owner", false, 1, "b", null, null, null, null, [], null);
        old = old with { Activation = activation };
        selected = selected with { Activation = activation };
        SourceListRow row = Assert.Single(SourceListRows.Merge([], [old, selected]));
        Assert.Equal("new", row.Id);
        Assert.Same(selected, row.Plugin);
        Assert.False(row.IsActive); // enabled/selected next start is not a provider registered in this session
    }

    [Fact]
    public void RegisteredObservationsAreRetainedAndAnUnrelatedDescriptionCannotReplaceThem()
    {
        SourceRow registered = Registered("source", "Host name");
        var rows = SourceListRows.Merge([registered], [Plugin("one", "a", Source("source", "Self description"))]);
        SourceListRow row = Assert.Single(rows);
        Assert.Same(registered, row.Source);
        Assert.Equal("Host name", row.Name);
        Assert.Equal("Host observation", row.Summary);
    }

    [Fact]
    public void CollidingOwnersNeverAcquireAnArbitraryActionOrConsentTarget()
    {
        SourceRow registered = Registered("shared", "Registered");
        var rows = SourceListRows.Merge([registered], [Plugin("one", "a", Source("shared", "One")), Plugin("two", "b", Source("shared", "Two"))]);
        SourceListRow row = Assert.Single(rows);
        Assert.Null(row.Plugin);
        Assert.Null(row.MediaIntegrityPolicy);
        Assert.Same(registered, row.Source);
    }

    [Fact]
    public void AmbiguousInactiveDeclarationsRemainVisibleWithoutAnArbitraryOwner()
    {
        var entries = new[] { Plugin("one", "a", Source("shared", "One")), Plugin("two", "b", Source("shared", "Two")) };
        var row = Assert.Single(SourceListRows.Merge([], entries));
        var reversed = Assert.Single(SourceListRows.Merge([], entries.Reverse().ToArray()));
        Assert.Equal(row.Name, reversed.Name);
        Assert.Null(row.Plugin); Assert.Null(row.MediaIntegrityPolicy); Assert.False(row.IsActive);
        Assert.Contains(row.Badges, badge => badge.Kind == SourceBadgeKind.Status && badge.Deviates);
    }

    [Fact]
    public void ExperimentalInactiveSourcesSortAfterUnclassifiedOnes()
    {
        var experimental = Source("experimental", "A experimental") with { Maturity = SourceMaturity.Experimental };
        var rows = SourceListRows.Merge([], [Plugin("owner", "a", experimental, Source("ordinary", "Z ordinary"))]);
        Assert.Equal(["ordinary", "experimental"], rows.Select(row => row.Id));
    }

    [Fact]
    public void AnUnknownNewFileDoesNotInventASourceRow()
    {
        PluginCatalogEntry unknown = new("new", null, "new", "/fixture/new.cs", null, null, null, []);
        Assert.Empty(SourceListRows.Merge([], [unknown]));
        Assert.Equal(2, (int)SourcesPluginsFilter.AllPlugins);
    }

    [Fact]
    public void TheDropdownIncludesHybridPluginsAndKeepsUnknownFilesInAllPlugins()
    {
        PluginCatalogEntry source = Plugin("owner", "a", Source("one", "One"));
        PluginCatalogEntry unknown = new("new", null, "new", null, null, null, null, []);
        Assert.True(SourcesPluginsFilterPolicy.Includes(source, SourcesPluginsFilter.Sources));
        Assert.False(SourcesPluginsFilterPolicy.Includes(source, SourcesPluginsFilter.ImageFilters));
        Assert.True(SourcesPluginsFilterPolicy.Includes(source, SourcesPluginsFilter.AllPlugins));
        Assert.False(SourcesPluginsFilterPolicy.Includes(unknown, SourcesPluginsFilter.Sources));
        Assert.False(SourcesPluginsFilterPolicy.Includes(unknown, SourcesPluginsFilter.ImageFilters));
        Assert.True(SourcesPluginsFilterPolicy.Includes(unknown, SourcesPluginsFilter.AllPlugins));
        var hybrid = source with { Description = source.Description! with
        { Filters = [new("image", "Image filter", "Fixture", FilterDataAccess.CandidateMetadata, new("1", []), ["image/png"])] } };
        Assert.True(SourcesPluginsFilterPolicy.Includes(hybrid, SourcesPluginsFilter.Sources));
        Assert.True(SourcesPluginsFilterPolicy.Includes(hybrid, SourcesPluginsFilter.ImageFilters));
        Assert.True(SourcesPluginsFilterPolicy.Includes(hybrid, SourcesPluginsFilter.AllPlugins));
    }

    [Fact]
    public void TheShellHasOneMergedDestinationAndTheCompatibilityRouteHasNoFooterOrDefaultDigit()
    {
        Assert.DoesNotContain("navigation.plugins", MainPageOrder.CommandIds);
        Assert.Equal(MainWindow.WorkspacePage.Sources, MainWindow.PageFor("navigation.plugins"));
        Assert.Null(CommandCatalog.Get("navigation.plugins").Shortcut);
        Assert.DoesNotContain("footer", CommandCatalog.Get("navigation.plugins").Contexts);
        Assert.Null(MainPageOrder.Digit("navigation.plugins"));
        Assert.Equal(7, MainPageOrder.Digit("navigation.status"));
        Assert.Equal("Primary+7", CommandCatalog.Get("navigation.status").Shortcut);
        string shell = Read("Desktop/Shell/MainWindow.axaml");
        Assert.DoesNotContain("PluginsNavigationButton", shell);
        Assert.DoesNotContain("PluginsPageHost", shell);
        Assert.DoesNotContain("PluginsMenuItem", shell);
    }

    [Fact]
    public void TheMergedSurfaceHasOneHeaderDropdownARealSourceListAndNoSectionsNavigator()
    {
        string source = Read("Desktop/Views/Sources/SourcesPage.axaml");
        string plugins = Read("Desktop/Views/Plugins/PluginsPage.axaml");
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var sourceXml = XDocument.Parse(source);
        var pluginXml = XDocument.Parse(plugins);
        Assert.Contains(sourceXml.Descendants(), element => element.Name.LocalName == "PluginSelectionList" && (string?)element.Attribute(x + "Name") == "SourceCards");
        Assert.Contains(sourceXml.Descendants(), element => element.Name.LocalName == "PluginsPage.SourceContent");
        Assert.Single(pluginXml.Descendants().Where(element => (string?)element.Attribute(x + "Name") == "PluginCategory"));
        Assert.DoesNotContain("PluginSections", plugins);
        Assert.DoesNotContain("PluginNoSelection", plugins);
        Assert.DoesNotContain("PluginListActions", plugins);
        Assert.DoesNotContain("PluginDirectoryExpander", plugins);
        Assert.Contains("SourcePluginBlock", source);
        Assert.Contains("SourcePluginSdk", source);
        Assert.Contains("PluginApproveSdkTransition", plugins);
        Assert.Contains("PluginDraftFooter", plugins);
    }

    [Fact]
    public void SafetyRowsBelongToSourcesWithoutChangingTheirPresence()
    {
        foreach (string id in new[] { "plugins.header.review-repairs", "plugins.header.inventory-problems", "plugins.header.error",
            "plugins.detail.sdk-transition", "plugins.detail.draft-footer", "sources.detail.media-integrity", "sources.detail.watches", "sources.detail.error" })
        {
            WorkViewEntry entry = WorkViewPolicy.Get(id);
            Assert.Equal(WorkViewPage.Sources, entry.Page);
            Assert.True(entry.Safety);
            Assert.All(WorkViewSetting.All, view => Assert.Equal(Presence.Required, entry.For(view)));
        }
        Assert.Equal(Presence.Overflow, WorkViewPolicy.Resolve("sources.detail.plugin", WorkView.Barebones));
        Assert.Equal(Presence.Overflow, WorkViewPolicy.Resolve("sources.detail.plugin", WorkView.Simple));
        Assert.Equal(Presence.Direct, WorkViewPolicy.Resolve("sources.detail.plugin", WorkView.Advanced));
    }

    [Fact]
    public void TheParentCatalogHookContainsUniqueBalancedEnglishGermanEntries()
    {
        Assert.Equal(SourcesPluginsCatalog.Entries.Length, SourcesPluginsCatalog.Entries.Select(entry => entry.Key).Distinct().Count());
        foreach (var entry in SourcesPluginsCatalog.Entries)
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.English)); Assert.False(string.IsNullOrWhiteSpace(entry.German));
            Assert.Equal(entry.English.Count(c => c == '{'), entry.German.Count(c => c == '{'));
        }
        Assert.Contains(SourcesPluginsCatalog.Entries, entry => entry.Key == "nav.sources" && entry.German == "Quellen & Plugins");
    }

    private static SourceDescription Source(string id, string name) => new(id, name, new Uri("https://fixture.invalid/"),
        SourceCapabilities.ThreadRead | SourceCapabilities.OriginalMedia, [], [], new("1", []));
    private static PluginCatalogEntry Plugin(string owner, string key, params SourceDescription[] sources) => new(key, owner, key, "/fixture/plugin.cs",
        new(owner, "Fixture plugin", "1.0", SdkContract.Version, "Description", sources, [], new("1", []), []), null, null, []);
    private static SourceRow Registered(string id, string name) => new(new(id, name, new Uri("https://registered.invalid/"),
        ProviderCapabilities.ThreadWatch, [], "1", true), "Host observation", []);
    private static string Read(string relative, [CallerFilePath] string source = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "../..", relative)));
}
