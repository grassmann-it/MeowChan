using System.Runtime.CompilerServices;
using System.Xml.Linq;

namespace MeowChan.DesktopTests;

// D3/D6: card, overlay and thumbnail-placeholder surfaces. Parsed sources only; no Avalonia application.
public sealed class SurfaceStyleTests
{
    private static readonly XNamespace A = "https://github.com/avaloniaui";

    [Fact]
    public void WorkspaceCardIsTheCardTokenOnItsEdgeWithoutAShadow()
    {
        Assert.Equal("{DynamicResource Workspace.Card}", Setting("Border.workspace-card", "Background"));
        Assert.Equal("{DynamicResource Workspace.CardEdge}", Setting("Border.workspace-card", "BorderBrush"));
        Assert.Equal("1", Setting("Border.workspace-card", "BorderThickness"));
        Assert.Equal("{StaticResource Workspace.Radius.Control}", Setting("Border.workspace-card", "CornerRadius"));
        Assert.Null(Style("Border.workspace-card").Elements(A + "Setter").FirstOrDefault(setter => (string?)setter.Attribute("Property") == "BoxShadow"));
    }

    [Fact]
    public void TheActiveCardIsATintPlusTheAccentEdge()
    {
        Assert.Equal("{DynamicResource Workspace.AccentSurface}", Setting("Border.workspace-card.workspace-card-active", "Background"));
        Assert.Equal("{DynamicResource Workspace.Accent}", Setting("Border.workspace-card.workspace-card-active", "BorderBrush"));
        Assert.Equal("3,1,1,1", Setting("Border.workspace-card.workspace-card-active", "BorderThickness"));
    }

    [Fact]
    public void TheOverlayLevelIsOverlayOnControlBorderWithRadiusEight()
    {
        Assert.Equal("{DynamicResource Workspace.Overlay}", Setting("Border.workspace-overlay", "Background"));
        Assert.Equal("{DynamicResource Workspace.ControlBorder}", Setting("Border.workspace-overlay", "BorderBrush"));
        Assert.Equal("{StaticResource Workspace.Radius.Group}", Setting("Border.workspace-overlay", "CornerRadius"));
    }

    [Theory]
    [InlineData("Desktop/Views/Archive/ArchivePage.axaml", "ArchiveFilterSurface")]
    public void FilterSurfacesAreOverlaysNotRaisedPanels(string file, string name)
    {
        XElement surface = XDocument.Load(Root(file)).Descendants().Single(element => (string?)element.Attribute("{http://schemas.microsoft.com/winfx/2006/xaml}Name") == name);
        Assert.Equal("workspace-overlay", (string?)surface.Attribute("Classes"));
    }

    [Fact]
    public void TheMediaFilterSectionIsInlineAndTheArchiveSurfaceStillFollowsTheOverlayReveal()
    {
        string media = File.ReadAllText(Root("Desktop/Views/Media/MediaPage.FilterSurface.cs"));
        // S1: a section in the header that pushes the grid, not an overlay: no overlay class, no stacking level, no Close button.
        Assert.Contains("Classes = { \"workspace-section\" }", media);
        Assert.DoesNotContain("workspace-overlay", media);
        Assert.DoesNotContain("ZIndex", media);
        Assert.DoesNotContain("_filterClose", media);
        Assert.Contains("new FilterSurfaceController(this, _filters, surface", media);
        Assert.Contains("overlay: false", media);
        Assert.Contains("_headerBody.Children.Add(filterSurface);", File.ReadAllText(Root("Desktop/Views/Media/MediaPage.cs")));
        Assert.Contains("new FilterSurfaceController(this, ArchiveFilters, ArchiveFilterSurface", File.ReadAllText(Root("Desktop/Views/Archive/ArchivePage.FilterSurface.cs")));
        Assert.Contains("if (overlay) OverlayReveal.Follow(surface)", File.ReadAllText(Root("Desktop/Controls/FilterSurfaceController.cs")));
    }

    [Fact]
    public void BooruHitsAndWatchersAreCardsAndTheirThumbnailIsFlat()
    {
        string booru = File.ReadAllText(Root("Desktop/Views/Booru/BooruWorkbenchWindow.cs"));
        Assert.Contains("BooruResult:\" + post.PostId };\n        card.Classes.Add(\"workspace-card\")", booru.Replace("\r\n", "\n"));
        Assert.Contains("BooruWatcher:\" + watcher.WatcherId };\n        card.Classes.Add(\"workspace-card\")", booru.Replace("\r\n", "\n"));
        Assert.Contains("frame.Classes.Add(\"workspace-thumb-placeholder\")", booru);
        Assert.DoesNotContain("frame.Classes.Add(\"raised\")", booru); // no box around a thumbnail inside a card
    }

    [Fact]
    public void ReaderTokensAreRetiredInFavourOfTheCardTokens()
    {
        string xaml = File.ReadAllText(Root("Desktop/WorkspaceStyles.axaml")) + File.ReadAllText(Root("Desktop/Views/Archive/ArchivePage.axaml"));
        foreach (string retired in new[] { "Workspace.ReaderPage", "Workspace.ReaderPostSurface", "Workspace.ReaderPostEdge", "Workspace.ReaderActiveSurface" })
            Assert.DoesNotContain(retired, xaml);
    }

    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly string[] SemiShadowKeys =
    {
        "MenuFlyoutBorderBoxShadow", "FlyoutBorderBoxShadow", "ComboBoxPopupBoxShadow", "AutoCompleteBoxPopupBoxShadow", "NotificationCardBoxShadows"
    };

    // D6: the one shadow. Semi's menu, flyout, drop-down, suggestion and toast shadows carry the value of Workspace.Shadow.Overlay.
    [Theory, InlineData("Dark"), InlineData("Light")]
    public void SemiOverlayShadowsCarryTheOverlayShadowOfTheTheme(string theme)
    {
        XElement dictionary = XDocument.Load(Root("Desktop/WorkspaceStyles.axaml")).Descendants(A + "ResourceDictionary").Single(element => (string?)element.Attribute(X + "Key") == theme);
        string value = Assert.Single(dictionary.Elements(A + "BoxShadows").Where(element => (string?)element.Attribute(X + "Key") == "Workspace.Shadow.Overlay")).Value.Trim();
        foreach (string key in SemiShadowKeys)
            Assert.Equal(value, Assert.Single(dictionary.Elements(A + "BoxShadows").Where(element => (string?)element.Attribute(X + "Key") == key)).Value.Trim());
    }

    [Fact]
    public void EveryOverlaySemiDrawsHasTheGroupRadiusAndTheInWindowOverlayNoShadow()
    {
        XElement root = XDocument.Load(Root("Desktop/WorkspaceStyles.axaml")).Root!;
        string group = Assert.Single(root.Descendants(A + "CornerRadius").Where(element => (string?)element.Attribute(X + "Key") == "Workspace.Radius.Group")).Value.Trim();
        foreach (string key in new[] { "MenuFlyoutCornerRadius", "FlyoutCornerRadius", "ToolTipCornerRadius", "NotificationCardCornerRadius", "ComboBoxPopupBoxCornerRadius", "AutoCompleteBoxPopupCornerRadius" })
            Assert.Equal(group, Assert.Single(root.Descendants(A + "CornerRadius").Where(element => (string?)element.Attribute(X + "Key") == key)).Value.Trim());
        // Measured (smoke "motion shadow cost"): a blurred shadow on an in-window overlay costs frames while the grid behind it scrolls.
        Assert.Null(Style("Border.workspace-overlay").Elements(A + "Setter").FirstOrDefault(setter => (string?)setter.Attribute("Property") == "BoxShadow"));
    }

    private static XElement Style(string selector) =>
        Assert.Single(XDocument.Load(Root("Desktop/WorkspaceStyles.axaml")).Root!.Elements(A + "Style").Where(style => (string?)style.Attribute("Selector") == selector));

    private static string? Setting(string selector, string property) =>
        (string?)Assert.Single(Style(selector).Elements(A + "Setter").Where(setter => (string?)setter.Attribute("Property") == property)).Attribute("Value");

    private static string Root(string relative, [CallerFilePath] string sourcePath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "../..", relative));
}
