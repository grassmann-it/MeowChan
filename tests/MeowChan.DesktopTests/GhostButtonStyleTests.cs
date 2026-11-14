using System.Globalization;
using System.Runtime.CompilerServices;
using System.Xml.Linq;

namespace MeowChan.DesktopTests;

// UX round 3: the quiet "ghost" action button for rows of actions inside cards and panels. Parsed sources only;
// contrast is computed from the token values (WCAG 2.x relative luminance).
public sealed class GhostButtonStyleTests
{
    private static readonly XNamespace A = "https://github.com/avaloniaui";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void AGhostIsOnlyItsLabelAtRestAndShowsTheInteractiveOutlineWhenPointedAtAndTheAccentWhenPressed()
    {
        Assert.Equal("Transparent", Setting("Button.ghost", "Background"));
        Assert.Equal("Transparent", Setting("Button.ghost", "BorderBrush"));
        Assert.Equal("{DynamicResource Workspace.ControlBorder}", Setting("Button.ghost:pointerover /template/ ContentPresenter#PART_ContentPresenter", "BorderBrush"));
        Assert.Equal("{DynamicResource Workspace.Raised}", Setting("Button.ghost:pointerover /template/ ContentPresenter#PART_ContentPresenter", "Background"));
        Assert.Equal("{DynamicResource Workspace.Accent}", Setting("Button.ghost:pressed /template/ ContentPresenter#PART_ContentPresenter", "BorderBrush"));
        // Disabled is the text colour only; Semi's disabled outline is switched off.
        Assert.Equal("Transparent", Setting("Button.ghost:disabled /template/ ContentPresenter#PART_ContentPresenter", "BorderBrush"));
        Assert.Equal("Transparent", Setting("Button.ghost:disabled /template/ ContentPresenter#PART_ContentPresenter", "Background"));
    }

    [Fact]
    public void TheGhostClassComesAfterSubtleSoACombinedButtonIsQuiet()
    {
        string[] selectors = StyleElements().Select(style => (string?)style.Attribute("Selector") ?? "").ToArray();
        Assert.True(Array.IndexOf(selectors, "Button.ghost") > Array.IndexOf(selectors, "Button.subtle"));
    }

    [Theory, InlineData("Dark"), InlineData("Light")]
    public void TheLabelReadsOnEverySurfaceAGhostSitsOnAndTheHoverOutlineReachesThreeToOne(string theme)
    {
        foreach (string surface in new[] { "Workspace.Background", "Workspace.Panel", "Workspace.Card", "Workspace.Raised", "Workspace.Overlay", "Workspace.Hover" })
            Assert.True(Contrast(Color(theme, "Workspace.Text"), Color(theme, surface)) >= 4.5, theme + ": label on " + surface);
        Assert.True(Contrast(Color(theme, "Workspace.ControlBorder"), Color(theme, "Workspace.Raised")) >= 3.0, theme + ": hover outline on the hover fill");
        Assert.True(Contrast(Color(theme, "Workspace.Accent"), Color(theme, "Workspace.AccentSurface")) >= 3.0, theme + ": pressed outline on the pressed fill");
        Assert.True(Contrast(Color(theme, "ButtonDefaultDisabledForeground"), Color(theme, "Workspace.Card")) >= 4.5, theme + ": disabled label on a card");
    }

    [Fact]
    public void BooruCardsKeepTheirOneFindableActionOutlinedAndRemoveRed()
    {
        string booru = File.ReadAllText(Root("Desktop/Views/Booru/BooruWorkbenchWindow.cs"));
        Assert.Contains("open.Classes.Add(\"ghost\")", booru);
        Assert.Contains("copy.Classes.Add(\"ghost\")", booru);
        Assert.Equal(3, booru.Split("ghost: true").Length - 1); // Resume, Pause, Restart
        Assert.Contains("remove.Classes.Add(\"danger\")", booru);
        // Check now is not a ghost: its call does not pass the flag.
        int check = booru.IndexOf("CheckPrefix + watcher.WatcherId", StringComparison.Ordinal);
        string call = booru[check..booru.IndexOf(");", check, StringComparison.Ordinal)];
        Assert.DoesNotContain("ghost", call);
    }

    [Fact]
    public void TheDownloadsInspectorKeepsCheckNowOutlinedAndQuietensTheNavigationActions()
    {
        XElement[] buttons = XDocument.Load(Root("Desktop/Shell/MainWindow.axaml")).Descendants(A + "Button").ToArray();
        string[] Classes(string name) => ((string?)buttons.Single(button => (string?)button.Attribute(X + "Name") == name).Attribute("Classes") ?? "").Split(' ');
        foreach (string name in new[] { "InspectorReadPosts", "InspectorGallery", "InspectorHtmlExport", "InspectorOpenFolderButton", "InspectorOpenBrowserButton" })
            Assert.Contains("ghost", Classes(name));
        Assert.DoesNotContain("ghost", Classes("InspectorCheckButton"));
        // The post-capture switch left the reader bar for the Thread menu (R2): no button of that name remains.
        Assert.DoesNotContain(XDocument.Load(Root("Desktop/Views/Archive/ArchivePage.axaml")).Descendants(A + "Button"),
            button => (string?)button.Attribute(X + "Name") == "CapturePostsButton");
    }

    [Fact]
    public void TheRepeatedBoardAssistantResetActionIsAGhost()
    {
        string source = File.ReadAllText(Root("Desktop/Views/Admission/BoardAdmissionWindow.cs"));
        Assert.Contains("reset.Classes.Add(\"ghost\")", source);
    }

    private static IEnumerable<XElement> StyleElements() => XDocument.Load(Root("Desktop/WorkspaceStyles.axaml")).Root!.Elements(A + "Style");

    private static string? Setting(string selector, string property) =>
        (string?)Assert.Single(Assert.Single(StyleElements(), style => (string?)style.Attribute("Selector") == selector).Elements(A + "Setter")
            .Where(setter => (string?)setter.Attribute("Property") == property)).Attribute("Value");

    private static string Color(string theme, string key) =>
        Assert.Single(Assert.Single(XDocument.Load(Root("Desktop/WorkspaceStyles.axaml")).Root!.Descendants(A + "ResourceDictionary"),
            element => (string?)element.Attribute(X + "Key") == theme).Elements(), element => (string?)element.Attribute(X + "Key") == key).Value.Trim();

    private static double Contrast(string first, string second)
    {
        double a = Luminance(first), b = Luminance(second);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    private static double Luminance(string hex)
    {
        double[] c = Enumerable.Range(0, 3).Select(index =>
        {
            double value = int.Parse(hex.AsSpan(1 + index * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255d;
            return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }).ToArray();
        return 0.2126 * c[0] + 0.7152 * c[1] + 0.0722 * c[2];
    }

    private static string Root(string path, [CallerFilePath] string sourcePath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "../..", path));
}
