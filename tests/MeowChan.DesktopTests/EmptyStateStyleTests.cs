using System.Runtime.CompilerServices;
using System.Xml.Linq;
using MeowChan.Desktop.Controls;

namespace MeowChan.DesktopTests;

// DS-P3: the one centred empty state (WorkspaceEmptyState). Parsed sources only; no Avalonia application.
public sealed class EmptyStateStyleTests
{
    private static readonly XNamespace A = "https://github.com/avaloniaui";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    // The three centred empty states (Downloads threads and boards, Archive, Media) are one control with one sentence class.
    [Theory]
    [InlineData("Desktop/Shell/MainWindow.axaml", "ThreadsEmptyPanel", "ThreadsEmptyState")]
    [InlineData("Desktop/Shell/MainWindow.axaml", "BoardsEmptyPanel", "BoardsEmptyState")]
    [InlineData("Desktop/Views/Archive/ArchivePage.axaml", "ArchiveEmptyState", "ArchiveEmpty")]
    public void TheCentredEmptyStatesAreTheSharedControlWithTheSharedSentenceClass(string file, string panel, string sentence)
    {
        XDocument document = XDocument.Load(Root(file));
        XElement state = document.Descendants().Single(element => (string?)element.Attribute(X + "Name") == panel);
        Assert.Equal("WorkspaceEmptyState", state.Name.LocalName);
        // Geometry comes from the style: no local alignment, width, margin or spacing on the page.
        foreach (string local in new[] { "MaxWidth", "Margin", "Spacing", "HorizontalAlignment", "VerticalAlignment" })
            Assert.Null(state.Attribute(local));
        XElement text = state.Descendants().Single(element => (string?)element.Attribute(X + "Name") == sentence);
        Assert.Contains("workspace-empty-text", ((string?)text.Attribute("Classes"))!.Split(' '));
        Assert.DoesNotContain("muted", ((string?)text.Attribute("Classes"))!.Split(' '));
    }

    [Fact]
    public void TheMediaEmptyStateIsTheSharedControlToo()
    {
        string source = File.ReadAllText(Root("Desktop/Views/Media/MediaPage.cs"));
        Assert.Contains("private readonly WorkspaceEmptyState _emptyState = new() { Name = \"MediaEmptyState\"", source);
        Assert.Contains("_empty.Classes.Add(WorkspaceEmptyState.TextClass);", source);
        Assert.Equal("workspace-empty-text", WorkspaceEmptyState.TextClass);
    }

    [Fact]
    public void TheEmptyStateStyleCarriesAlignmentWidthMarginAndSpacingAsTokens()
    {
        string selector = "controls|WorkspaceEmptyState";
        Assert.Equal("Center", Setting(selector, "HorizontalAlignment"));
        Assert.Equal("Center", Setting(selector, "VerticalAlignment"));
        Assert.Equal("{StaticResource Workspace.Size.EmptyStateWidth}", Setting(selector, "MaxWidth"));
        Assert.Equal("{StaticResource Workspace.Inset.EmptyState}", Setting(selector, "Margin"));
        Assert.Equal("{StaticResource Workspace.Space.12}", Setting(selector, "Spacing"));
        Assert.Equal("{DynamicResource Workspace.Muted}", Setting("TextBlock.workspace-empty-text, SelectableTextBlock.workspace-empty-text", "Foreground"));
    }

    private static IEnumerable<XElement> Styles() => XDocument.Load(Root("Desktop/WorkspaceStyles.axaml")).Root!.Elements(A + "Style");

    private static XElement Style(string selector) =>
        Assert.Single(Styles().Where(style => (string?)style.Attribute("Selector") == selector));

    private static string? Setting(string selector, string property) =>
        (string?)Assert.Single(Style(selector).Elements(A + "Setter").Where(setter => (string?)setter.Attribute("Property") == property)).Attribute("Value");

    private static string Root(string relative, [CallerFilePath] string sourcePath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "../..", relative));
}
