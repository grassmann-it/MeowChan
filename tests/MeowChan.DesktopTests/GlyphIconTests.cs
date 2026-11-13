using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Avalonia.Automation;
using Avalonia.Controls;
using MeowChan.Desktop.Controls;

namespace MeowChan.DesktopTests;

// P6: drawn icons instead of font glyphs, three stroke weights, one icon-only button class, geometry built lazily.
// Source scans and the icon table only; no Avalonia application (a static Geometry.Parse would fail here, which is the point).
public sealed class GlyphIconTests
{
    private static readonly XNamespace A = "https://github.com/avaloniaui";

    private static IEnumerable<string> ProductFiles(params string[] extensions) =>
        Directory.EnumerateFiles(Root("Desktop"), "*.*", SearchOption.AllDirectories)
            .Where(file => extensions.Contains(Path.GetExtension(file)) && !file.Contains(Path.DirectorySeparatorChar + "Localization" + Path.DirectorySeparatorChar)
                && !file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) && !file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar));

    private static bool IsComment(string line)
    {
        string trimmed = line.TrimStart();
        return trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.StartsWith("*", StringComparison.Ordinal) || trimmed.StartsWith("<!--", StringComparison.Ordinal);
    }

    [Fact]
    public void NoButtonOrMenuUsesAFontGlyphAsItsIcon()
    {
        // A control's whole content or text being one of these marks. Prose that mentions them ("A × B" sizes, "…" ellipses) is not matched.
        Regex glyph = new("(Content|Header|Text)\\s*=\\s*\"(⋯|⋮|×|✕|✖|✓|✔|★|☆|‹|›|⌃|⌄)\"|(Content|Header)\\s*=\\s*\"[★☆✕×⋯]\\s", RegexOptions.CultureInvariant);
        List<string> found = [];
        foreach (string file in ProductFiles(".cs", ".axaml"))
        {
            string[] lines = File.ReadAllLines(file);
            for (int index = 0; index < lines.Length; index++)
                if (!IsComment(lines[index]) && glyph.IsMatch(lines[index])) found.Add(Path.GetFileName(file) + ":" + (index + 1) + " " + lines[index].Trim());
        }
        Assert.True(found.Count == 0, "Font glyphs used as icons (use WorkspaceIcons):\n" + string.Join("\n", found));
    }

    [Fact]
    public void NoChipOrRowAppendsACrossToItsText()
    {
        foreach (string file in ProductFiles(".cs"))
            Assert.DoesNotMatch("\"[^\"]*\\s✕\"", string.Join("\n", File.ReadAllLines(file).Where(line => !IsComment(line))));
    }

    [Fact]
    public void StrokeWeightsAreOnlyOneOnePointFiveAndTwo()
    {
        double[] allowed = [.. WorkspaceIcons.StrokeWeights];
        Regex[] patterns =
        [
            new("StrokeThickness\\s*=\\s*\"?([0-9]+(?:\\.[0-9]+)?)", RegexOptions.CultureInvariant),
            new("Property=\"StrokeThickness\"\\s+Value=\"([0-9]+(?:\\.[0-9]+)?)\"", RegexOptions.CultureInvariant),
            new("LineThickness\\s*=\\s*\"?([0-9]+(?:\\.[0-9]+)?)", RegexOptions.CultureInvariant),
            new("nameof\\((?:Stroke|Line)Thickness\\),\\s*([0-9]+(?:\\.[0-9]+)?)", RegexOptions.CultureInvariant)
        ];
        List<string> found = [];
        foreach (string file in ProductFiles(".cs", ".axaml"))
            foreach (string line in File.ReadAllLines(file).Where(line => !IsComment(line)))
                foreach (Regex pattern in patterns)
                    foreach (Match match in pattern.Matches(line))
                    {
                        double weight = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                        if (weight != 0 && !allowed.Contains(weight)) found.Add(Path.GetFileName(file) + ": " + match.Value);
                    }
        Assert.True(found.Count == 0, "Stroke weights outside 1 / 1.5 / 2:\n" + string.Join("\n", found));
    }

    [Fact]
    public void NoStaticInitializerParsesGeometryEagerly()
    {
        // Geometry.Parse needs the render platform; a static field that parses on type load breaks every test that touches the type.
        Regex eager = new("static\\s+(?:readonly\\s+)?[\\w<>?.]+\\s+\\w+\\s*=\\s*(?:new\\(\\)\\s*\\{[^}]*Data\\s*=\\s*)?(?:Avalonia\\.Media\\.)?(?:Stream)?Geometry\\.Parse", RegexOptions.CultureInvariant);
        List<string> found = [];
        foreach (string file in ProductFiles(".cs"))
            foreach (string line in File.ReadAllLines(file).Where(line => !IsComment(line)))
                if (eager.IsMatch(line)) found.Add(Path.GetFileName(file) + ": " + line.Trim());
        Assert.True(found.Count == 0, "Eager Geometry.Parse in a static initializer:\n" + string.Join("\n", found));
    }

    [Fact]
    public void TheIconTableNeedsNoRenderPlatformAndEveryKeyHasPathData()
    {
        Assert.NotEmpty(WorkspaceIcons.Keys);
        foreach (string key in WorkspaceIcons.Keys)
        {
            string data = WorkspaceIcons.DataOf(key);
            Assert.StartsWith("M", data);
            Assert.DoesNotContain("NaN", data);
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => WorkspaceIcons.DataOf("no-such-icon"));
    }

    [Fact]
    public void AnIconIsAPathThatBuildsItsGeometryOnlyWhenShown()
    {
        Avalonia.Controls.Shapes.Path close = WorkspaceIcons.Create(WorkspaceIcons.Close);
        Assert.Equal(WorkspaceIcons.Close, WorkspaceIcons.KeyOf(close));
        Assert.Contains(WorkspaceIcons.StyleClass, close.Classes);
        Assert.Null(close.Data); // not built yet: it needs the render platform
        Assert.Equal(AccessibilityView.Raw, AutomationProperties.GetAccessibilityView(close));
        Avalonia.Controls.Shapes.Path small = WorkspaceIcons.Create(WorkspaceIcons.CloseSmall);
        Assert.Contains(WorkspaceIcons.SmallClass, small.Classes);
        Assert.Contains(WorkspaceIcons.HeavyClass, WorkspaceIcons.Create(WorkspaceIcons.More).Classes);
        Assert.Null(WorkspaceIcons.KeyOf("×"));
        Assert.Null(WorkspaceIcons.KeyOf(null));
    }

    [Fact]
    public void AnIconOnlySquareButtonKeepsItsTooltipAndAccessibleName()
    {
        Button button = new();
        WorkspaceIcons.MakeSquare(button, WorkspaceIcons.Close, "Dismiss", "Dismiss this message");
        Assert.Contains(WorkspaceIcons.SquareClass, button.Classes);
        Assert.Equal(WorkspaceIcons.Close, WorkspaceIcons.KeyOf(button.Content));
        Assert.Equal("Dismiss", AutomationProperties.GetName(button));
        Assert.Equal("Dismiss this message", ToolTip.GetTip(button));
        Button plain = new();
        WorkspaceIcons.MakeSquare(plain, WorkspaceIcons.More, "More", subtle: false);
        Assert.DoesNotContain("subtle", plain.Classes);
        Assert.Equal("More", ToolTip.GetTip(plain));
    }

    [Fact]
    public void ARemovableChipShowsItsNameAndADrawnCrossNotAGlyph()
    {
        StackPanel content = WorkspaceChips.RemovableContent("my_tag");
        Assert.Equal("my_tag", WorkspaceChips.RemovableName(content));
        Assert.Equal(WorkspaceIcons.CloseSmall, WorkspaceIcons.KeyOf(content.Children[1]));
        Assert.DoesNotContain("✕", content.Children.OfType<TextBlock>().Single().Text);
        Assert.Null(WorkspaceChips.RemovableName("plain"));
    }

    [Fact]
    public void TheStyleFileDefinesTheSquareButtonAndTheIconClassesAndNothingLeftOfThePluginName()
    {
        XElement styles = XDocument.Load(Root("Desktop/WorkspaceStyles.axaml")).Root!;
        string[] selectors = styles.Descendants(A + "Style").Select(style => (string?)style.Attribute("Selector") ?? string.Empty).ToArray();
        Assert.Contains("Button.icon-square", selectors);
        Assert.Contains("Path.glyph-icon", selectors);
        Assert.Contains("Path.glyph-icon.glyph-thin", selectors);
        Assert.Contains("Path.glyph-icon.glyph-heavy", selectors);
        Assert.DoesNotContain(selectors, selector => selector.Contains("plugin-overflow", StringComparison.Ordinal));
        Assert.DoesNotContain("plugin-overflow", File.ReadAllText(Root("Desktop/Views/Plugins/PluginsPage.axaml")));
        Assert.Contains("icon-square", File.ReadAllText(Root("Desktop/Views/Plugins/PluginsPage.axaml")));
        XElement square = styles.Descendants(A + "Style").Single(style => (string?)style.Attribute("Selector") == "Button.icon-square");
        Assert.Equal("{StaticResource Workspace.Size.Input}", square.Elements(A + "Setter").Single(setter => (string?)setter.Attribute("Property") == "Width").Attribute("Value")!.Value);
    }

    [Fact]
    public void EveryIconOnlyCloseOrMoreButtonOfTheShellUsesTheSquareClass()
    {
        string shell = File.ReadAllText(Root("Desktop/Shell/MainWindow.axaml"));
        Assert.Matches("x:Name=\"ShellFeedbackDismissButton\"[^>]*Classes=\"subtle icon-square\"", shell);
        Assert.Matches("x:Name=\"InspectorCloseButton\"[^>]*Classes=\"subtle icon-square\"", shell);
        Assert.Contains("WorkspaceIcons.SquareClass", File.ReadAllText(Root("Desktop/Shell/MainWindow.Notifications.cs")));
    }

    private static string Root(string relative, [CallerFilePath] string sourcePath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "../..", relative));
}
