using System.Text.Json;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using MeowChan.Desktop;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.WorkViews;

namespace MeowChan.DesktopTests;

/// <summary>
/// G2: the slideshow is one split control. The main part toggles, the 24 dip menu button (or the context request of the toggle) opens a
/// flyout with the settings; the settings panel is lent from the "⋯" menu while the flyout is open and goes back when it closes.
/// </summary>
[Collection("Localization")]
public sealed class GallerySlideshowSplitTests : IDisposable
{
    public GallerySlideshowSplitTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage("en"); }

    // ---- the loan of the settings panel ----

    private static (StackPanel Home, StackPanel Panel, ScrollViewer Host, PanelLoan Loan) Menu()
    {
        StackPanel home = new(), panel = new();
        home.Children.Add(new Button()); home.Children.Add(new Border()); // a row and a separator before the panel
        home.Children.Add(panel);
        home.Children.Add(new Border()); home.Children.Add(new Button()); // a separator and a row after it
        ScrollViewer host = new();
        return (home, panel, host, new PanelLoan(home, panel, host));
    }

    [Fact]
    public void LendingTakesThePanelOutOfTheMenuIntoTheFlyoutAndReturningRestoresItsPlace()
    {
        var (home, panel, host, loan) = Menu();
        loan.Lend();
        Assert.True(loan.IsLent);
        Assert.Same(panel, host.Content);
        Assert.DoesNotContain(panel, home.Children);
        Assert.Equal(4, home.Children.Count);
        loan.Return();
        Assert.False(loan.IsLent);
        Assert.Null(host.Content);
        Assert.Equal(2, home.Children.IndexOf(panel)); // between the two separators again
        Assert.Equal(5, home.Children.Count);
        Assert.Same(home, panel.Parent);
    }

    [Fact]
    public void LendingAndReturningTwiceChangesNothing()
    {
        var (home, panel, host, loan) = Menu();
        loan.Return(); // before any loan: the panel is in its place already
        Assert.Equal(2, home.Children.IndexOf(panel));
        loan.Lend(); loan.Lend();
        Assert.Equal(4, home.Children.Count);
        loan.Return(); loan.Return();
        Assert.Equal(5, home.Children.Count);
        Assert.Equal(2, home.Children.IndexOf(panel));
        Assert.Null(host.Content);
    }

    [Fact]
    public void ARowAddedToTheMenuWhileTheFlyoutIsOpenDoesNotMoveThePanelOutOfPlaceTooFar()
    {
        var (home, panel, _, loan) = Menu();
        loan.Lend();
        home.Children.RemoveAt(0); // the menu lost a row in the meantime
        loan.Return();
        Assert.Contains(panel, home.Children);
        Assert.InRange(home.Children.IndexOf(panel), 0, home.Children.Count - 1);
    }

    [Fact]
    public void APanelThatWasNeverInTheMenuIsAppendedOnReturn()
    {
        StackPanel home = new(), panel = new();
        home.Children.Add(new Button());
        ScrollViewer host = new();
        PanelLoan loan = new(home, panel, host);
        loan.Lend();
        Assert.Same(panel, host.Content);
        loan.Return();
        Assert.Equal(1, home.Children.IndexOf(panel));
    }

    // ---- the menu button ----

    [Fact]
    public void TheMenuButtonIsAButtonThatKeepsTheButtonStyle()
    {
        SplitMenuButton button = new();
        Assert.IsAssignableFrom<Button>(button);
        Assert.Equal(typeof(Button), typeof(SplitMenuButton).GetProperty("StyleKeyOverride",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(button));
    }

    [Fact]
    public void TheMenuButtonAnnouncesCollapsedAndExpandedForItsFlyout()
    {
        SplitMenuButton button = new() { Flyout = new Flyout() };
        AutomationPeer? peer = ControlAutomationPeer.CreatePeerForElement(button);
        Assert.NotNull(peer);
        IExpandCollapseProvider provider = Assert.IsAssignableFrom<IExpandCollapseProvider>(peer);
        Assert.True(provider.ShowsMenu);
        Assert.Equal(Avalonia.Automation.ExpandCollapseState.Collapsed, provider.ExpandCollapseState);
    }

    // ---- wiring (source level: the viewer needs a window) ----

    [Fact]
    public void TheMenuButtonIsOneTabStopRightOfTheToggleInOneSplitGroup()
    {
        string menu = ReadSource("Desktop/GalleryViewer.SlideshowMenu.cs");
        Assert.Contains("Width = 24", menu);
        Assert.Contains("_slideshowSplit.Children.Add(_slideshowMenuButton);", menu);
        Assert.Contains("_slideshowMenuButton.Flyout = _slideshowFlyout;", menu); // a click, Enter or Space opens it; HasGalleryPopup sees it
        string overflow = ReadSource("Desktop/GalleryViewer.Overflow.cs");
        Assert.Contains("_slideshowSplit.Children.Insert(0, _slideshowToggle);", overflow);
        Assert.Contains("_focusTools.Children.Insert(Math.Min(1, _focusTools.Children.Count), _slideshowSplit);", overflow);
        // The toggle leaves the bar: the menu button goes with it, the borrowed rows go back first.
        Assert.Contains("if (!inline) CloseSlideshowMenu();", overflow);
    }

    [Fact]
    public void TheContextRequestOfTheToggleOpensTheSameFlyoutOnlyWhileItIsOnTheBar()
    {
        string menu = ReadSource("Desktop/GalleryViewer.SlideshowMenu.cs");
        Assert.Contains("_slideshowToggle.ContextRequested +=", menu);
        Assert.Contains("if (!SlideshowToggleOnBar || e.Handled) return;", menu);
        Assert.Contains("if (SetSlideshowMenuOpen(true)) e.Handled = true;", menu);
    }

    [Fact]
    public void TheFlyoutHoldsTheSlideshowAndGivesFocusBackToItsOrigin()
    {
        string menu = ReadSource("Desktop/GalleryViewer.SlideshowMenu.cs");
        Assert.Contains("HoldSlideshowAdvance();", menu);
        Assert.Contains("_slideshowLoan.Lend();", menu);
        Assert.Contains("ReturnSlideshowHome();", menu);
        Assert.Contains("RaiseExpandedChanged(true)", menu);
        Assert.Contains("RaiseExpandedChanged(false)", menu);
        // Esc and the arrow keys are the overflow menu's own handler: one rule for both ways.
        Assert.Contains("OnMenuKeyDown(e, _slideshowHome, _slideshowFlyout)", menu);
        Assert.Contains("_slideshowMenuButton : _overflowButton", menu);
    }

    [Fact]
    public void TheShortcutsStayOnTheToggleCommandAndTheSettingsRowsStayUnderMore()
    {
        string playback = ReadSource("Desktop/GalleryViewer.Playback.cs");
        Assert.Contains("(_slideshowToggle, \"gallery.slideshow-toggle\")", playback);
        Assert.Contains("InitializeSlideshowMenu();", playback);
        // Seconds and mode are under More in every view (the flyout lends the same rows); neither has a place on the bar.
        foreach (WorkView view in Enum.GetValues<WorkView>())
            foreach (string id in new[] { "gallery.toolbar.slideshow-seconds", "gallery.toolbar.playback-mode" })
                Assert.DoesNotContain(id, WorkViewPolicy.GalleryBarToolIds(view, slideshowActive: true));
        // The menu button has no policy row of its own: it is part of the slideshow control.
        Assert.DoesNotContain("SetId(_slideshowMenuButton", ReadSource("Desktop/GalleryViewer.WorkView.cs"));
    }

    [Fact]
    public void TheMenuButtonUsesTokensAndAnIconInsteadOfAGlyphCharacter()
    {
        string menu = ReadSource("Desktop/GalleryViewer.SlideshowMenu.cs");
        Assert.Contains("WorkspaceIcons.ChevronDown", menu);
        Assert.DoesNotContain("Brush.Parse", menu);
        Assert.DoesNotContain("Color.Parse", menu);
        Assert.DoesNotContain("Colors.", menu);
    }

    // ---- texts ----

    [Fact]
    public void TheMenuTextsExistInEveryPackAndInTheBuiltInLanguages()
    {
        string[] keys = ["viewer.playback.menu", "viewer.playback.menuHelp"];
        foreach (string key in keys)
        {
            Assert.Contains(GalleryPlaybackCatalog.Entries, entry => entry.Key == key);
            Assert.Equal("Slideshow settings", GalleryPlaybackCatalog.Get("viewer.playback.menu"));
        }
        foreach (string code in new[] { "es", "fr", "ru", "zh-Hans", "ja", "ko", "pt-BR", "cs", "it", "pl" })
        {
            using JsonDocument pack = JsonDocument.Parse(ReadSource("Desktop/Localization/Packs/" + code + ".json"));
            JsonElement strings = pack.RootElement.GetProperty("strings");
            foreach (string key in keys)
            {
                string text = strings.GetProperty(key).GetString()!;
                Assert.False(string.IsNullOrWhiteSpace(text), code + " " + key);
                Assert.NotEqual(GalleryPlaybackCatalog.Get(key), text);
            }
        }
        Texts.SetLanguage("de");
        Assert.Equal("Diashow-Einstellungen", GalleryPlaybackCatalog.Get("viewer.playback.menu"));
    }

    private static string ReadSource(string path)
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
        {
            string candidate = Path.Combine(directory.FullName, path);
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
        }
        throw new FileNotFoundException(path);
    }
}
