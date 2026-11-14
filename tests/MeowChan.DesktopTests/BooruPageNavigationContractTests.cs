using System.Runtime.CompilerServices;
using System.Xml.Linq;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.WorkViews;

namespace MeowChan.DesktopTests;

/// <summary>
/// BP2 page-host contracts that do not require a Window, renderer, profile, provider or dispatcher.
/// Runtime page lifetime and local/no-network behavior remain covered by BooruPageHostContractTests.
/// </summary>
public sealed class BooruPageNavigationContractTests
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static string Root([CallerFilePath] string path = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "../.."));

    private static string Read(string relative) => File.ReadAllText(Path.Combine(Root(), relative));

    private static XDocument Shell() => XDocument.Load(Path.Combine(Root(), "Desktop/Shell/MainWindow.axaml"));

    private static string? Name(XElement element) => (string?)element.Attribute(X + "Name");

    [Fact]
    public void MainPageAndShortcutUseTheFormerlyFreeSixSlot()
    {
        Assert.Equal("navigation.booru", MainPageOrder.CommandIds[3]);
        Assert.Equal(6, MainPageOrder.Digit("navigation.booru"));
        Assert.Equal("Primary+6", CommandCatalog.Get("navigation.booru").Shortcut);
        Assert.Equal(MainWindow.WorkspacePage.Booru, MainWindow.PageFor("navigation.booru"));
        Assert.Equal("Booru", CommandCatalog.Get("navigation.booru").LabelEn);
    }

    [Theory]
    [InlineData(WorkView.Barebones, Presence.Overflow)]
    [InlineData(WorkView.Simple, Presence.Direct)]
    [InlineData(WorkView.Advanced, Presence.Direct)]
    public void BooruFooterFollowsThePresentationMatrix(WorkView view, Presence expected) =>
        Assert.Equal(expected, WorkViewPolicy.Resolve("shell.footer.booru", view));

    [Fact]
    public void OffAndUnavailableTargetsAreRemovedWithoutRemovingThePageHostContract()
    {
        string navigation = Read("Desktop/Shell/MainWindow.Navigation.cs");
        string host = Read("Desktop/Booru/MainWindow.BooruNavigation.cs");
        string markup = Read("Desktop/Shell/MainWindow.axaml");

        Assert.Contains("id != \"navigation.booru\" || CanOpenBooru", navigation, StringComparison.Ordinal);
        Assert.Contains("if (_selectedPage == WorkspacePage.Booru)", host, StringComparison.Ordinal);
        Assert.Contains("BooruMenuItem.IsVisible = CanOpenBooru", host, StringComparison.Ordinal);
        Assert.Contains("BooruMoreMenuItem.IsVisible = false", host, StringComparison.Ordinal);
        Assert.Contains("ShowBooruToolsToggle", markup, StringComparison.Ordinal);
        Assert.Contains("BooruPageHost", markup, StringComparison.Ordinal);
        Assert.Contains("BooruNavigationButton", markup, StringComparison.Ordinal);
    }

    [Fact]
    public void FooterMoreAndAppMenuKeepTheSameBooruOrder()
    {
        XDocument markup = Shell();
        XElement footer = Assert.Single(markup.Descendants(), element => Name(element) == "WorkspaceNavigationButtons");
        string[] buttons = footer.Elements().Select(Name).OfType<string>().Where(name => name.EndsWith("NavigationButton", StringComparison.Ordinal)).ToArray();
        Assert.Equal("BooruNavigationButton", buttons[3]);

        XElement flyout = Assert.Single(markup.Descendants(), element => Name(element) == "ToolsNavigationButton");
        Assert.Equal(new[] { "nav.archive", "nav.media", "nav.booru" },
            flyout.Descendants().Where(element => element.Name.LocalName == "MenuItem")
                .Select(element => (string?)element.Attribute("Tag")).Take(3).ToArray());

        XElement appMenu = Assert.Single(markup.Descendants(), element => Name(element) == "WorkspaceMenuItem");
        string[] tags = appMenu.Elements().Select(element => (string?)element.Attribute("Tag"))
            .Where(tag => tag != null).OfType<string>().ToArray();
        Assert.Equal("nav.booru", tags[3]);
    }

    [Fact]
    public void PersistedPluginShortcutSixWinsOverTheNewBooruDefault()
    {
        foreach (ShortcutPlatform platform in Enum.GetValues<ShortcutPlatform>())
        {
            ShortcutGesture six = ShortcutGesture.Parse("Primary+6");
            var defaults = EffectiveShortcutBindings.Build(platform: platform);
            Assert.Contains(defaults.For("navigation.booru"), gesture =>
                EffectiveShortcutBindings.Equivalent(six, gesture, platform));

            var saved = EffectiveShortcutBindings.Build(
                new ShortcutBindingsSection().Set("navigation.plugins", [six]), platform);
            Assert.Empty(saved.For("navigation.booru"));
            Assert.Single(saved.For("navigation.plugins"));
            Assert.Contains(saved.Issues, issue => issue.CommandId == "navigation.booru"
                && issue.OtherCommandId == "navigation.plugins"
                && issue.Reason?.Code == "defaultYields");
        }
    }

    [Fact]
    public void CompositionSwitchDisposesByReferenceAndDoesNotRecreateOnRepeatedOpen()
    {
        string host = Read("Desktop/Booru/MainWindow.BooruNavigation.cs");
        Assert.Contains("ReferenceEquals(current.Services, services)", host, StringComparison.Ordinal);
        Assert.Contains("ReferenceEquals(page.Services, services)", host, StringComparison.Ordinal);
        Assert.Contains("Dispatcher.UIThread.Post(OnBooruChanged)", host, StringComparison.Ordinal);
        Assert.Contains("if (!visible && _selectedPage == WorkspacePage.Booru)", host, StringComparison.Ordinal);
        Assert.Contains("page.Dispose();", host, StringComparison.Ordinal);
        Assert.Contains("ReferenceEquals(_booruPage, page)", Read("Desktop/Booru/MainWindow.Booru.cs"), StringComparison.Ordinal);
    }

    [Fact]
    public void OpenAndQueryHandoffsOnlyNavigateAndPrefill()
    {
        string booru = Read("Desktop/Booru/MainWindow.Booru.cs");
        Assert.Contains("NavigateWorkspace(WorkspacePage.Booru)", booru, StringComparison.Ordinal);
        Assert.Contains("page.ShowQuery(query)", booru, StringComparison.Ordinal);
        Assert.DoesNotContain("SearchAsync", booru, StringComparison.Ordinal);
        Assert.Contains("BooruPage", Read("Desktop/Booru/MainWindow.BooruNavigation.cs"), StringComparison.Ordinal);
    }

    [Fact]
    public void MediaOverflowIsAttachedAtTheRealCreationPoint()
    {
        string media = Read("Desktop/Shell/MainWindow.Media.cs");
        int content = media.IndexOf("MediaPageHost.Content = _mediaPage", StringComparison.Ordinal);
        int attach = media.IndexOf("AttachBooruOverflow(_mediaPage)", content, StringComparison.Ordinal);
        Assert.True(content >= 0 && attach > content, "The Booru overflow hook must follow MediaPage creation.");
        Assert.Contains("ConfigureBooruMediaLinks(BooruPage page)", Read("Desktop/Booru/MainWindow.BooruMedia.cs"), StringComparison.Ordinal);
    }

    [Fact]
    public void LocalMediaHandoffUsesThePageLifetimeAndRechecksAfterActivation()
    {
        string bridge = Read("Desktop/Booru/MainWindow.BooruMedia.cs");
        Assert.DoesNotContain("ConfigureBooruMediaLinks(BooruWorkbenchWindow", bridge, StringComparison.Ordinal);
        Assert.DoesNotContain("_booruWindow", Read("Desktop/Booru/MainWindow.Booru.cs"), StringComparison.Ordinal);
        Assert.Contains("CaptureMediaPageMenuGuard()", bridge, StringComparison.Ordinal);
        Assert.Contains("ReferenceEquals(_booruPage, sourcePage)", bridge, StringComparison.Ordinal);
        Assert.Contains("ReferenceEquals(_session.BooruServices, services)", bridge, StringComparison.Ordinal);
        int activation = bridge.IndexOf("await page.ActivateAsync()", StringComparison.Ordinal);
        int capture = bridge.IndexOf("page.CaptureBooruHandoffInteraction()", StringComparison.Ordinal);
        int recheck = bridge.IndexOf("if (!Current() || !interactionCurrent()) return false;", activation, StringComparison.Ordinal);
        int selection = bridge.IndexOf("await page.ShowBooruPostAsync(postId, Current)", StringComparison.Ordinal);
        Assert.True(capture >= 0 && activation > capture && recheck > activation && selection > recheck,
            "A delayed media activation must revalidate navigation and source lifetime before changing selection.");
    }

    [Fact]
    public void PageConstructionUsesTheExistingLazyActivationAndOwnsNoProviderAction()
    {
        string page = Read("Desktop/Views/Booru/BooruPage.cs");
        Assert.Contains("internal Task ActivateAsync()", page, StringComparison.Ordinal);
        Assert.Contains("_view.ActivateAsync()", page, StringComparison.Ordinal);
        Assert.DoesNotContain("SearchAsync", page, StringComparison.Ordinal);
        Assert.DoesNotContain("ReadPostAsync", page, StringComparison.Ordinal);
        Assert.Contains("public void Dispose()", page, StringComparison.Ordinal);
    }
}
