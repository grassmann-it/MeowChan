using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
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
        Assert.Equal(360, HelpPopover.MaxContentWidth);
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
        Assert.Equal("12", Setter(styles, "FlyoutPresenter.help-popover", "Padding"));
        Assert.Equal("{DynamicResource Workspace.ControlBorder}", Setter(styles, "FlyoutPresenter.help-popover", "BorderBrush"));
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

    private static string? Setter(XElement styles, string selector, string property) =>
        (string?)Assert.Single(Assert.Single(styles.Elements(A + "Style"), style => (string?)style.Attribute("Selector") == selector)
            .Elements(A + "Setter"), setter => (string?)setter.Attribute("Property") == property).Attribute("Value");

    private static string Root(string relative, [CallerFilePath] string sourcePath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "../..", relative));
}
