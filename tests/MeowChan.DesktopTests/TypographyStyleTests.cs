using System.Runtime.CompilerServices;
using System.Xml.Linq;

namespace MeowChan.DesktopTests;

// D5: the Subtitle step for dialog headings and card titles, and tabular figures for counters and tables. Parsed sources only.
public sealed class TypographyStyleTests
{
    private static readonly XNamespace A = "https://github.com/avaloniaui";

    [Fact]
    public void DialogHeadingsAndCardTitlesUseTheSubtitleStep()
    {
        XElement style = Style("Border#DialogFrameHeader TextBlock.page-title, Border#DialogFrameHeader TextBlock.section-title, TextBlock.card-title");
        Assert.Equal("{StaticResource Workspace.Text.Subtitle}", Setting(style, "FontSize"));
        Assert.Equal("SemiBold", Setting(style, "FontWeight"));
        Assert.Contains("Name = HeaderName", File.ReadAllText(Root("Desktop/Controls/DialogFrame.cs")));
        Assert.Contains("HeaderName = \"DialogFrameHeader\"", File.ReadAllText(Root("Desktop/Controls/DialogFrame.cs")));
    }

    [Fact]
    public void TabularFiguresReachTablesAndTheNamedCounters()
    {
        Assert.Equal("tnum", Setting(Style("TextBlock.tnum, SelectableTextBlock.tnum, DataGrid TextBlock"), "FontFeatures"));
        string main = File.ReadAllText(Root("Desktop/Shell/MainWindow.axaml"));
        Assert.Contains("x:Name=\"WorkspaceCounts\" Grid.Column=\"2\" Classes=\"muted tnum\"", main);
        Assert.Contains("Classes=\"mono muted tnum\"", main);
        string archive = File.ReadAllText(Root("Desktop/Views/Archive/ArchivePage.axaml"));
        foreach (string name in new[] { "ArchiveThreadPage", "ArchivePostPage", "ArchiveSelectionSummary", "ArchiveReferencesPage" })
            Assert.Matches("x:Name=\"" + name + "\"[^>]*Classes=\"[^\"]*tnum", archive);
    }

    [Fact]
    public void BooruCardTitlesCarryTheCardTitleClassInsteadOfALiteralSize()
    {
        string booru = File.ReadAllText(Root("Desktop/Views/Booru/BooruWorkbenchWindow.cs"));
        Assert.Contains("Classes = { \"card-title\" } });", booru);
        Assert.Contains("Classes = { \"mono\", \"card-title\" }", booru);
        Assert.DoesNotContain("FontSize = 14", booru);
    }

    private static XElement Style(string selector) =>
        Assert.Single(XDocument.Load(Root("Desktop/WorkspaceStyles.axaml")).Root!.Elements(A + "Style").Where(style => (string?)style.Attribute("Selector") == selector));

    private static string? Setting(XElement style, string property) =>
        (string?)Assert.Single(style.Elements(A + "Setter").Where(setter => (string?)setter.Attribute("Property") == property)).Attribute("Value");

    private static string Root(string relative, [CallerFilePath] string sourcePath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "../..", relative));
}
