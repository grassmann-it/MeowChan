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
    public void TheMediaFilterSurfaceAndTheArchiveOneFollowTheOverlayReveal()
    {
        string media = File.ReadAllText(Root("Desktop/Views/Media/MediaPage.FilterSurface.cs"));
        Assert.Contains("Classes = { \"workspace-overlay\" }", media);
        Assert.Contains("OverlayReveal.Follow(surface)", media);
        Assert.Contains("OverlayReveal.Follow(ArchiveFilterSurface)", File.ReadAllText(Root("Desktop/Views/Archive/ArchivePage.axaml.cs")));
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

    private static XElement Style(string selector) =>
        Assert.Single(XDocument.Load(Root("Desktop/WorkspaceStyles.axaml")).Root!.Elements(A + "Style").Where(style => (string?)style.Attribute("Selector") == selector));

    private static string? Setting(string selector, string property) =>
        (string?)Assert.Single(Style(selector).Elements(A + "Setter").Where(setter => (string?)setter.Attribute("Property") == property)).Attribute("Value");

    private static string Root(string relative, [CallerFilePath] string sourcePath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "../..", relative));
}
