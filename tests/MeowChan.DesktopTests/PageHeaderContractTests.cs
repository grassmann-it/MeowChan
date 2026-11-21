using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using MeowChan.Desktop.Controls;

namespace MeowChan.DesktopTests;

/// <summary>
/// Narrow-window sweep (Tom, 16.11.2026: "find and fix every place where headings and texts vanish into nowhere in small windows"). His example:
/// the Sources page at about 967 × 700, where the header's action row ("Show [Sources ▾] [+ Add plugin…] [Refresh status] [Open plugin folder]
/// [Review bundled repairs] [?]") took the whole width, so the title column of the "*,Auto" grid was 0 dip wide and the title block (MinWidth 200)
/// was centred on it: pushed out of the window to the left, the start of every line cut off. The shared <see cref="PageHeader"/> measures the
/// action row and stacks it under the title when the title would get less than its room. Layout runs without an Avalonia application (plain
/// borders with fixed widths stand for the texts, as in <see cref="HeaderBarContractTests"/>); the pages are checked in their XAML.
/// A generic headless guard over the real pages (every visible TextBlock inside its parent or trimming/wrapping) is not possible here: the
/// pages need the platform's text shaping and their services to lay out. The per-page captures at the shell minimum (640 and 760 dip, en and
/// de) run centrally instead.
/// </summary>
public sealed class PageHeaderContractTests
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";
    private const string ControlsNs = "clr-namespace:MeowChan.Desktop.Controls";

    private static Border Box(double width, double height = 32) => new() { Width = width, Height = height, HorizontalAlignment = HorizontalAlignment.Left };

    /// <summary>A title block: the title (180 dip) over a subtitle whose single line is <paramref name="subtitle"/> dip long.</summary>
    private static StackPanel Title(double subtitle = 600) => new() { Spacing = 4, Children = { Box(180, 28), Box(subtitle, 20) } };

    /// <summary>An action row like the Sources one: <paramref name="count"/> buttons of 130 dip, 8 apart, wrapping.</summary>
    private static WrapPanel Actions(int count = 6)
    {
        WrapPanel row = new() { ItemSpacing = 8, LineSpacing = 8, VerticalAlignment = VerticalAlignment.Top };
        for (int index = 0; index < count; index++) row.Children.Add(Box(130));
        PageHeader.SetRole(row, PageHeaderRole.Actions);
        return row;
    }

    private static PageHeader Header(Control title, params Control[] rest)
    {
        PageHeader header = new();
        header.Children.Add(title);
        foreach (Control control in rest) header.Children.Add(control);
        return header;
    }

    private static void Layout(Control control, double width)
    {
        control.Measure(new Size(width, double.PositiveInfinity));
        control.Arrange(new Rect(0, 0, width, control.DesiredSize.Height));
    }

    [Theory]
    // available, actions, title natural, expected: the title keeps 280 dip beside the actions or the header stacks.
    [InlineData(1356, 820, 600, false)]  // a wide window: 524 dip for the title
    [InlineData(943, 820, 600, true)]    // Tom's 967 dip window: 111 dip would be left, the old grid gave the title nothing at all
    [InlineData(1112, 820, 600, false)]  // exactly 280 left
    [InlineData(1111, 820, 600, true)]   // one dip less
    [InlineData(600, 300, 120, false)]   // a short title alone needs only its own width
    [InlineData(600, 0, 600, false)]     // no visible action: nothing to stack
    public void TheDecisionKeepsTheTitleItsRoomOrStacks(double available, double actions, double title, bool stacked) =>
        Assert.Equal(stacked, PageHeader.ShouldStack(available, actions, title, PageHeader.DefaultTitleMinWidth, PageHeader.DefaultSpacing));

    [Fact]
    public void AnUnboundedRowNeverStacksAndAFixedBreakpointStacksBelowItsWidth()
    {
        Assert.False(PageHeader.ShouldStack(double.PositiveInfinity, 2000, 600, 280, 12));
        // The Plugins page keeps its documented 900 dip page breakpoint (876 dip of header) on top of the fit.
        Assert.True(PageHeader.ShouldStack(875, 200, 600, 280, 12, stackBelowWidth: 876));
        Assert.False(PageHeader.ShouldStack(876, 200, 600, 280, 12, stackBelowWidth: 876));
    }

    [Fact]
    public void AWideHeaderPutsTheActionsTopRightAndGivesTheTitleTheRest()
    {
        StackPanel title = Title();
        WrapPanel actions = Actions();
        Border count = Box(60, 16);
        PageHeader.SetRole(count, PageHeaderRole.Aside);
        PageHeader header = Header(title, actions, count);
        Layout(header, 1356);
        Assert.False(header.IsStacked);
        Assert.Equal(0, title.Bounds.Left, 1);
        Assert.Equal(1356 - 820 - PageHeader.DefaultSpacing, title.Bounds.Width, 1);
        Assert.Equal(1356, actions.Bounds.Right, 1);
        Assert.Equal(32, actions.Bounds.Height, 1); // one line
        Assert.Equal(0, Grid.GetRow(actions));
        // The count stands right-aligned under the actions.
        Assert.Equal(1356, count.Bounds.Right, 1);
        Assert.True(count.Bounds.Top >= actions.Bounds.Bottom - 0.5);
    }

    [Fact]
    public void TomsSourcesHeaderAt967DipStacksAndTheTitleStartsAtTheLeftEdgeOnTheFullWidth()
    {
        // The regression: the title block had MinWidth 200 in a 0 dip column and was centred, i.e. its left edge stood at about -100 dip.
        StackPanel title = Title();
        title.MinWidth = 200;
        WrapPanel actions = Actions(8);
        Border count = Box(60, 16);
        PageHeader.SetRole(count, PageHeaderRole.Aside);
        PageHeader header = Header(title, actions, count);
        Layout(header, 943);
        Assert.True(header.IsStacked);
        Assert.Equal(0, title.Bounds.Left, 1);
        Assert.Equal(943, title.Bounds.Width, 1);
        // Title, then the count, then the actions, all at the start edge; the actions wrap inside the row instead of running out of it.
        Assert.Equal(2, Grid.GetRow(actions));
        Assert.Equal(1, Grid.GetRow(count));
        Assert.Equal(0, count.Bounds.Left, 1);
        Assert.True(count.Bounds.Top >= title.Bounds.Bottom - 0.5);
        Assert.Equal(0, actions.Bounds.Left, 1);
        Assert.True(actions.Bounds.Top >= count.Bounds.Bottom + PageHeader.StackedActionsGap - 0.5);
        Assert.True(actions.Bounds.Right <= 943 + 0.5, $"actions {actions.Bounds} run out of the 943 dip row");
        Assert.True(actions.Bounds.Height > 32, "eight 130 dip buttons should take a second line at 943 dip");
    }

    [Theory]
    [InlineData(616)] // the compact shell minimum (640) minus the page inset
    [InlineData(736)] // the normal shell minimum (760) minus the page inset
    [InlineData(943)]
    [InlineData(1356)]
    public void AtEveryWidthEveryPartStaysInsideTheRow(double width)
    {
        StackPanel title = Title();
        WrapPanel actions = Actions();
        PageHeader header = Header(title, actions);
        Layout(header, width);
        foreach (Control part in new Control[] { title, actions })
        {
            Assert.True(part.Bounds.Left >= -0.5, $"{part.GetType().Name} {part.Bounds} starts left of the row at {width} dip");
            Assert.True(part.Bounds.Right <= width + 0.5, $"{part.GetType().Name} {part.Bounds} ends right of the row at {width} dip");
        }
        Assert.True(title.Bounds.Width >= Math.Min(PageHeader.DefaultTitleMinWidth, width) - 0.5);
    }

    [Fact]
    public void WideningAgainPutsTheActionsBackBesideTheTitle()
    {
        StackPanel title = Title();
        WrapPanel actions = Actions();
        PageHeader header = Header(title, actions);
        Layout(header, 700);
        Assert.True(header.IsStacked);
        Assert.Equal(new Thickness(0, PageHeader.StackedActionsGap, 0, 0), actions.Margin);
        Layout(header, 1400);
        Assert.False(header.IsStacked);
        Assert.Equal(0, Grid.GetRow(actions));
        Assert.Equal(1, Grid.GetColumn(actions));
        Assert.Equal(default(Thickness), actions.Margin);
        Assert.Equal(HorizontalAlignment.Right, actions.HorizontalAlignment);
    }

    [Fact]
    public void AHiddenActionRowTakesNoRoomAndTheTitleGetsTheWholeRow()
    {
        StackPanel title = Title();
        WrapPanel actions = Actions();
        actions.IsVisible = false;
        PageHeader header = Header(title, actions);
        Layout(header, 616);
        Assert.False(header.IsStacked);
        Assert.Equal(0d, header.ColumnSpacing); // no gap before the empty end column
        Assert.Equal(616, title.Bounds.Width, 1);
    }

    [Fact]
    public void AHeaderBarContentThatCannotShrinkKeepsItsStartEdgeInsteadOfBeingCentredUnderTheBackSlot()
    {
        Border back = Box(100), content = new() { Height = 32, MinWidth = 300 }, more = Box(40);
        HeaderBar bar = HeaderBar.Create(back, content, more);
        bar.Measure(new Size(360, 40));
        bar.Arrange(new Rect(0, 0, 360, 40));
        // 360 - 100 - 24 - 40 - 16 = 180 dip of room for a 300 dip minimum: the content starts after the back gap and overflows to the end.
        Assert.Equal(back.Bounds.Right + HeaderBar.BackGap, content.Bounds.Left, 1);
        Assert.Equal(300, content.Bounds.Width, 1);
        Assert.Equal(300, HeaderBar.ContentFloor(content), 1);
    }

    [Theory]
    [InlineData("Desktop/Views/Plugins/PluginsPage.axaml", "PluginsPageHeader")]
    [InlineData("Desktop/Views/Status/StatusPage.axaml", "StatusPageHeader")]
    [InlineData("Desktop/Views/Rules/RulesPage.axaml", "RulesHeaderRow")]
    [InlineData("Desktop/Views/Archive/ArchivePage.axaml", "ArchivePageHeader")]
    public void ThePageHeadsWithActionsUseTheSharedPageHeaderAndLeavePlacementToIt(string file, string name)
    {
        XDocument markup = XDocument.Load(Root(file));
        XElement header = Assert.Single(markup.Descendants(), e => (string?)e.Attribute(X + "Name") == name);
        Assert.Equal(XName.Get("PageHeader", ControlsNs), header.Name);
        XElement[] children = header.Elements().ToArray();
        // The title block first (tab and reading order), then the actions; the header owns row, column, span and the actions' alignment.
        Assert.Contains("workspace-page-header", ((string?)children[0].Attribute("Classes") ?? "").Split(' '));
        Assert.Null(children[0].Attribute("MinWidth")); // a minimum width centred the block out of the page
        Assert.Contains(children.Skip(1), child => child.Attributes().Any(a => a.Name.LocalName == "PageHeader.Role" && a.Value == "Actions"));
        foreach (XElement child in children)
            Assert.DoesNotContain(child.Attributes(), a => a.Name.LocalName is "Grid.Row" or "Grid.Column" or "Grid.RowSpan" or "Grid.ColumnSpan");
        Assert.Null(header.Attribute("ColumnDefinitions"));
    }

    // The Archive head was the same "*,Auto" grid as Sources: its toolbar (Import, Export, Clean up, Refresh) in the Auto column took the
    // whole width in a narrow window and pushed the title and description out. The toolbar keeps its name, work view ids and order (the
    // work view and entry point tests read them); the header owns its alignment and where it stands.
    [Fact]
    public void TheArchiveHeaderToolbarIsTheHeadersActionsAndKeepsItsIdentity()
    {
        XElement header = Assert.Single(XDocument.Load(Root("Desktop/Views/Archive/ArchivePage.axaml")).Descendants(),
            e => (string?)e.Attribute(X + "Name") == "ArchivePageHeader");
        XElement bar = Assert.Single(header.Elements(), e => (string?)e.Attribute(X + "Name") == "ArchiveHeaderActions");
        Assert.Equal("ActionToolBar", bar.Name.LocalName);
        Assert.Contains(bar.Attributes(), a => a.Name.LocalName == "PageHeader.Role" && a.Value == "Actions");
        Assert.Null(bar.Attribute("HorizontalAlignment")); // the header sets Right beside the title and Left under it
        Assert.Equal(["ArchiveTransferPreview", "ArchiveTransferExport", "ArchiveCleanupOpen", "RefreshArchiveButton"],
            bar.Elements().Select(e => (string?)e.Attribute(X + "Name") ?? "").ToArray());
        string code = File.ReadAllText(Root("Desktop/Views/Archive/ArchivePage.axaml.cs"));
        Assert.Contains("ArchivePageHeader.IsVisible = !(_showReader && narrow);", code);
    }

    // The Media and Tagging pages build their head in code: the title and subtitle are one vertical workspace-page-header stack over the
    // page's own rows, never a "*,Auto" grid with actions beside the title, so there is nothing a long action row could push out.
    [Theory]
    [InlineData("Desktop/Views/Media/MediaPage.cs", "StackPanel header = new() { Margin = new Thickness(12, PageHeader.PageTopOffset, 12, 8), Classes = { \"workspace-page-header\" } };")]
    [InlineData("Desktop/Views/Tagging/TaggingPage.cs", "StackPanel heading = new() { Name = \"TaggingHeading\", Classes = { \"workspace-page-header\" },\n            [!StackPanel.SpacingProperty] = new DynamicResourceExtension(\"Workspace.Space.8\"), Children = { _title } };")]
    public void TheMediaAndTaggingTitlesStandInAVerticalHeaderStackWithoutActionsBesideThem(string file, string stack)
    {
        string code = File.ReadAllText(Root(file)).Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.Contains(stack, code);
        Assert.DoesNotContain("PageHeader.SetRole", code);
    }

    // The bug class itself: a page title in a Grid whose column definitions are "*,Auto" (the Auto column is measured at infinity). A page
    // head with actions is a PageHeader; any new page that writes the grid by hand fails here.
    [Fact]
    public void NoPageTitleSitsInAStarAutoGrid()
    {
        string root = Root("Desktop");
        foreach (string file in Directory.EnumerateFiles(root, "*.axaml", SearchOption.AllDirectories))
        {
            XDocument markup = XDocument.Load(file);
            foreach (XElement grid in markup.Descendants().Where(e => e.Name.LocalName == "Grid"
                && ((string?)e.Attribute("ColumnDefinitions"))?.Replace(" ", "").StartsWith("*,Auto", StringComparison.Ordinal) == true))
                Assert.DoesNotContain(grid.Elements().SelectMany(child => child.DescendantsAndSelf()).Take(40), e =>
                    e.Name.LocalName == "TextBlock" && ((string?)e.Attribute("Classes") ?? "").Split(' ').Contains("page-title"));
        }
    }

    [Fact]
    public void ThePluginsHeaderNoLongerMovesItsPartsFromCode()
    {
        string code = File.ReadAllText(Root("Desktop/Views/Plugins/PluginsPage.axaml.cs"));
        Assert.DoesNotContain("stackedHeader", code);
        Assert.DoesNotContain("Grid.SetRow(headerActions", code);
        Assert.Contains("StackBelowWidth=\"876\"", File.ReadAllText(Root("Desktop/Views/Plugins/PluginsPage.axaml")));
    }

    // A horizontal StackPanel measures its children at infinite width, so a wrapping text inside it never wraps and is cut at the edge of a
    // narrow window (the Plugins status line, the Rules and Settings footer status). In the pages of this sweep such a line sits in a DockPanel.
    [Theory]
    [InlineData("Desktop/Views/Plugins/PluginsPage.axaml")]
    [InlineData("Desktop/Views/Sources/SourcesPage.axaml")]
    [InlineData("Desktop/Views/Rules/RulesPage.axaml")]
    [InlineData("Desktop/Views/Status/StatusPage.axaml")]
    [InlineData("Desktop/Views/Statistics/StatisticsPage.axaml")]
    [InlineData("Desktop/Views/Diagnostics/DiagnosticsPage.axaml")]
    [InlineData("Desktop/Views/Settings/SettingsEditor.axaml")]
    public void NoWrappingTextSitsInAHorizontalStack(string file)
    {
        XDocument markup = XDocument.Load(Root(file));
        foreach (XElement stack in markup.Descendants().Where(e => e.Name.LocalName == "StackPanel" && (string?)e.Attribute("Orientation") == "Horizontal"))
            Assert.DoesNotContain(stack.Elements(), child => child.Name.LocalName is "TextBlock" or "SelectableTextBlock"
                && (string?)child.Attribute("TextWrapping") == "Wrap");
        if (file.EndsWith("PluginsPage.axaml", StringComparison.Ordinal))
            Assert.Equal("DockPanel", Assert.Single(markup.Descendants(), e => (string?)e.Attribute(X + "Name") == "PluginStatusRow").Name.LocalName);
    }

    [Fact]
    public void APageTitleWrapsInsteadOfBeingCutAtTheEdge()
    {
        XNamespace a = "https://github.com/avaloniaui";
        XElement style = Assert.Single(XDocument.Load(Root("Desktop/WorkspaceStyles.axaml")).Root!.Elements(a + "Style"),
            s => (string?)s.Attribute("Selector") == "TextBlock.page-title");
        Assert.Contains(style.Elements(a + "Setter"), setter => (string?)setter.Attribute("Property") == "TextWrapping" && (string?)setter.Attribute("Value") == "Wrap");
    }

    private static string Root(string relative, [CallerFilePath] string sourcePath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "../..", relative));
}
