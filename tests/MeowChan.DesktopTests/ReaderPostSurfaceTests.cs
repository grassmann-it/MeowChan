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
    [InlineData("Light", "#E3DFD8", "#FAF7F1", "#726858")]
    public void PostCardTokensReplaceTheSeparatorWithoutChangingThePalette(string theme, string page, string card, string edge)
    {
        XElement dictionary = Theme(theme);
        Assert.Equal(page, Resource(dictionary, "Workspace.Background").Value);
        Assert.Equal(card, Resource(dictionary, "Workspace.Card").Value);
        Assert.Equal(edge, Resource(dictionary, "Workspace.CardEdge").Value);
        // The page is the window background tone; the shared palette roles stay as they were.
        Assert.Equal(theme == "Dark" ? "#2D3239" : "#948A7D", Resource(dictionary, "Workspace.Border").Value);
        Assert.Equal(theme == "Dark" ? "#1B1E22" : "#EFECE6", Resource(dictionary, "Workspace.Panel").Value);
        foreach (string retired in new[] { "Workspace.ReaderActiveSurface", "Workspace.ReaderPostSeparatorHeight", "Workspace.ReaderPage", "Workspace.ReaderPostSurface", "Workspace.ReaderPostEdge" })
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
        Assert.True(Contrast(Hex("Workspace.CardEdge"), Hex("Workspace.Background")) >= 3.0);
        Assert.True(Contrast(Hex("Workspace.Accent"), Hex("Workspace.Background")) >= 3.0);
        foreach (string surface in new[] { "Workspace.Card", "Workspace.AccentSurface", "Workspace.Background" })
            foreach (string text in new[] { "Workspace.Text", "Workspace.Muted", "Workspace.Accent" })
                Assert.True(Contrast(Hex(text), Hex(surface)) >= 4.5, $"{theme}: {text} on {surface}");
    }

    [Fact]
    public void PostsSitOnThePageToneWithoutASeparatorRule()
    {
        XElement posts = Posts();
        Assert.Equal("{DynamicResource Workspace.Background}", (string?)posts.Attribute("Background"));
        Assert.Equal("0,4", (string?)posts.Attribute("Padding"));
        XElement root = PostRoot();
        Assert.Equal(A + "StackPanel", root.Name);
        Assert.Equal("8,8,8,12", (string?)root.Attribute("Margin"));
        Assert.Equal("8", (string?)root.Attribute("Spacing"));
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
        Assert.Equal("{DynamicResource Workspace.Card}", Setting(style, "Background"));
        Assert.Equal("{DynamicResource Workspace.CardEdge}", Setting(style, "BorderBrush"));
        Assert.Equal("1", Setting(style, "BorderThickness"));
        Assert.Equal("{StaticResource Workspace.Radius.Control}", Setting(style, "CornerRadius"));
        if (selector == "ListBoxItem")
        {
            Assert.Equal("8,4", Setting(style, "Margin"));
            Assert.Equal("Stretch", Setting(style, "HorizontalContentAlignment"));
        }
    }

    [Theory]
    [InlineData("ListBoxItem:pointerover /template/ ContentPresenter")]
    [InlineData("ListBoxItem:pressed /template/ ContentPresenter")]
    public void SemiHoverAndPressedTintsDoNotRecolourCards(string selector)
    {
        Assert.Equal("{DynamicResource Workspace.Card}", Setting(PostStyle(selector), "Background"));
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
        Assert.Equal("{StaticResource Workspace.Size.ControlCompact}", Setting(style, "MinHeight"));
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
        // The reading-mark line (a Grid with a label and a rule) may sit above the header; the header is the next Grid.
        XElement header = PostRoot().Elements(A + "Grid").First(element => (string?)element.Attribute("ColumnDefinitions") == "*,Auto");
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
        Assert.Equal("{StaticResource Workspace.Size.ControlCompact}", Setting(PostStyle("Button.reader-post-number"), "MinHeight"));
        // The reply counter at the right is the toggle for the nested reply cards (I2).
        XElement counter = Assert.Single(header.Elements(A + "Button"));
        Assert.Equal("1", (string?)counter.Attribute("Grid.Column"));
        Assert.Equal("{Binding HasReplyToggle}", (string?)counter.Attribute("IsVisible"));
        Assert.Equal("{Binding BacklinkCountLabel}", (string?)Assert.Single(counter.Descendants(A + "TextBlock")).Attribute("Text"));
        Assert.DoesNotContain(PostRoot().Descendants(), element => (string?)element.Attribute("Text") == "{Binding Header}");
    }

    [Fact]
    public void ReaderFontAndAttachmentLayoutRetainTheirExistingBindings()
    {
        XElement root = PostRoot();
        XElement layout = Assert.Single(root.Descendants(Desktop + "ReaderPostLayout"));
        XElement attachments = Assert.Single(layout.Elements(A + "ItemsControl"));
        Assert.Equal("{Binding Attachments}", (string?)attachments.Attribute("ItemsSource"));
        // "Hide media" (reader view) gives the slot way to a short line; without it the slot shows as before.
        Assert.Equal("{Binding ShowsMedia}", (string?)attachments.Attribute("IsVisible"));
        XElement attachment = Assert.Single(attachments.Descendants(Desktop + "ArchiveAttachmentView"));
        Assert.Equal("0,2,0,8", (string?)attachment.Attribute("Margin"));
        XElement body = Assert.Single(layout.Descendants(Desktop + "ReaderBodyTextBlock"));
        Assert.Equal("{Binding BodyDocument}", (string?)body.Attribute("Document"));
        Assert.Null(body.Attribute("Text")); // Text/Inlines are chosen by the control from the document.
        Assert.Equal("OnInlineQuoteInvoked", (string?)body.Attribute("QuoteInvoked"));
        Assert.Equal("{Binding IsTextOnly}", (string?)body.Attribute("IsTextOnly"));
        Assert.Equal("{Binding $parent[ListBox].FontSize}", (string?)body.Attribute("FontSize"));
        Assert.Equal("Wrap", (string?)body.Attribute("TextWrapping"));
        Assert.Equal("reader-body", (string?)body.Attribute("Classes")); // Reading font via the shared style, not a local FontFamily.
        Assert.DoesNotContain(root.DescendantsAndSelf(), element => element.Attribute("FontFamily") != null);
    }

    [Fact]
    public void QuotesAndBacklinksRetainNavigationNamesAndDisclosure()
    {
        XElement root = PostRoot();
        XElement[] quoteButtons = root.Descendants(A + "Button").Where(element =>
            (string?)element.Attribute("Click") == "OnQuoteClick").ToArray();
        Assert.Equal(2, quoteButtons.Length); // Keyboard quote row and the nested reply cards.
        XElement card = Assert.Single(quoteButtons, button => ((string?)button.Attribute("Classes"))?.Contains("reader-reply-card") == true);
        Assert.Equal("{Binding AccessibleLabel}", (string?)card.Attribute("ToolTip.Tip"));
        Assert.Equal("{Binding AccessibleLabel}", (string?)card.Attribute("AutomationProperties.Name"));
        Assert.Equal("reader.open-quote", Assert.Single(card.Attributes().Where(attribute => attribute.Name.LocalName == "CommandPresentation.Id")).Value);
        Assert.Contains(card.Descendants(A + "TextBlock"), text => (string?)text.Attribute("Text") == "{Binding ReplyCardHeader}");
        Assert.Contains(card.Descendants(A + "TextBlock"), text => (string?)text.Attribute("Text") == "{Binding ReplyExcerpt}");
        foreach (XElement button in quoteButtons.Where(button => button != card))
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
        Assert.Empty(root.Descendants(A + "Expander")); // The header counter replaced the expander (I2).
        XElement toggle = Assert.Single(root.Descendants(A + "Button"), button => ((string?)button.Attribute("Classes"))?.Contains("reader-reply-toggle") == true);
        Assert.Equal("{Binding HasReplyToggle}", (string?)toggle.Attribute("IsVisible"));
        Assert.Equal("OnReplyToggleClick", (string?)toggle.Attribute("Click"));
        Assert.Equal("{Binding ReplyToggleLabel}", (string?)toggle.Attribute("AutomationProperties.Name"));
        Assert.Equal("{Binding ReplyToggleLabel}", (string?)toggle.Attribute("ToolTip.Tip"));
        Assert.Contains(toggle.Descendants(A + "TextBlock"), text => (string?)text.Attribute("Text") == "{Binding BacklinkCountLabel}");
        XElement cards = Assert.Single(root.Descendants(A + "StackPanel"), panel => (string?)panel.Attribute("IsVisible") == "{Binding RepliesExpanded}");
        Assert.Contains(cards.Descendants(A + "Rectangle"), rail => (string?)rail.Attribute("Width") == "3"); // The left rail.
        Assert.Contains(cards.Descendants(A + "Button"), more => (string?)more.Attribute("Click") == "OnMoreRepliesClick");
    }

    [Fact]
    public void InlineReferencesKeepAKeyboardRowAfterTheText()
    {
        XElement root = PostRoot(); // One parsed document: the order check compares element identity.
        XElement body = Assert.Single(root.Descendants(Desktop + "ReaderBodyTextBlock"));
        XElement keys = Assert.Single(body.ElementsAfterSelf(A + "ItemsControl"));
        Assert.Equal("{Binding Quotes}", (string?)keys.Attribute("ItemsSource"));
        Assert.Equal("reader-quote-keys", (string?)keys.Attribute("Classes"));
        Assert.Equal("{Binding HasUnplacedQuotes}", (string?)keys.Attribute("Classes.reader-quotes-visible"));
        Assert.Equal("{Binding QuotesLabel}", (string?)keys.Attribute("AutomationProperties.Name"));
        Assert.Single(keys.Descendants(A + "Button").Where(button => (string?)button.Attribute("Click") == "OnQuoteClick"));
        // Collapsed only while neither focused nor needed for an unplaced reference.
        XElement collapsed = PostStyle("ItemsControl.reader-quote-keys:not(:focus-within):not(.reader-quotes-visible)");
        Assert.Equal("0", Setting(collapsed, "MaxHeight"));
        // Reading order: text, then quotes, then the nested replies.
        XElement replies = Assert.Single(root.Descendants(A + "StackPanel"), panel => (string?)panel.Attribute("IsVisible") == "{Binding RepliesExpanded}");
        XElement[] order = root.Descendants().Where(element => element == body || element == keys || element == replies).ToArray();
        Assert.Equal(new[] { body, keys, replies }, order);
    }

    [Theory]
    [InlineData("Dark", "#9CCB6A")]
    [InlineData("Light", "#386217")]
    public void GreentextMeetsTextContrastOnEveryPostSurface(string theme, string green)
    {
        XElement dictionary = Theme(theme);
        Assert.Equal(green, Resource(dictionary, "Workspace.ReaderGreentext").Value);
        foreach (string surface in new[] { "Workspace.Card", "Workspace.AccentSurface", "Workspace.Background" })
            Assert.True(Contrast(green, Resource(dictionary, surface).Value) >= 4.5, $"{theme}: greentext on {surface}");
    }

    [Fact]
    public void ReadingMarkLineIsARowOfItsOwnOutsideTheCardAndOnlyVisibleOnTheMarkedPost()
    {
        XElement panel = Assert.Single(Posts().Element(A + "ListBox.ItemTemplate")!.Element(A + "DataTemplate")!.Elements());
        Assert.Equal(A + "Panel", panel.Name);
        // The line is a sibling of the card's content, not a child of it: it never sits inside the card.
        XElement host = panel.Elements().First();
        Assert.Equal("ReaderUnreadLineHost", host.Name.LocalName);
        Assert.Equal(A + "StackPanel", panel.Elements().Last().Name);
        Assert.DoesNotContain(PostRoot().Descendants(), element => element.Name.LocalName == "ReaderUnreadLineHost");
        Assert.Equal("{Binding HasUnreadLine}", (string?)host.Attribute("IsVisible"));
        Assert.Equal("{Binding UnreadLabel}", (string?)host.Attribute("AutomationProperties.Name"));
        Assert.Equal("False", (string?)host.Attribute("IsHitTestVisible"));
        XElement line = Assert.Single(host.Elements());
        Assert.Equal(A + "Grid", line.Name);
        Assert.Equal("{Binding UnreadLabel}", (string?)line.Element(A + "TextBlock")!.Attribute("Text"));
        Assert.All(line.Elements(A + "Rectangle"), rule => Assert.Equal("{DynamicResource Workspace.Accent}", (string?)rule.Attribute("Fill")));
        Assert.Equal(2, line.Elements(A + "Rectangle").Count()); // A rule on both sides of the count.
        Assert.Null(line.Attribute("Margin")); // No negative margin: the host places the line, the card keeps its own padding.
        Assert.Equal("8,8,8,12", (string?)PostRoot().Attribute("Margin"));
    }

    [Fact]
    public void ReadingMarkLineGetsEightDipOfAirAboveAndBelowAndTheMarkedCardReservesTheRoom()
    {
        Assert.Equal(8, MeowChan.Desktop.ReaderUnreadLineHost.Air);
        // Gap between the card above and the marked card: air + line + air; two plain cards are already 8 dip apart.
        double room = ArchivePage.ReaderUnreadLineRoom;
        Assert.Equal(MeowChan.Desktop.ReaderUnreadLineHost.Air * 2 + MeowChan.Desktop.ReaderUnreadLineHost.LineHeight, 8 + room);
        Assert.Equal(new Avalonia.Thickness(8, 4 + room, 8, 4), ArchivePage.ReaderPostIndentMargin(0, 1000, room));
        Assert.Equal(new Avalonia.Thickness(8, 4, 8, 4), ArchivePage.ReaderPostIndentMargin(0, 1000));
        // The line ends 8 dip above the card's top edge, and a jump to the post scrolls to the line, not to the card.
        Assert.Equal(MeowChan.Desktop.ReaderUnreadLineHost.Air + MeowChan.Desktop.ReaderUnreadLineHost.LineHeight, MeowChan.Desktop.ReaderUnreadLineHost.Reach);
    }

    [Theory]
    [InlineData("Dark")]
    [InlineData("Light")]
    public void SpoilerAndCodeSurfacesKeepEveryInlineColourReadable(string theme)
    {
        XElement dictionary = Theme(theme);
        string spoiler = Resource(dictionary, "Workspace.ReaderSpoiler").Value, code = Resource(dictionary, "Workspace.ReaderCodeSurface").Value;
        // A shown spoiler keeps text, references (accent), greentext and the muted dead-reference colour readable;
        // a hidden one is the same colour twice, so it must still be an area against the card.
        foreach (string text in new[] { "Workspace.Text", "Workspace.Accent", "Workspace.ReaderGreentext" })
        {
            Assert.True(Contrast(Resource(dictionary, text).Value, spoiler) >= 4.5, $"{theme}: {text} on the spoiler");
            Assert.True(Contrast(Resource(dictionary, text).Value, code) >= 4.5, $"{theme}: {text} on code");
        }
        Assert.True(Contrast(Resource(dictionary, "Workspace.Muted").Value, code) >= 4.5, $"{theme}: muted on code");
        Assert.True(Contrast(spoiler, Resource(dictionary, "Workspace.Card").Value) >= 1.3, $"{theme}: the spoiler area against the card");
        Assert.True(Contrast(code, Resource(dictionary, "Workspace.Card").Value) >= 1.1, $"{theme}: the code area against the card");
    }

    [Fact]
    public void OpeningPostSitsOnThePageAndBecomesTheActiveCardWhenSelected()
    {
        Assert.Equal("OnReaderPostContainerPrepared", (string?)Posts().Attribute("ContainerPrepared"));
        foreach (string selector in new[] { "ListBoxItem.reader-op", "ListBoxItem.reader-op /template/ ContentPresenter" })
        {
            Assert.Equal("Transparent", Setting(PostStyle(selector), "Background"));
            Assert.Equal("Transparent", Setting(PostStyle(selector), "BorderBrush"));
        }
        string[] order = Posts().Element(A + "ListBox.Styles")!.Elements(A + "Style")
            .Select(style => (string?)style.Attribute("Selector") ?? "").ToArray();
        Assert.True(Array.IndexOf(order, "ListBoxItem:selected /template/ ContentPresenter")
            > Array.IndexOf(order, "ListBoxItem.reader-op:pressed /template/ ContentPresenter"));
    }

    // P8: the reader's local style blocks read sizes, type and spacing from the design tokens or sit on the space grid;
    // only the documented geometry stays literal (selection edge 3,1,1,1 / 3,2,2,2, the post-column cap 1120).
    [Fact]
    public void ReaderLocalStylesUseTokensAndTheSpaceGrid()
    {
        double[] grid = { 0, 2, 4, 8, 12, 16, 24, 32 };
        XElement page = Source("Desktop/Views/Archive/ArchivePage.axaml");
        XElement[] blocks = page.Descendants().Where(element => element.Name.LocalName.EndsWith(".Styles", StringComparison.Ordinal)).ToArray();
        Assert.NotEmpty(blocks);
        foreach (XElement setter in blocks.SelectMany(block => block.Descendants(A + "Setter")))
        {
            string property = (string?)setter.Attribute("Property") ?? "", value = (string?)setter.Attribute("Value") ?? "";
            if (property is "FontSize" or "Width" or "MinWidth" or "Height" or "MinHeight" && !value.StartsWith("0", StringComparison.Ordinal))
                Assert.True(value.StartsWith("{StaticResource Workspace.", StringComparison.Ordinal), $"{property}=\"{value}\" is a literal");
            if (property is "Margin" or "Padding" && !value.StartsWith("{", StringComparison.Ordinal))
                Assert.All(value.Split(','), part => Assert.Contains(double.Parse(part, System.Globalization.CultureInfo.InvariantCulture), grid));
        }
        foreach (XElement element in page.DescendantsAndSelf())
        {
            string name = (string?)element.Attribute(X + "Name") ?? "";
            if (!name.StartsWith("Reader", StringComparison.Ordinal) && name != "ArchivePosts" && name != "ArchiveReaderPane") continue;
            foreach (string spacing in new[] { "Spacing", "ItemSpacing", "LineSpacing", "RowSpacing", "ColumnSpacing" })
                if (double.TryParse((string?)element.Attribute(spacing), System.Globalization.CultureInfo.InvariantCulture, out double value))
                    Assert.Contains(value, grid);
        }
        Assert.DoesNotContain(page.DescendantsAndSelf().SelectMany(element => element.Attributes()), attribute =>
            attribute.Value.StartsWith('#') && attribute.Name.LocalName != "Name");
        Assert.Equal("{DynamicResource Workspace.Scrim}", (string?)Assert.Single(page.Descendants(A + "Border"), element =>
            (string?)element.Attribute(X + "Name") == "ReaderLinkDialog").Attribute("Background"));
        // Archive has several toolbars now; bind this contract to the reader toolbar instead of assuming it is the only one.
        XElement readerToolbar = Assert.Single(page.Descendants(Desktop + "ActionToolBar").Where(element =>
            (string?)element.Attribute(X + "Name") == "ReaderToolbar"));
        XElement readerToolbarStyle = Assert.Single(readerToolbar.Descendants(Desktop + "ActionToolBar.Styles").Elements(A + "Style"));
        Assert.Equal("{StaticResource Workspace.Size.Input}", Setting(readerToolbarStyle, "Height"));
    }

    private static XElement PostRoot()
    {
        XElement template = Posts().Element(A + "ListBox.ItemTemplate")!.Element(A + "DataTemplate")!;
        Assert.Equal("desktop:ArchivePostRow", (string?)template.Attribute(X + "DataType"));
        XElement root = Assert.Single(template.Elements()); // The card content sits beside the reading-line host in one panel.
        return root.Name == A + "Panel" ? Assert.Single(root.Elements(A + "StackPanel")) : root;
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
