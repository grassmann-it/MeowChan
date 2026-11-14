using System.Runtime.CompilerServices;
using System.Xml.Linq;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

/// <summary>
/// Tom, 14.11.2026: the merged page is plain "Plugins" again (command ids and Ctrl digits unchanged), and the read-only folder
/// sources moved from that page to Settings › Storage and limits as the group "Media folders".
/// </summary>
[Collection("Localization")]
public sealed class PluginsRenameMediaFoldersTests : IDisposable
{
    private readonly string _language = Texts.Language;
    public PluginsRenameMediaFoldersTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage(_language); }

    [Fact]
    public void TheMergedPageIsCalledPluginsInEnglishAndGermanEverywhereItIsNamed()
    {
        CommandDefinition command = CommandCatalog.Get("navigation.sources");
        Assert.Equal("Plugins", command.LabelEn);
        Assert.Equal("Plugins", command.LabelDe);
        foreach (string language in new[] { "en", "de" })
        {
            Texts.SetLanguage(language);
            Assert.Equal("Plugins", Texts.T("nav.sources"));
            Assert.Equal("Plugins", Texts.T("command.navigation.sources"));
            Assert.Equal("Plugins", CommandCatalog.Label("navigation.sources"));
            Assert.Equal("Plugins", ShortcutGroups.Label("sources"));
            Assert.DoesNotContain("&", Texts.T("nav.sources"));
            Assert.DoesNotContain("Quellen", Texts.T("nav.sources"));
        }
        // The command ids and the Ctrl digits stay, so saved bindings remain valid.
        Assert.Equal((int?)5, MainPageOrder.Digit("navigation.sources"));
        Assert.Null(MainPageOrder.Digit("navigation.plugins"));
        Assert.Equal("Show all plugins", CommandCatalog.Get("navigation.plugins").LabelEn);
    }

    [Fact]
    public void EveryBundledPackNamesThePageLikeItsPluginsWord()
    {
        foreach (string code in Texts.BundledPackCodes())
        {
            Texts.SetLanguage(code);
            Assert.Equal(Texts.T("nav.plugins"), Texts.T("nav.sources"));
            Assert.Equal(Texts.T("nav.plugins"), Texts.T("command.navigation.sources"));
            Assert.NotEqual("nav.sources", Texts.T("nav.sources"));
            foreach (string key in new[] { "folderSource.section.title", "folderSource.section.limitsHint", "budgets.group.folderSources", "sources.mediaFoldersLink" })
                Assert.NotEqual(key, Texts.T(key));
        }
    }

    [Fact]
    public void TheMediaFoldersGroupAndItsLimitsAreNamedApartAndTheLinkSaysWhereTheyAre()
    {
        Assert.Equal("Media folders", Texts.T("folderSource.section.title"));
        Assert.Equal("Media folder limits", Texts.T("budgets.group.folderSources"));
        Assert.Equal("Media folders are in Settings › Storage and limits", Texts.T("sources.mediaFoldersLink"));
        Texts.SetLanguage("de");
        Assert.Equal("Medienordner", Texts.T("folderSource.section.title"));
        Assert.Equal("Grenzen der Medienordner", Texts.T("budgets.group.folderSources"));
        Assert.Contains(Texts.T("settings.storageLimits"), Texts.T("sources.mediaFoldersLink"));
    }

    [Fact]
    public void TheSettingsSearchFindsTheMediaFoldersAndTheirLimitsByFolderWords()
    {
        foreach (string word in new[] { "folder", "ordner", "medienordner", "directory", "verzeichnis" })
        {
            Assert.Contains(SettingsSearchAliases.For("MediaFolders", "settings.worker-budgets"), aliases => aliases.Contains(word, StringComparison.Ordinal));
            Assert.Contains(SettingsSearchAliases.For("FolderSourceMaxFiles", "settings.worker-budgets"), aliases => aliases.Contains(word, StringComparison.Ordinal));
        }
        Assert.Equal("MediaFolders", SettingsFieldId.MediaFolders);
    }

    [Fact]
    public void TheGroupSitsOnTheStoragePageBeforeTheLimitsAndOutsideEveryWorkViewGroup()
    {
        XDocument markup = XDocument.Parse(Read("Desktop/Views/Settings/SettingsEditor.axaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement Named(string name) => markup.Descendants().Single(element => (string?)element.Attribute(x + "Name") == name);
        XElement page = Named("SettingsWorkerBudgetsPage");
        XElement group = Named("MediaFolders"), host = Named("MediaFoldersHost"), limits = Named("SettingsLimitsGroup");
        Assert.Same(page, group.Parent);
        Assert.Same(page, host.Parent);
        Assert.Same(page, limits.Parent);
        XElement[] order = page.Elements().ToArray();
        Assert.True(Array.IndexOf(order, group) < Array.IndexOf(order, host) && Array.IndexOf(order, host) < Array.IndexOf(order, limits));
        // The heading is the search entry; the limit rows keep their own group (and the folder rows their Input names).
        Assert.Contains(group.Descendants(), element => (string?)element.Attribute(x + "Name") == "MediaFoldersHeading");
        Assert.Contains(limits.Descendants(), element => (string?)element.Attribute(x + "Name") == "FolderSourceMaxFilesInput");
        Assert.DoesNotContain("settings.group.", string.Concat(group.Attributes().Select(attribute => attribute.Value)));
    }

    [Fact]
    public void ThePluginsPageHasNoFolderSectionAnymoreButPointsToSettings()
    {
        string axaml = Read("Desktop/Views/Sources/SourcesPage.axaml");
        string code = Read("Desktop/Views/Sources/SourcesPage.axaml.cs");
        Assert.DoesNotContain("FolderSources", axaml + code);
        Assert.DoesNotContain("FolderSourcesPanel", Read("Desktop/Shell/MainWindow.Navigation.cs").Split("new SourcesPage(")[1].Split("SourcesPageHost")[0]);
        Assert.Contains("sources.mediaFoldersLink", axaml);
        Assert.Contains("OpenMediaFolders = () => ShowSettingsAt(SettingsFieldId.MediaFolders)", Read("Desktop/Shell/MainWindow.Navigation.cs"));
        Assert.Contains("_settingsEditor?.RefreshFolderSources();", Read("Desktop/Shell/MainWindow.FolderSources.cs"));
        Assert.False(File.Exists(Path.Combine(Root(), "Desktop/Views/Sources/SourcesPage.FolderSources.cs")));
    }

    private static string Root([CallerFilePath] string source = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "../.."));
    private static string Read(string relative) => File.ReadAllText(Path.Combine(Root(), relative));
}
