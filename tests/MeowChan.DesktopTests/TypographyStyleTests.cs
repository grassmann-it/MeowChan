using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace MeowChan.DesktopTests;

// D5: the Subtitle step for dialog headings and card titles, and tabular figures for counters and tables. Parsed sources only.
public sealed class TypographyStyleTests
{
    private static readonly XNamespace A = "https://github.com/avaloniaui";

    [Fact]
    public void DialogHeadingsAndCardTitlesUseTheSubtitleStep()
    {
        XElement style = Style("Border#DialogFrameHeader TextBlock.page-title, Border#DialogFrameHeader TextBlock.section-title, TextBlock.card-title, SelectableTextBlock.card-title");
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
        Assert.Contains("x:Name=\"WorkspaceCounts\" Classes=\"muted tnum\"", main);
        // The header rate labels are Codex's directional graph labels: they carry tabular figures directly (FontFeatures), not the mono class.
        foreach (string label in new[] { "TransferSpeedLabel", "TransferUploadLabel", "TransferDiskReadLabel", "TransferDiskLabel" })
            Assert.Matches("x:Name=\"" + label + "\"[^>]*FontFeatures=\"tnum\"", main);
        string archive = File.ReadAllText(Root("Desktop/Views/Archive/ArchivePage.axaml"));
        foreach (string name in new[] { "ArchiveThreadPage", "ArchiveSelectionSummary", "ArchiveReferencesPage" })
            Assert.Matches("x:Name=\"" + name + "\"[^>]*Classes=\"[^\"]*tnum", archive);
    }

    [Fact]
    public void BooruWatcherTitlesUseTheCardTitleClass()
    {
        string booru = File.ReadAllText(Root("Desktop/Views/Booru/BooruWorkbenchView.cs"));
        Assert.Contains("Classes = { \"mono\", \"card-title\" }", booru);
        Assert.DoesNotContain("FontSize = 14", booru);
    }

    [Fact]
    public void FigureStylesLiveInTheStyleFileOnly()
    {
        XElement value = Style("TextBlock.stats-value");
        Assert.Equal("{StaticResource Workspace.Text.Subtitle}", Setting(value, "FontSize"));
        Assert.Equal("SemiBold", Setting(value, "FontWeight"));
        Assert.Equal("tnum", Setting(value, "FontFeatures"));
        Assert.Equal("tnum", Setting(Style("TextBlock.stats-num"), "FontFeatures"));
        Assert.DoesNotContain("Selector=\"TextBlock.stats-", File.ReadAllText(Root("Desktop/Views/Statistics/StatisticsPage.axaml")));
    }

    // P5: sizes come from the type scale (tokens or the heading classes); the only literals left are the About brand
    // mark (documented brand size) and the Reader's local post-number style (package P8). The tagging queue
    // deliberately bold the next-row number as a state cue in both queue presentations; no other desktop surface may use literal bold.
    [Fact]
    public void FontSizesComeFromTheScaleAndNothingIsBold()
    {
        Regex literal = new("FontSize\\s*=\\s*[0-9]|FontSize=\"[0-9]|Property=\"FontSize\" Value=\"[0-9]", RegexOptions.CultureInvariant);
        string desktop = Root("Desktop");
        var found = new List<string>();
        foreach (string file in Directory.EnumerateFiles(desktop, "*.*", SearchOption.AllDirectories)
                     .Where(path => path.EndsWith(".axaml", StringComparison.Ordinal) || path.EndsWith(".cs", StringComparison.Ordinal))
                     .Where(path => !path.EndsWith("WorkspaceStyles.axaml", StringComparison.Ordinal)))
        {
            string relative = Path.GetRelativePath(desktop, file).Replace('\\', '/');
            string text = File.ReadAllText(file);
            foreach (Match match in literal.Matches(text)) found.Add(relative + ": " + match.Value);
            if (relative is not ("Views/Tagging/TaggingQueueRowView.cs" or "Views/Tagging/TaggingQueueTileView.cs"))
            {
                Assert.DoesNotContain("FontWeight.Bold", text);
                Assert.DoesNotContain("FontWeight=\"Bold\"", text);
            }
        }
        string[] allowed = { "Views/About/AboutWindow.axaml: FontSize=\"2", "Views/Archive/ArchivePage.axaml: Property=\"FontSize\" Value=\"1" };
        Assert.All(found, entry => Assert.Contains(allowed, prefix => entry.StartsWith(prefix, StringComparison.Ordinal)));
    }

    private static XElement Style(string selector) =>
        Assert.Single(XDocument.Load(Root("Desktop/WorkspaceStyles.axaml")).Root!.Elements(A + "Style").Where(style => (string?)style.Attribute("Selector") == selector));

    private static string? Setting(XElement style, string property) =>
        (string?)Assert.Single(style.Elements(A + "Setter").Where(setter => (string?)setter.Attribute("Property") == property)).Attribute("Value");

    private static string Root(string relative, [CallerFilePath] string sourcePath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "../..", relative));
}
