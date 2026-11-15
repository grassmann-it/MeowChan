using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

/// <summary>
/// HB1: the structured help popover and its round button, the tooltip style (360 dip, wraps) and the two-line command tooltip.
/// Controls are built without an application (the drawn "?" is a path whose geometry is built when the button is shown, so only its key and path data are tested);
/// the style rules are read from WorkspaceStyles.axaml.
/// </summary>
[Collection("Localization")]
public sealed class HelpPopoverTests : IDisposable
{
    private static readonly XNamespace A = "https://github.com/avaloniaui";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    public HelpPopoverTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage("en"); }

    [Fact]
    public void ContentIsReadFromTheKeysAndStopsAtTheFirstMissingOne()
    {
        HelpContent stats = HelpContent.FromKeys("help.stats");
        Assert.Equal("What is counted", stats.Title);
        Assert.Equal(4, stats.Paragraphs.Count);
        Assert.Empty(stats.Bullets);
        Assert.StartsWith("Network counts", stats.Paragraphs[0], StringComparison.Ordinal);
        Assert.Equal(stats.Title + "\n" + string.Join("\n", stats.Paragraphs), stats.PlainText);
        // No key at all: Texts.T answers with the key; the title is the key and there is nothing else.
        HelpContent none = HelpContent.FromKeys("help.nothing-here");
        Assert.Empty(none.Paragraphs);
        Assert.Empty(none.Bullets);
    }

    [Fact]
    public void TheParagraphsOfAllTwelveLanguagesAreTheFourSentencesOfTheFootnote()
    {
        foreach (string language in Texts.Languages.Select(info => info.Code))
        {
            Texts.SetLanguage(language);
            HelpContent content = HelpContent.FromKeys("help.stats");
            Assert.True(content.Paragraphs.Count == 4, language + " has " + content.Paragraphs.Count + " paragraphs");
            Assert.NotEqual("help.stats.title", content.Title);
            Assert.DoesNotContain("{0}", Texts.T("help.popover.more"), StringComparison.Ordinal);
            Assert.Contains("{0}", Texts.T("help.button.name", "{0}"), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheColumnHasTitleParagraphsBulletsAndTheMoreLinkInOrderAndIsNarrow()
    {
        int more = 0;
        HelpContent content = new("Title", ["First", "Second"], ["One", "Two", "Three"], () => more++);
        int closed = 0;
        StackPanel body = HelpPopover.Build(content, () => closed++);
        Assert.True(body.Focusable);
        Assert.Equal(400, HelpPopover.MaxContentWidth);
        Assert.Equal(HelpPopover.MaxContentWidth, body.MaxWidth);
        Assert.Equal("Title", AutomationProperties.GetName(body));
        Assert.Equal(5, body.Children.Count);
        TextBlock title = Assert.IsType<TextBlock>(body.Children[0]);
        Assert.Equal("Title", title.Text);
        Assert.Equal(2, AutomationProperties.GetHeadingLevel(title));
        Assert.All(body.Children.Skip(1).Take(2).Cast<TextBlock>(), paragraph => Assert.Equal(Avalonia.Media.TextWrapping.Wrap, paragraph.TextWrapping));
        Assert.Equal(["First", "Second"], body.Children.Skip(1).Take(2).Cast<TextBlock>().Select(text => text.Text));
        StackPanel list = Assert.IsType<StackPanel>(body.Children[3]);
        Assert.Equal(["One", "Two", "Three"], list.Children.Cast<Grid>().Select(row => ((TextBlock)row.Children[1]).Text));
        Assert.All(list.Children.Cast<Grid>(), row => Assert.Equal(AccessibilityView.Raw, AutomationProperties.GetAccessibilityView(row.Children[0])));
        Button link = Assert.IsType<Button>(body.Children[4]);
        Assert.Equal("More in Help", link.Content);
        link.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal((1, 1), (closed, more));
    }

    [Fact]
    public void WithoutBulletsOrLinkTheColumnIsTitleAndParagraphsOnly()
    {
        StackPanel body = HelpPopover.Build(new HelpContent("T", ["P"], []), () => { });
        Assert.Equal(2, body.Children.Count);
    }

    [Fact]
    public void TheButtonIsNamedAfterTheSectionAndCarriesThePopoverTextAsHelpText()
    {
        HelpButton button = new() { HelpKey = "help.stats" };
        Assert.Contains("help-round", button.Classes);
        Assert.Contains("subtle", button.Classes);
        Assert.Equal("Help: What is counted", AutomationProperties.GetName(button));
        Assert.Equal("Help: What is counted", ToolTip.GetTip(button));
        Assert.Equal(HelpContent.FromKeys("help.stats").PlainText, AutomationProperties.GetHelpText(button));
        Assert.Same(button.Popover, button.Flyout);
        // The page may keep its own one-paragraph note for assistive technology.
        button.HelpTextOverride = "Own note";
        Assert.Equal("Own note", AutomationProperties.GetHelpText(button));
        button.HelpTextOverride = null;
        Texts.SetLanguage("de");
        button.Refresh();
        Assert.Equal("Hilfe: Was wird gezählt", AutomationProperties.GetName(button));
        Assert.Equal("Hilfe: Was wird gezählt", ToolTip.GetTip(button));
    }

    [Fact]
    public void TheHelpButtonShowsAPlainQuestionMarkNotACircledIconInsideItsOwnCircle()
    {
        HelpButton button = new() { HelpKey = "help.stats" };
        // The content is the drawn plain "?" (a path), not a catalog mask: the round button's outline is the one circle.
        Assert.Equal(WorkspaceIcons.Help, WorkspaceIcons.KeyOf(button.Content));
        Assert.IsType<Avalonia.Controls.Shapes.Path>(button.Content);
        string data = WorkspaceIcons.DataOf(WorkspaceIcons.Help);
        // No ring: no second full circle (a closed path, or two arcs that return to their start) in the icon.
        Assert.DoesNotContain("Z", data, StringComparison.OrdinalIgnoreCase);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(data, "A"));
        // Still a 32 dip circular button with the accessible name of the section.
        Assert.Equal("Help: What is counted", AutomationProperties.GetName(button));
        Assert.Contains("help-round", button.Classes);
        // The icon stays after the button is shown again (language change, re-attach): the same object, never a recreated catalog icon.
        object? icon = button.Content;
        button.Refresh();
        Assert.Same(icon, button.Content);
    }

    [Fact]
    public void TheHelpButtonSourceNamesNoCircledCatalogIcon()
    {
        string[] code = File.ReadAllLines(Root("Desktop/Controls/HelpButton.cs"))
            .Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal) && !line.TrimStart().StartsWith("///", StringComparison.Ordinal)).ToArray();
        string joined = string.Join("\n", code);
        Assert.DoesNotContain("-circle", joined, StringComparison.Ordinal);
        Assert.DoesNotContain("NamedIcon", joined, StringComparison.Ordinal);
        Assert.DoesNotContain("CommandBindings", joined, StringComparison.Ordinal);
        Assert.Contains("WorkspaceIcons.Create(WorkspaceIcons.Help)", joined, StringComparison.Ordinal);
    }

    [Fact]
    public void ABuiltSourceWinsOverTheKeyAndTheMoreLinkOfTheButtonIsHandedOn()
    {
        bool linked = false;
        HelpButton button = new() { HelpKey = "help.stats", Source = () => new HelpContent("Built", ["Text"], []), More = () => linked = true };
        Assert.Equal("Help: Built", AutomationProperties.GetName(button));
        HelpContent? resolved = button.Resolve();
        Assert.NotNull(resolved);
        resolved!.More!();
        Assert.True(linked);
        Assert.Null(new HelpButton().Resolve());
    }

    [Fact]
    public void TheTooltipStyleWrapsAndCapsTheWidthAt360AndTheHelpButtonIsA32DipCircle()
    {
        XElement styles = XDocument.Load(Root("Desktop/WorkspaceStyles.axaml")).Root!;
        Assert.Equal("360", Assert.Single(styles.Descendants(X + "Double"), e => (string?)e.Attribute(X + "Key") == "ToolTipMaxWidth").Value.Trim());
        Assert.Equal("360", Setter(styles, "ToolTip", "MaxWidth"));
        Assert.Equal("Wrap", Setter(styles, "ToolTip TextBlock", "TextWrapping"));
        Assert.Equal("{StaticResource Workspace.Size.Control}", Setter(styles, "Button.help-round", "Width"));
        Assert.Equal("{StaticResource Workspace.Size.Control}", Setter(styles, "Button.help-round", "Height"));
        Assert.Equal("16", Setter(styles, "Button.help-round", "CornerRadius"));
        Assert.Equal("32", Assert.Single(styles.Descendants(X + "Double"), e => (string?)e.Attribute(X + "Key") == "Workspace.Size.Control").Value.Trim());
        Assert.Equal("{StaticResource Workspace.Inset.Popover}", Setter(styles, "FlyoutPresenter.help-popover", "Padding"));
        Assert.Equal("{DynamicResource Workspace.ControlBorder}", Setter(styles, "FlyoutPresenter.help-popover", "BorderBrush"));
    }

    [Fact]
    public void TheHelpTextLayoutNamesItsPartsByClassAndEveryTextWrapsInsideTheReadingColumn()
    {
        StackPanel body = HelpPopover.Build(new HelpContent("Title", ["First", "Second"], ["One", "Two"]), () => { });
        Assert.Contains(HelpPopover.BodyClass, body.Classes);
        Assert.Contains(HelpPopover.TitleClass, body.Children[0].Classes);
        Assert.All(body.Children.Skip(1).Take(2), paragraph => Assert.Contains(HelpPopover.ParagraphClass, paragraph.Classes));
        StackPanel list = Assert.IsType<StackPanel>(body.Children[3]);
        Assert.Contains(HelpPopover.ListClass, list.Classes);
        foreach (Grid row in list.Children.Cast<Grid>())
        {
            Assert.Contains(HelpPopover.BulletRowClass, row.Classes);
            // Hanging indent: the dot alone in an Auto column, the text in the star column, so wrapped lines start under the text.
            Assert.Equal(2, row.ColumnDefinitions.Count);
            Assert.True(row.ColumnDefinitions[0].Width.IsAuto);
            Assert.True(row.ColumnDefinitions[1].Width.IsStar);
            Assert.Equal(0, Grid.GetColumn(row.Children[0]));
            Assert.Contains(HelpPopover.DotClass, row.Children[0].Classes);
            Assert.Equal(1, Grid.GetColumn(row.Children[1]));
            Assert.Contains(HelpPopover.BulletClass, row.Children[1].Classes);
        }
        // Every text of the popover wraps; the column is the reading width of the style file.
        Assert.All(body.GetLogicalDescendants().OfType<TextBlock>(), text => Assert.Equal(Avalonia.Media.TextWrapping.Wrap, text.TextWrapping));
        XElement styles = XDocument.Load(Root("Desktop/WorkspaceStyles.axaml")).Root!;
        Assert.Equal(HelpPopover.MaxContentWidth.ToString(System.Globalization.CultureInfo.InvariantCulture), Token(styles, "Workspace.Size.ReadingWidth"));
    }

    [Fact]
    public void TheHelpTextStylesUseTheSpacingTokensOnTheEightDipGridAndAReadingLineHeight()
    {
        XElement styles = XDocument.Load(Root("Desktop/WorkspaceStyles.axaml")).Root!;
        Assert.Equal("{StaticResource Workspace.Space.8}", Setter(styles, "StackPanel.help-popover-body", "Spacing"));
        Assert.Equal("{StaticResource Workspace.Space.4}", Setter(styles, "StackPanel.help-text-list", "Spacing"));
        Assert.Equal("{StaticResource Workspace.Space.8}", Setter(styles, "Grid.help-text-bullet-row", "ColumnSpacing"));
        Assert.Equal("{StaticResource Workspace.LineHeight.Body}", Setter(styles, "TextBlock.help-text-paragraph, TextBlock.help-text-bullet", "LineHeight"));
        Assert.Equal("Wrap", Setter(styles, "TextBlock.help-text-paragraph, TextBlock.help-text-bullet", "TextWrapping"));
        // About 1.4 x the body size, and the dot sits in the middle of the first line.
        double body = double.Parse(Token(styles, "Workspace.Text.Body"), System.Globalization.CultureInfo.InvariantCulture);
        double line = double.Parse(Token(styles, "Workspace.LineHeight.Body"), System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(line / body, 1.35, 1.45);
        double dot = double.Parse(Setter(styles, "Border.help-popover-dot", "Height")!, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal("0," + ((line - dot) / 2).ToString(System.Globalization.CultureInfo.InvariantCulture) + ",0,0", Setter(styles, "Border.help-popover-dot", "Margin"));
        // Text never sits closer than 16 dip to the popover edge, and the presenter is exactly column + inset + border wide.
        Assert.Equal("16", Assert.Single(styles.Descendants(A + "Thickness"), e => (string?)e.Attribute(X + "Key") == "Workspace.Inset.Popover").Value.Trim());
        Assert.Equal((HelpPopover.MaxContentWidth + 2 * 16 + 2).ToString(System.Globalization.CultureInfo.InvariantCulture), Setter(styles, "FlyoutPresenter.help-popover", "MaxWidth"));
        // Tooltips: 12 at the sides and 8 above and below, 2 dip between wrapped lines.
        Assert.Equal("12,8", Assert.Single(styles.Descendants(A + "Thickness"), e => (string?)e.Attribute(X + "Key") == "ToolTipPadding").Value.Trim());
        Assert.Equal("{StaticResource Workspace.Space.2}", Setter(styles, "ToolTip TextBlock", "LineSpacing"));
    }

    [Fact]
    public void EveryHelpPopoverIsTheSharedOneBuiltByTheHelpButton()
    {
        // The help-text layout is used by every help popover: only HelpButton creates a HelpPopover, and no other flyout borrows its class.
        foreach (string file in Directory.EnumerateFiles(Root("Desktop"), "*.*", SearchOption.AllDirectories)
            .Where(file => (file.EndsWith(".axaml", StringComparison.Ordinal) || file.EndsWith(".cs", StringComparison.Ordinal))
                && !file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)))
        {
            string name = Path.GetFileName(file);
            string text = File.ReadAllText(file);
            if (name != "HelpButton.cs" && name != "HelpPopover.cs")
                Assert.False(text.Contains("new HelpPopover(", StringComparison.Ordinal)
                    || System.Text.RegularExpressions.Regex.IsMatch(text, @"HelpPopover\s+\w+\s*=\s*new\(\)"), name + " builds its own help popover");
            if (name != "HelpPopover.cs" && name != "WorkspaceStyles.axaml")
                Assert.DoesNotContain("\"help-popover", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheRoundHelpButtonHasARoundFocusRingConcentricWithItsCircle()
    {
        XElement styles = XDocument.Load(Root("Desktop/WorkspaceStyles.axaml")).Root!;
        Assert.Equal("{StaticResource Workspace.FocusAdorner.Round}", Setter(styles, "Button.help-round", "FocusAdorner"));
        // Inside a toolbar (Tagging page) Ursa's borderless theme took the ring away; the help button keeps the standard button theme.
        Assert.Equal("{DynamicResource {x:Type Button}}", Setter(styles, "Button.help-round", "Theme"));
        XElement template = Assert.Single(styles.Descendants(A + "Template"), e => (string?)e.Attribute(X + "Key") == "Workspace.FocusAdorner.Round");
        XElement outer = Assert.Single(template.Elements(A + "Border"));
        XElement inner = Assert.Single(outer.Elements(A + "Border"));
        // Button radius 16 + the 4 dip offset; the inner ring 2 less (the outer ring's width).
        Assert.Equal(("20", "-4", "2", "False"), ((string?)outer.Attribute("CornerRadius"), (string?)outer.Attribute("Margin"), (string?)outer.Attribute("BorderThickness"), (string?)outer.Attribute("AdornerLayer.IsClipEnabled")));
        Assert.Equal(("18", "1"), ((string?)inner.Attribute("CornerRadius"), (string?)inner.Attribute("BorderThickness")));
        Assert.Equal("{DynamicResource Workspace.Accent}", (string?)outer.Attribute("BorderBrush"));
        Assert.Equal("{DynamicResource Workspace.Background}", (string?)inner.Attribute("BorderBrush"));
    }

    [Fact]
    public void TheQuestionMarkStaysOneOpenCurlOnTheMiddleAxisOfTheIconGrid()
    {
        string data = WorkspaceIcons.DataOf(WorkspaceIcons.Help);
        // Single ring rule: one arc, nothing closed, and the arc is the small curl (radius 3), never a ring around the 16 dip grid.
        Assert.DoesNotContain("Z", data, StringComparison.OrdinalIgnoreCase);
        System.Text.RegularExpressions.Match arc = Assert.Single(System.Text.RegularExpressions.Regex.Matches(data,
            @"A([\d.]+),([\d.]+) 0 0 1 ([\d.]+),([\d.]+)"));
        double radius = double.Parse(arc.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(radius, 2, 4);
        // The arc ends at the curl's rightmost point (its circle centre + radius): that centre and the dot share the axis x = 8.
        double end = double.Parse(arc.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(8, end - radius, 3);
        System.Text.RegularExpressions.Match dot = System.Text.RegularExpressions.Regex.Match(data, @"M([\d.]+),([\d.]+) L([\d.]+),([\d.]+)$");
        Assert.True(dot.Success, data);
        Assert.Equal("8", dot.Groups[1].Value);
        Assert.Equal("8", dot.Groups[3].Value);
        // Vertically the glyph (curl top to dot bottom, with the 1.5 stroke) sits in the middle of the grid or at most half a dip below it.
        double top = double.Parse(arc.Groups[4].Value, System.Globalization.CultureInfo.InvariantCulture) - radius - 0.75;
        double bottom = double.Parse(dot.Groups[4].Value, System.Globalization.CultureInfo.InvariantCulture) + 0.75;
        Assert.InRange((top + bottom) / 2, 8, 8.5);
    }

    [Fact]
    public void NoButtonUsesAQuestionMarkAsItsContentAndTheStatisticsPageUsesTheSharedButton()
    {
        string[] sources = Directory.EnumerateFiles(Root("Desktop"), "*.*", SearchOption.AllDirectories)
            .Where(file => file.EndsWith(".axaml", StringComparison.Ordinal) || file.EndsWith(".cs", StringComparison.Ordinal))
            .Where(file => !file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)).ToArray();
        foreach (string file in sources)
        {
            string text = File.ReadAllText(file);
            Assert.DoesNotContain("Content=\"?\"", text, StringComparison.Ordinal);
            Assert.DoesNotContain("Content = \"?\"", text, StringComparison.Ordinal);
        }
        XElement page = XDocument.Load(Root("Desktop/Views/Statistics/StatisticsPage.axaml")).Root!;
        XElement help = Assert.Single(page.Descendants(), e => (string?)e.Attribute(X + "Name") == "StatisticsHelpButton");
        Assert.Equal("HelpButton", help.Name.LocalName);
        Assert.Equal("help.stats", (string?)help.Attribute("HelpKey"));
    }

    [Fact]
    public void ACommandTooltipHasTheKeyOnTheFirstLineAndHintAndReasonOnTheSecond()
    {
        string id = "archive.select-page";
        string label = CommandCatalog.Label(id);
        string? shortcut = CommandCatalog.ShortcutDisplay(id);
        string headline = shortcut is null ? label : label + " · " + shortcut;
        string hint = CommandCatalog.ScopeHint(id)!;
        Assert.Equal(headline + "\n" + hint, CommandBindings.Tooltip(id));
        Assert.Equal(headline + "\n" + hint + " · No page is open", CommandBindings.Tooltip(id, "No page is open"));
        Assert.Equal(headline, CommandBindings.TooltipHeadline(id));
        Assert.Equal("Custom label" + (shortcut is null ? "" : " · " + shortcut) + "\n" + hint, CommandBindings.Tooltip(id, null, "Custom label"));
        // A command without a hint has the reason alone on line two, and none at all stays one line.
        string plain = CommandCatalog.All.First(command => command.IsImplemented && CommandCatalog.ScopeHint(command.Id) is null).Id;
        string plainHeadline = CommandBindings.Tooltip(plain);
        Assert.DoesNotContain('\n', plainHeadline);
        Assert.Equal(plainHeadline + "\nBecause", CommandBindings.Tooltip(plain, "Because"));
        Assert.Equal(plainHeadline, CommandBindings.TooltipHeadline(plain));
        Assert.DoesNotContain('\n', CommandBindings.Tooltip(plain, "  "));
    }

    private static string Token(XElement styles, string key) =>
        Assert.Single(styles.Descendants(), e => (string?)e.Attribute(X + "Key") == key).Value.Trim();

    private static string? Setter(XElement styles, string selector, string property) =>
        (string?)Assert.Single(Assert.Single(styles.Elements(A + "Style"), style => (string?)style.Attribute("Selector") == selector)
            .Elements(A + "Setter"), setter => (string?)setter.Attribute("Property") == property).Attribute("Value");

    private static string Root(string relative, [CallerFilePath] string sourcePath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "../..", relative));
}
