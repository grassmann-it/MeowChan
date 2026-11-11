using System.Runtime.CompilerServices;
using System.Xml.Linq;

namespace MeowChan.DesktopTests;

// Parsed source contracts only: no Avalonia application, GUI, profile IO, or
// claims about rendered contrast, theme resolution, DPI, or accessibility acceptance.
public sealed class ReaderPostSurfaceTests
{
    private static readonly XNamespace A = "https://github.com/avaloniaui";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly XNamespace Desktop = "clr-namespace:MeowChan.Desktop";
    private const string SeparatorHeight = "Workspace.ReaderPostSeparatorHeight";

    [Theory]
    [InlineData("Dark", "1")]
    [InlineData("Light", "0")]
    public void DecorativeRuleIsDarkOnlyWithoutChangingPaletteBrushes(string theme, string height)
    {
        XElement styles = Source("Desktop/WorkspaceStyles.axaml");
        XElement dictionary = Assert.Single(styles.Descendants(A + "ResourceDictionary")
            .Where(element => (string?)element.Attribute(X + "Key") == theme));
        XElement resource = Assert.Single(dictionary.Elements()
            .Where(element => (string?)element.Attribute(X + "Key") == SeparatorHeight));
        Assert.Equal(X + "Double", resource.Name);
        Assert.Equal(height, resource.Value);
        // Reuse the existing decorative border role, not a new contrast/palette role.
        Assert.Equal(theme == "Dark" ? "#2D3239" : "#A89F92", Resource(dictionary, "Workspace.Border").Value);
        Assert.Equal(theme == "Dark" ? "#1B1E22" : "#EFECE6", Resource(dictionary, "Workspace.Panel").Value);
        Assert.Equal(theme == "Dark" ? "#152233" : "#E8E0D5", Resource(dictionary, "Workspace.ReaderActiveSurface").Value);
    }

    [Fact]
    public void SeparatorOverlaysTheExistingGapWithoutASecondCardOrExtraLayoutRow()
    {
        XElement root = PostRoot();
        Assert.Equal(A + "Grid", root.Name);
        Assert.Empty(root.Attributes()); // No background, fixed size, rows, padding, or clipping.
        Assert.Equal(new[] { A + "StackPanel", A + "Border" }, root.Elements().Select(element => element.Name));
        XElement body = root.Element(A + "StackPanel")!;
        Assert.Equal("8,8,8,12", (string?)body.Attribute("Margin"));
        Assert.Equal("6", (string?)body.Attribute("Spacing"));
        Assert.Null(body.Attribute("Background"));
        Assert.Empty(body.Descendants(A + "Border")); // No cards around quotes/replies/attachments.

        XElement rule = root.Element(A + "Border")!;
        Assert.Equal("{DynamicResource " + SeparatorHeight + "}", (string?)rule.Attribute("Height"));
        Assert.Equal("{DynamicResource Workspace.Border}", (string?)rule.Attribute("Background"));
        Assert.Equal("8,0", (string?)rule.Attribute("Margin"));
        Assert.Equal("Bottom", (string?)rule.Attribute("VerticalAlignment"));
        Assert.Null(rule.Attribute("BorderThickness"));
        Assert.Null(rule.Attribute("CornerRadius"));
        Assert.Empty(rule.Elements());
    }

    [Fact]
    public void SeparatorResourceIsConsumedOnlyByThePostTemplateNotTheToolbarOrOtherTabs()
    {
        XElement page = Source("Desktop/Views/Archive/ArchivePage.axaml");
        XElement rule = Assert.Single(page.Descendants().Where(element => element.Attributes().Any(attribute =>
            attribute.Value == "{DynamicResource " + SeparatorHeight + "}")));
        Assert.Equal(A + "Border", rule.Name);
        Assert.Contains(rule.Ancestors(A + "ListBox"), element =>
            (string?)element.Attribute(X + "Name") == "ArchivePosts");
        Assert.DoesNotContain(rule.Ancestors(), element =>
            (string?)element.Attribute(X + "Name") == "ReaderToolbar");
    }

    [Fact]
    public void DecorativeRuleCannotInterceptMediaQuoteOrKeyboardInteraction()
    {
        XElement rule = PostRoot().Element(A + "Border")!;
        Assert.Equal("False", (string?)rule.Attribute("IsHitTestVisible"));
        Assert.Equal("Raw", (string?)rule.Attribute("AutomationProperties.AccessibilityView"));
        Assert.Null(rule.Attribute("Focusable"));
        Assert.DoesNotContain(rule.Attributes(), attribute =>
            attribute.Name.LocalName.StartsWith("Pointer", StringComparison.Ordinal) ||
            attribute.Name.LocalName.StartsWith("Key", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("ListBoxItem:selected")]
    [InlineData("ListBoxItem:selected /template/ ContentPresenter")]
    [InlineData("ListBoxItem:selected:pointerover /template/ ContentPresenter")]
    [InlineData("ListBoxItem:selected:focus /template/ ContentPresenter")]
    public void ActivePostSurfaceKeepsItsExistingPrecedence(string selector)
    {
        Assert.Equal("{DynamicResource Workspace.ReaderActiveSurface}", Setting(PostStyle(selector), "Background"));
    }

    [Fact]
    public void SelectionEdgeAndKeyboardFocusRemainSeparateFromTheDecorativeRule()
    {
        foreach (string selector in new[] { "ListBoxItem:selected", "ListBoxItem:selected /template/ ContentPresenter" })
        {
            XElement style = PostStyle(selector);
            Assert.Equal("{DynamicResource Workspace.Accent}", Setting(style, "BorderBrush"));
            Assert.Equal("3,0,0,0", Setting(style, "BorderThickness"));
        }
        XElement focus = PostStyle("ListBoxItem:focus-visible /template/ ContentPresenter");
        Assert.Equal("{DynamicResource Workspace.Accent}", Setting(focus, "BorderBrush"));
        Assert.Equal("3,1,1,1", Setting(focus, "BorderThickness"));
        XElement styles = Source("Desktop/WorkspaceStyles.axaml");
        XElement adornerStyle = Assert.Single(styles.Elements(A + "Style").Where(style =>
            ((string?)style.Attribute("Selector"))?.Split(", ").Contains("ListBoxItem") == true &&
            style.Elements(A + "Setter").Any(setter => (string?)setter.Attribute("Property") == "FocusAdorner")));
        Assert.Equal("{StaticResource Workspace.FocusAdorner}", Setting(adornerStyle, "FocusAdorner"));
        XElement adorner = Assert.Single(styles.Descendants(A + "Template").Where(element =>
            (string?)element.Attribute(X + "Key") == "Workspace.FocusAdorner"));
        Assert.Equal("-4", (string?)adorner.Element(A + "Border")!.Attribute("Margin"));
    }

    [Fact]
    public void ReaderFontAndAttachmentLayoutRetainTheirExistingBindings()
    {
        XElement root = PostRoot();
        XElement layout = Assert.Single(root.Descendants(Desktop + "ReaderPostLayout"));
        XElement attachments = Assert.Single(layout.Elements(A + "ItemsControl"));
        Assert.Equal("{Binding Attachments}", (string?)attachments.Attribute("ItemsSource"));
        Assert.Equal("{Binding HasAttachments}", (string?)attachments.Attribute("IsVisible"));
        XElement attachment = Assert.Single(attachments.Descendants(Desktop + "ArchiveAttachmentView"));
        Assert.Equal("0,2,0,8", (string?)attachment.Attribute("Margin"));
        XElement body = Assert.Single(layout.Descendants(Desktop + "ReaderBodyTextBlock"));
        Assert.Equal("{Binding Body}", (string?)body.Attribute("Text"));
        Assert.Equal("{Binding IsTextOnly}", (string?)body.Attribute("IsTextOnly"));
        Assert.Equal("{Binding $parent[ListBox].FontSize}", (string?)body.Attribute("FontSize"));
        Assert.Equal("Wrap", (string?)body.Attribute("TextWrapping"));
        Assert.DoesNotContain(root.DescendantsAndSelf(), element => element.Attribute("FontFamily") != null);
    }

    [Fact]
    public void QuotesAndBacklinksRetainNavigationNamesAndDisclosure()
    {
        XElement root = PostRoot();
        XElement[] quoteButtons = root.Descendants(A + "Button").Where(element =>
            (string?)element.Attribute("Click") == "OnQuoteClick").ToArray();
        Assert.Equal(2, quoteButtons.Length); // Quote and expanded backlink templates.
        foreach (XElement button in quoteButtons)
        {
            Assert.Equal("{Binding Label}", (string?)button.Attribute("Content"));
            Assert.Equal("{Binding AccessibleLabel}", (string?)button.Attribute("ToolTip.Tip"));
            Assert.Equal("{Binding AccessibleLabel}", (string?)button.Attribute("AutomationProperties.Name"));
            Assert.Equal("reader.open-quote", Assert.Single(button.Attributes().Where(attribute =>
                attribute.Name.LocalName == "CommandPresentation.Id")).Value);
        }
        foreach (string binding in new[] { "{Binding Quotes}", "{Binding Replies}" })
            Assert.Single(root.Descendants(A + "ItemsControl").Where(element =>
                (string?)element.Attribute("ItemsSource") == binding));
        XElement backlinks = Assert.Single(root.Descendants(A + "Expander"));
        Assert.Equal("{Binding BacklinksLabel}", (string?)backlinks.Attribute("Header"));
        Assert.Equal("{Binding CanExpandReplies}", (string?)backlinks.Attribute("IsVisible"));
        Assert.Equal("{Binding RepliesExpanded, Mode=TwoWay}", (string?)backlinks.Attribute("IsExpanded"));
        Assert.Equal("OnRepliesExpanded", (string?)backlinks.Attribute("Expanded"));
        Assert.Equal("OnRepliesCollapsed", (string?)backlinks.Attribute("Collapsed"));
    }

    private static XElement PostRoot()
    {
        XElement template = Posts().Element(A + "ListBox.ItemTemplate")!.Element(A + "DataTemplate")!;
        Assert.Equal("desktop:ArchivePostRow", (string?)template.Attribute(X + "DataType"));
        return Assert.Single(template.Elements());
    }

    private static XElement Posts() => Assert.Single(Source("Desktop/Views/Archive/ArchivePage.axaml")
        .Descendants(A + "ListBox").Where(element => (string?)element.Attribute(X + "Name") == "ArchivePosts"));

    private static XElement PostStyle(string selector) => Assert.Single(Posts().Element(A + "ListBox.Styles")!
        .Elements(A + "Style").Where(element => (string?)element.Attribute("Selector") == selector));

    private static string? Setting(XElement style, string property) => (string?)Assert.Single(style.Elements(A + "Setter")
        .Where(element => (string?)element.Attribute("Property") == property)).Attribute("Value");

    private static XElement Resource(XElement dictionary, string key) => Assert.Single(dictionary.Elements()
        .Where(element => (string?)element.Attribute(X + "Key") == key));

    private static XElement Source(string relative, [CallerFilePath] string sourcePath = "") =>
        XDocument.Load(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "../..", relative))).Root!;
}
