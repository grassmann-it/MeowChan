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
    public void MainPageAndShortcutFollowTheFooterNumbering()
    {
        Assert.Equal("navigation.booru", MainPageOrder.CommandIds[4]); // behind Media and Tagging
        // F1: Booru is hidden in the default footer, so it is numbered behind the visible buttons (Ctrl/Cmd+8); showing it moves the digits.
        Assert.Equal(8, MainPageOrder.Digit("navigation.booru"));
        Assert.Equal("Primary+8", MainPageOrder.DefaultShortcut("navigation.booru"));
        Assert.Null(CommandCatalog.Get("navigation.booru").Shortcut);
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
        Assert.DoesNotContain("BooruMenuItem.IsVisible", host, StringComparison.Ordinal); // the ⋯ menu has no Booru entry any more (F1)
        Assert.Contains("BooruMoreMenuItem.IsVisible = false", host, StringComparison.Ordinal);
        Assert.DoesNotContain("ShowBooruToolsToggle", markup, StringComparison.Ordinal); // F1: the switch moved to Settings
        Assert.Contains("BooruPageHost", markup, StringComparison.Ordinal);
        Assert.Contains("BooruNavigationButton", markup, StringComparison.Ordinal);
    }

    [Fact]
    public void FooterMoreAndAppMenuKeepTheSameBooruOrder()
    {
        XDocument markup = Shell();
        XElement footer = Assert.Single(markup.Descendants(), element => Name(element) == "WorkspaceNavigationButtons");
        string[] buttons = footer.Elements().Select(Name).OfType<string>().Where(name => name.EndsWith("NavigationButton", StringComparison.Ordinal)).ToArray();
        Assert.Equal("TaggingNavigationButton", buttons[3]); // Tagging sits behind Media, Booru keeps its place right behind it
        Assert.Equal("BooruNavigationButton", buttons[4]);

        XElement flyout = Assert.Single(markup.Descendants(), element => Name(element) == "ToolsNavigationButton");
        Assert.Equal(new[] { "nav.archive", "nav.media", "nav.tagging", "nav.booru" },
            flyout.Descendants().Where(element => element.Name.LocalName == "MenuItem")
                .Select(element => (string?)element.Attribute("Tag")).Take(4).ToArray());

        XElement appMenu = Assert.Single(markup.Descendants(), element => Name(element) == "WorkspaceMenuItem");
        // F1: the ⋯ menu holds no page entries; the footer and its More menu are the only routes (and the Ctrl/Cmd+digit shortcuts).
        Assert.DoesNotContain(appMenu.Elements(), element => ((string?)element.Attribute("Tag"))?.StartsWith("nav.", StringComparison.Ordinal) == true
            && (string?)element.Attribute("Tag") is not ("nav.settings" or "nav.about"));
        Assert.DoesNotContain(markup.Descendants(), element => Name(element) == "BooruMenuItem");
    }

    [Fact]
    public void PersistedPluginShortcutOnTheBooruSlotWinsOverTheBooruDefault()
    {
        foreach (ShortcutPlatform platform in Enum.GetValues<ShortcutPlatform>())
        {
            ShortcutGesture six = ShortcutGesture.Parse(MainPageOrder.DefaultShortcut("navigation.booru")); // slot 6 before F1, now the footer digit
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
    public void BooruAvailabilityChangesRefreshTheFooterButtonAndMoreEntry()
    {
        // Booru composed or revoked at runtime (OnBooruChanged), "Show Booru actions" or the session state changing (RefreshBooruToolsPresentation)
        // must run the footer refresh, or the button and the More entry stay stale until the next resize or page change.
        string host = Read("Desktop/Booru/MainWindow.BooruNavigation.cs");
        string navigation = Read("Desktop/Shell/MainWindow.Navigation.cs");

        string changed = Between(host, "private void OnBooruChanged()", "private void RefreshBooruToolsPresentation()");
        Assert.Contains("SyncBooruNavigation();", changed, StringComparison.Ordinal);
        Assert.True(changed.IndexOf("SyncBooruNavigation();", StringComparison.Ordinal) < changed.IndexOf("UpdateInteractionState();", StringComparison.Ordinal),
            "The footer must be refreshed before the interaction state enables the Booru button.");

        string presentation = Between(host, "private void RefreshBooruToolsPresentation()", "private void SyncBooruNavigation()");
        Assert.Contains("SyncBooruNavigation();", presentation, StringComparison.Ordinal);

        // The sync is a no-op until the first refresh and when the availability the last refresh used is unchanged (UpdateInteractionState runs often).
        string sync = Between(host, "private void SyncBooruNavigation()", "private void RequestBooruUnavailableFallback()");
        Assert.Contains("_booruNavigationAvailable is not { } built || built == CanOpenBooru || _isClosing", sync, StringComparison.Ordinal);
        Assert.Contains("UpdatePageNavigation();", sync, StringComparison.Ordinal);

        // The refresh records the availability it builds from; the guard against re-entry comes first so a nested call does not record.
        string update = Between(navigation, "private void UpdatePageNavigation()", "_measuringPageNavigation = true;");
        Assert.Contains("if (_measuringPageNavigation) return;", update, StringComparison.Ordinal);
        Assert.Contains("_booruNavigationAvailable = CanOpenBooru;", update, StringComparison.Ordinal);
    }

    private static string Between(string text, string from, string to)
    {
        int start = text.IndexOf(from, StringComparison.Ordinal);
        Assert.True(start >= 0, "Missing: " + from);
        int end = text.IndexOf(to, start + from.Length, StringComparison.Ordinal);
        Assert.True(end > start, "Missing: " + to);
        return text[start..end];
    }

    [Fact]
    public void CompositionSwitchDisposesByReferenceAndDoesNotRecreateOnRepeatedOpen()
    {
        string host = Read("Desktop/Booru/MainWindow.BooruNavigation.cs");
        Assert.Contains("ReferenceEquals(current.Services, services)", host, StringComparison.Ordinal);
        Assert.Contains("ReferenceEquals(page.Services, services)", host, StringComparison.Ordinal);
        Assert.Contains("Dispatcher.UIThread.Post(OnBooruChanged)", host, StringComparison.Ordinal);
        Assert.Contains("RequestBooruUnavailableFallback();", host, StringComparison.Ordinal); // turning Booru off in Settings leaves the page this way (F1)
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
        int recheck = bridge.IndexOf("if (!Current() || !interactionCurrent()) return Cancelled();", activation, StringComparison.Ordinal);
        int selection = bridge.IndexOf("await page.ShowBooruPostAsync(postId, Current, cancelNotice)", StringComparison.Ordinal);
        Assert.True(capture >= 0 && activation > capture && recheck > activation && selection > recheck,
            "A delayed media activation must revalidate navigation and source lifetime before changing selection.");
    }

    // Source-wiring regression only. Native shell execution must additionally cover the actual
    // asynchronous pause owner, service loss and completed Downloads navigation.
    [Theory]
    [InlineData("private async Task ApplySettingsAsync(Config candidate)", "private void ApplyAppearance()")]
    [InlineData("private async void OnPauseClick(object? sender, RoutedEventArgs e)", "private async void OnAboutMenuItemClick")]
    public void BothPreferenceOwnersRetryPendingBooruFallbackOnlyAfterReleasingTheirBusyGate(string start, string next)
    {
        string source = Read("Desktop/Shell/MainWindow.axaml.cs");
        int begin = source.IndexOf(start, StringComparison.Ordinal);
        int end = source.IndexOf(next, begin, StringComparison.Ordinal);
        Assert.True(begin >= 0 && end > begin);
        string owner = source[begin..end];
        int cleanup = owner.IndexOf("finally", StringComparison.Ordinal);
        int release = owner.IndexOf("_applyingPreferences = false;", cleanup, StringComparison.Ordinal);
        int alive = owner.IndexOf("if (!_isClosing)", release, StringComparison.Ordinal);
        int refresh = owner.IndexOf("UpdateInteractionState();", alive, StringComparison.Ordinal);
        int retry = owner.IndexOf("TryCompleteBooruUnavailableFallback();", refresh, StringComparison.Ordinal);
        Assert.True(cleanup >= 0 && release > cleanup && alive > release && refresh > alive && retry > refresh,
            "The real preference owner must release the gate and retry a pending fallback without navigating during teardown.");
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
