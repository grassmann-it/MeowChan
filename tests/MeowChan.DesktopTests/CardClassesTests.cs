using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace MeowChan.DesktopTests;

// DS-P4: cards on three classes (workspace-section, workspace-card, workspace-overlay) plus the flush variant; panel and raised are
// deprecated. Parsed sources only; no Avalonia application.
public sealed class CardClassesTests
{
    private static readonly XNamespace A = "https://github.com/avaloniaui";

    // The retired "panel" class has no users left; a section is workspace-section-flush (content to the edge) or
    // workspace-section (own inset), an in-window overlay is workspace-overlay.
    private static readonly string[] PanelAllowList = Array.Empty<string>();

    [Fact]
    public void TheFlushSectionIsTheSectionSurfaceWithInsetZeroAndTheControlRadius()
    {
        XElement root = XDocument.Load(Root("Desktop/WorkspaceStyles.axaml")).Root!;
        XElement shared = Assert.Single(root.Elements(A + "Style"), style => ((string?)style.Attribute("Selector"))!.StartsWith("Border.workspace-section,"));
        Assert.Contains("Border.workspace-section-flush", (string)shared.Attribute("Selector")!);
        Assert.Equal("{StaticResource Workspace.Radius.Control}", Value(shared, "CornerRadius"));
        Assert.Equal("{DynamicResource Workspace.Panel}", Value(shared, "Background"));
        XElement flush = Assert.Single(root.Elements(A + "Style"), style => (string?)style.Attribute("Selector") == "Border.workspace-section-flush");
        Assert.Equal("0", Value(flush, "Padding"));
        // The inset override must come after the shared rule, or the section inset would win.
        Assert.True(shared.NodesBeforeSelf().Count() < flush.NodesBeforeSelf().Count());
    }

    [Fact]
    public void RaisedIsGoneFromTheStylesAndTheSources()
    {
        Assert.DoesNotContain(XDocument.Load(Root("Desktop/WorkspaceStyles.axaml")).Root!.Elements(A + "Style"),
            style => Regex.IsMatch((string?)style.Attribute("Selector") ?? "", @"Border\.raised\b"));
        foreach (string file in Sources())
        {
            string text = File.ReadAllText(file);
            Assert.False(Regex.IsMatch(text, "Classes=\"[^\"]*\\braised\\b") || text.Contains("Classes.Add(\"raised\")") || text.Contains("Classes = { \"raised\" }"),
                Path.GetRelativePath(Root(""), file) + " still uses the retired raised class");
        }
    }

    [Fact]
    public void PanelIsOnlyUsedWhereTheAllowListSaysAndNowhereElse()
    {
        string[] users = Sources().Where(file => Regex.IsMatch(File.ReadAllText(file),
                "Classes=\"([^\"]*\\s)?panel(\\s[^\"]*)?\"|Classes\\.Add\\(\"panel\"\\)|Classes = \\{[^}]*\"panel\""))
            .Select(file => Path.GetRelativePath(Root(""), file).Replace('\\', '/')).Order().ToArray();
        Assert.Equal(PanelAllowList.Order().ToArray(), users);
    }

    [Fact]
    public void ADimLayerNeverCarriesAColourLiteralBecauseItUsesTheScrimToken()
    {
        foreach (string file in Sources().Where(file => file.EndsWith(".axaml") && !file.EndsWith("WorkspaceStyles.axaml")))
            Assert.False(Regex.IsMatch(File.ReadAllText(file), "Background=\"#[0-9A-Fa-f]{6,8}\""), Path.GetRelativePath(Root(""), file));
        Assert.Contains("Background=\"{DynamicResource Workspace.Scrim}\"", File.ReadAllText(Root("Desktop/Views/Archive/ArchivePage.axaml")));
    }

    [Fact]
    public void TheSectionRadiusIsTheControlRadiusAndTheTokenCommentSaysSo()
    {
        string styles = File.ReadAllText(Root("Desktop/WorkspaceStyles.axaml"));
        Assert.Contains("Control 5 (buttons, inputs, sections, cards, tiles), Group 8 (overlays, dialog body)", styles);
        Assert.DoesNotContain("Group 8 (sections", styles);
    }

    private static string? Value(XElement style, string property) =>
        (string?)Assert.Single(style.Elements(A + "Setter"), setter => (string?)setter.Attribute("Property") == property).Attribute("Value");

    private static IEnumerable<string> Sources() =>
        Directory.EnumerateFiles(Root("Desktop"), "*.*", SearchOption.AllDirectories).Where(path => path.EndsWith(".axaml") || path.EndsWith(".cs"));

    private static string Root(string relative, [CallerFilePath] string sourcePath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "..", "..", relative));
}
