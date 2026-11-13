using System.Runtime.CompilerServices;
using System.Xml.Linq;

namespace MeowChan.DesktopTests;

// Parsed source contracts only: no Avalonia application, GUI or profile IO. Contrast checks use the
// dictionary hex values, not rendered pixels; theme resolution, DPI and accessibility acceptance stay smoke/capture evidence.
public sealed class ReaderPostSurfaceTests
{
    private static readonly XNamespace A = "https://github.com/avaloniaui";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly XNamespace Desktop = "clr-namespace:MeowChan.Desktop";
    [Theory]
    [InlineData("Dark", "#121416", "#20242A", "#5A636E")]
    [InlineData("Light", "#E3DFD8", "#FAF7F1", "#7A7062")]
    public void PostCardTokensReplaceTheSeparatorWithoutChangingThePalette(string theme, string page, string card, string edge)
    {
        XElement dictionary = Theme(theme);
        Assert.Equal(page, Resource(dictionary, "Workspace.ReaderPage").Value);
        Assert.Equal(card, Resource(dictionary, "Workspace.ReaderPostSurface").Value);
        Assert.Equal(edge, Resource(dictionary, "Workspace.ReaderPostEdge").Value);
        // The page is the window background tone; the shared palette roles stay as they were.
        Assert.Equal(page, Resource(dictionary, "Workspace.Background").Value);
        Assert.Equal(theme == "Dark" ? "#2D3239" : "#A89F92", Resource(dictionary, "Workspace.Border").Value);
        Assert.Equal(theme == "Dark" ? "#1B1E22" : "#EFECE6", Resource(dictionary, "Workspace.Panel").Value);
        foreach (string retired in new[] { "Workspace.ReaderActiveSurface", "Workspace.ReaderPostSeparatorHeight" })
            Assert.DoesNotContain(dictionary.Elements(), element => (string?)element.Attribute(X + "Key") == retired);
    }

    // Dictionary values, not rendered pixels: the edge is a non-text boundary (>= 3:1 against the page);
    // reading text, meta and links stay >= 4.5:1 on the card and on the active card.
    [Theory]
    [InlineData("Dark")]
    [InlineData("Light")]
    public void CardEdgeAndPostTextMeetTheirContrastTargets(string theme)
    {
        XElement dictionary = Theme(theme);
        string Hex(string key) => Resource(dictionary, key).Value;
        Assert.True(Contrast(Hex("Workspace.ReaderPostEdge"), Hex("Workspace.ReaderPage")) >= 3.0);
        Assert.True(Contrast(Hex("Workspace.Accent"), Hex("Workspace.ReaderPage")) >= 3.0);
        foreach (string surface in new[] { "Workspace.ReaderPostSurface", "Workspace.AccentSurface", "Workspace.ReaderPage" })
            foreach (string text in new[] { "Workspace.Text", "Workspace.Muted", "Workspace.Accent" })
                Assert.True(Contrast(Hex(text), Hex(surface)) >= 4.5, $"{theme}: {text} on {surface}");
    }

    [Fact]
    public void PostsSitOnThePageToneWithoutASeparatorRule()
    {
        XElement posts = Posts();
        Assert.Equal("{DynamicResource Workspace.ReaderPage}", (string?)posts.Attribute("Background"));
        Assert.Equal("2,4", (string?)posts.Attribute("Padding"));
        XElement root = PostRoot();
        Assert.Equal(A + "StackPanel", root.Name);
        Assert.Equal("8,8,8,12", (string?)root.Attribute("Margin"));
        Assert.Equal("6", (string?)root.Attribute("Spacing"));
        Assert.Null(root.Attribute("Background"));
        Assert.Empty(root.Descendants(A + "Border")); // The card is the item itself; no card in the card.
        Assert.DoesNotContain(Source("Desktop/Views/Archive/ArchivePage.axaml").Descendants(), element =>
            element.Attributes().Any(attribute => attribute.Value.Contains("ReaderPostSeparatorHeight", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("ListBoxItem")]
    [InlineData("ListBoxItem /template/ ContentPresenter")]
    public void InactivePostIsACardWithAnEdge(string selector)
    {
        XElement style = PostStyle(selector);
        Assert.Equal("{DynamicResource Workspace.ReaderPostSurface}", Setting(style, "Background"));
        Assert.Equal("{DynamicResource Workspace.ReaderPostEdge}", Setting(style, "BorderBrush"));
        Assert.Equal("1", Setting(style, "BorderThickness"));
        Assert.Equal("4", Setting(style, "CornerRadius"));
        if (selector == "ListBoxItem")
        {
            Assert.Equal("6,4", Setting(style, "Margin"));
            Assert.Equal("Stretch", Setting(style, "HorizontalContentAlignment"));
        }
    }

    [Theory]
    [InlineData("ListBoxItem:pointerover /template/ ContentPresenter")]
    [InlineData("ListBoxItem:pressed /template/ ContentPresenter")]
    public void SemiHoverAndPressedTintsDoNotRecolourCards(string selector)
    {
        Assert.Equal("{DynamicResource Workspace.ReaderPostSurface}", Setting(PostStyle(selector), "Background"));
    }

    [Theory]
    [InlineData("ListBoxItem:selected")]
    [InlineData("ListBoxItem:selected /template/ ContentPresenter")]
    [InlineData("ListBoxItem:selected:pointerover /template/ ContentPresenter")]
    [InlineData("ListBoxItem:selected:pressed /template/ ContentPresenter")]
    [InlineData("ListBoxItem:selected:focus /template/ ContentPresenter")]
    public void ActivePostCardKeepsItsSurfaceInEveryState(string selector)
    {
        Assert.Equal("{DynamicResource Workspace.AccentSurface}", Setting(PostStyle(selector), "Background"));
        string[] order = Posts().Element(A + "ListBox.Styles")!.Elements(A + "Style")
            .Select(style => (string?)style.Attribute("Selector") ?? "").ToArray();
        // Later styles win: the active surface must follow the suppressed hover/pressed tints.
        Assert.True(Array.IndexOf(order, selector) > Array.IndexOf(order, "ListBoxItem:pressed /template/ ContentPresenter"));
    }

    [Fact]
    public void SelectionEdgeAndKeyboardFocusRemainDistinct()
    {
        foreach (string selector in new[] { "ListBoxItem:selected", "ListBoxItem:selected /template/ ContentPresenter" })
        {
            XElement style = PostStyle(selector);
            Assert.Equal("{DynamicResource Workspace.Accent}", Setting(style, "BorderBrush"));
            Assert.Equal("3,1,1,1", Setting(style, "BorderThickness"));
        }
        XElement focus = PostStyle("ListBoxItem:focus-visible /template/ ContentPresenter");
        Assert.Equal("{DynamicResource Workspace.Accent}", Setting(focus, "BorderBrush"));
        Assert.Equal("3,2,2,2", Setting(focus, "BorderThickness"));
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
    public void ReferenceButtonsReadAsLinks()
    {
        XElement style = PostStyle("Button.reader-quote");
        Assert.Equal("Transparent", Setting(style, "Background"));
        Assert.Equal("0", Setting(style, "BorderThickness"));
        Assert.Equal("{DynamicResource Workspace.Accent}", Setting(style, "Foreground"));
        Assert.Equal("24", Setting(style, "MinHeight"));
    }

    [Theory]
    [InlineData(13, 19)]
    [InlineData(15, 22)]
    [InlineData(18, 26)]
    [InlineData(22, 32)]
    public void ReadingLineHeightFollowsTheReadingSize(double fontSize, double lineHeight)
    {
        Assert.Equal(lineHeight, MeowChan.Desktop.ReaderBodyTextBlock.ReadingLineHeight(fontSize));
    }

    [Fact]
    public void HeaderIsOneLineInImageboardOrderWithACopyableNumber()
    {
        XElement header = PostRoot().Elements().First();
        Assert.Equal(A + "Grid", header.Name);
        Assert.Equal("*,Auto", (string?)header.Attribute("ColumnDefinitions"));
        XElement line = Assert.Single(header.Elements(A + "WrapPanel"));
        Assert.Equal(new[] { "{Binding Subject}", "{Binding Author}", "{Binding Posted}", "{Binding NumberLabel}", "{Binding RemovedLabel}" },
            line.Elements().Select(element => (string?)element.Attribute("Text") ?? (string?)element.Attribute("Content")));
        XElement number = Assert.Single(line.Elements(A + "Button"));
        Assert.Equal("link reader-post-number", (string?)number.Attribute("Classes"));
        Assert.Equal("OnCopyPostNumberClick", (string?)number.Attribute("Click"));
        Assert.Equal("{Binding CopyNumberLabel}", (string?)number.Attribute("AutomationProperties.Name"));
        Assert.Equal("{Binding CopyNumberLabel}", (string?)number.Attribute("ToolTip.Tip"));
        Assert.Equal("24", Setting(PostStyle("Button.reader-post-number"), "MinHeight"));
        XElement count = Assert.Single(header.Elements(A + "TextBlock"));
        Assert.Equal("1", (string?)count.Attribute("Grid.Column"));
        Assert.Equal("{Binding BacklinkCountLabel}", (string?)count.Attribute("Text"));
        Assert.Equal("{Binding HasBacklinks}", (string?)count.Attribute("IsVisible"));
        Assert.DoesNotContain(PostRoot().Descendants(), element => (string?)element.Attribute("Text") == "{Binding Header}");
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

    private static XElement Theme(string theme) => Assert.Single(Source("Desktop/WorkspaceStyles.axaml")
        .Descendants(A + "ResourceDictionary").Where(element => (string?)element.Attribute(X + "Key") == theme));

    private static double Contrast(string first, string second)
    {
        static double Luminance(string hex)
        {
            double[] channel = Enumerable.Range(0, 3).Select(index =>
            {
                double value = Convert.ToInt32(hex.Substring(1 + index * 2, 2), 16) / 255d;
                return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
            }).ToArray();
            return 0.2126 * channel[0] + 0.7152 * channel[1] + 0.0722 * channel[2];
        }
        double a = Luminance(first), b = Luminance(second);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    private static XElement Resource(XElement dictionary, string key) => Assert.Single(dictionary.Elements()
        .Where(element => (string?)element.Attribute(X + "Key") == key));

    private static XElement Source(string relative, [CallerFilePath] string sourcePath = "") =>
        XDocument.Load(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "../..", relative))).Root!;
}
