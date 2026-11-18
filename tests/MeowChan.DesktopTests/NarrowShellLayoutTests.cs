using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Layout;
using MeowChan.Desktop;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Stats;

namespace MeowChan.DesktopTests;

/// <summary>
/// Narrow-window sweep, shell group (Tom, 16.11.2026: "headings and texts vanish into nowhere in small windows"): the title bar, the status
/// line, the footer, the Downloads table and the dialog footers at the window minimums (760 x 500 Simple/Advanced, 640 x 480 Barebones, the
/// dialog steps S 360, M 420, L 480, XL 640 dip wide) in the longest of the twelve languages. Layout runs on stand-in controls without an
/// Avalonia application (fixed sizes stand in for measured texts); the language checks use the conservative per-glyph model of
/// <see cref="DialogFooterBudgetTests"/>. A generic "every TextBlock inside its parent" guard over the real pages and dialogs is not possible
/// headless (they need the session, the services and the Skia font stack); the rendered check is the narrow-window GUI capture.
/// </summary>
[Collection("Localization")]
public sealed class NarrowShellLayoutTests : IDisposable
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";
    private readonly string _language = Texts.Language;

    public NarrowShellLayoutTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage(_language); }

    /// <summary>
    /// A stand-in for a text: it asks for its natural width, accepts less when the panel offers less (like a trimming text block) and fills
    /// the rectangle it is arranged in, so its bounds are exactly what the panel handed out. A fixed <c>Width</c> would be centred instead.
    /// </summary>
    private sealed class Probe(double natural, double height = 20) : Control
    {
        protected override Size MeasureOverride(Size availableSize) =>
            new(Math.Min(natural, availableSize.Width), Math.Min(height, availableSize.Height));
    }

    private static Probe Box(double width, double height = 20) => new(width, height);

    private static void Layout(Control control, double width, double height = 40)
    {
        control.Measure(new Size(width, height));
        control.Arrange(new Rect(0, 0, width, height));
    }

    // ---- ShrinkRowPanel: the pure allocation ----------------------------------------------------------------------

    [Fact]
    public void ARowThatFitsKeepsEveryNaturalWidth()
    {
        Assert.Equal([120, 40, 300], ShrinkRowPanel.Allocate([120, 40, 300], 700, 12));
        Assert.Equal([120, 40, 300], ShrinkRowPanel.Allocate([120, 40, 300], 120 + 40 + 300 + 24, 12));
        Assert.Equal([500, 900], ShrinkRowPanel.Allocate([500, 900], double.PositiveInfinity, 12));
        Assert.Empty(ShrinkRowPanel.Allocate([], 100, 12));
    }

    [Fact]
    public void TheWidestItemGivesWayFirstAndTheShortOnesStayWhole()
    {
        // The status line at 728 dip: "Paused" (48), the update notice (150) and a French counter line of 690 dip.
        double[] widths = ShrinkRowPanel.Allocate([48, 150, 690], 728, 12);
        Assert.Equal(48, widths[0]);
        Assert.Equal(150, widths[1]);
        Assert.Equal(728 - 24 - 48 - 150, widths[2], 6);
    }

    [Fact]
    public void TwoLongItemsShareTheRestEquallyAfterTheShortOne()
    {
        double[] widths = ShrinkRowPanel.Allocate([40, 600, 400], 540, 10);
        Assert.Equal(40, widths[0]);
        Assert.Equal(widths[1], widths[2], 6);
        Assert.Equal(540 - 20, widths.Sum(), 6);
    }

    [Fact]
    public void NoItemShrinksBelowTheFloor()
    {
        double[] widths = ShrinkRowPanel.Allocate([200, 200, 200], 30, 8);
        Assert.All(widths, width => Assert.Equal(ShrinkRowPanel.MinimumShrunkWidth, width));
        // An item narrower than the floor keeps its own width.
        Assert.Equal(10, ShrinkRowPanel.Allocate([10, 400], 50, 8)[0]);
    }

    [Fact]
    public void UnusableWidthsCountAsZero()
    {
        Assert.Equal([0, 0, 50], ShrinkRowPanel.Allocate([double.NaN, -4, 50], 100, 0));
    }

    // ---- ShrinkRowPanel: the arranged row -------------------------------------------------------------------------

    [Fact]
    public void WithFillFirstTheFirstItemTakesTheRestAndTheOthersStandAtTheRightEdge()
    {
        Probe state = Box(60), counts = Box(200);
        ShrinkRowPanel row = new() { FillFirst = true, Spacing = 12, Children = { state, counts } };
        Layout(row, 728);
        Assert.Equal(0, state.Bounds.X);
        Assert.Equal(728 - 12 - 200, state.Bounds.Width, 6);
        Assert.Equal(728, counts.Bounds.Right, 6);
        Assert.Empty(row.ShrunkItems);
    }

    [Fact]
    public void AnOverlongRowStaysInsideThePanelAndShortensItsWidestItem()
    {
        // The former Grid("*,Auto,Auto") let these run 400 dip past a 728 dip window.
        Probe state = Box(80), notice = Box(220), warning = Box(560), counts = Box(690);
        ShrinkRowPanel row = new() { FillFirst = true, Spacing = 12, Children = { state, notice, warning, counts } };
        Layout(row, 728);
        foreach (Probe item in new[] { state, notice, warning, counts })
        {
            Assert.True(item.Bounds.X >= 0 && item.Bounds.Right <= 728.01, $"{item.Bounds} lies outside the 728 dip row");
            Assert.True(item.Bounds.Width > 0, "an item was pushed to zero width");
        }
        Assert.Equal(80, state.Bounds.Width, 6); // the short state stays whole
        Assert.Contains(counts, row.ShrunkItems);
        Assert.DoesNotContain(state, row.ShrunkItems);
    }

    [Fact]
    public void HiddenItemsTakeNeitherRoomNorSpacing()
    {
        Probe state = Box(60), hidden = Box(300), counts = Box(100);
        hidden.IsVisible = false;
        ShrinkRowPanel row = new() { FillFirst = true, Spacing = 12, Children = { state, hidden, counts } };
        Layout(row, 400);
        Assert.Equal(400 - 12 - 100, state.Bounds.Width, 6);
        Assert.Equal(400, counts.Bounds.Right, 6);
    }

    [Fact]
    public void WithoutFillFirstTheItemsStayPackedAtTheLeft()
    {
        // The Source / board cell: the host shortens, the board label stays beside it.
        Probe host = Box(160), board = Box(28);
        ShrinkRowPanel cell = new() { Spacing = 8, Children = { host, board } };
        Layout(cell, 106);
        Assert.Equal(28, board.Bounds.Width, 6);
        Assert.Equal(106, board.Bounds.Right, 6);
        Assert.Equal(106 - 8 - 28, host.Bounds.Width, 6);
        Layout(cell, 400);
        Assert.Equal(160 + 8, board.Bounds.X, 6);
    }

    // ---- DialogFooterRow: the status never shrinks to a sliver ----------------------------------------------------

    [Theory]
    [InlineData(448, 200, 120, false)] // room 248 for a 120 dip status: beside
    [InlineData(448, 400, 30, false)]  // a short status ("3 of 5") needs only its own width
    [InlineData(448, 400, 300, true)]  // 48 dip left for a long status: above the actions
    [InlineData(448, 300, 300, true)]  // 148 dip left: less than the 160 dip minimum
    [InlineData(448, 288, 300, false)] // exactly the minimum: beside
    [InlineData(448, 440, 0, false)]   // no status text: nothing to place
    public void TheStatusGoesAboveTheActionsOnlyWhenItWouldBeASliver(double available, double actions, double status, bool stacked) =>
        Assert.Equal(stacked, DialogFooterRow.Stacks(available, actions, status));

    [Fact]
    public void AStackedStatusSpansTheRowAboveTheRightAlignedActions()
    {
        Probe actions = Box(400, 30), status = Box(300, 18);
        actions.HorizontalAlignment = HorizontalAlignment.Right;
        DialogFooterRow row = new() { LastChildFill = true };
        DockPanel.SetDock(actions, Dock.Right);
        row.Children.Add(actions);
        row.Children.Add(status);
        row.Measure(new Size(448, double.PositiveInfinity));
        row.Arrange(new Rect(0, 0, 448, row.DesiredSize.Height));
        Assert.True(row.Stacked);
        Assert.Equal(18 + DialogFooterRow.StackedGap + 30, row.DesiredSize.Height, 6);
        Assert.True(status.Bounds.Bottom <= actions.Bounds.Top, "the status overlaps the actions");
        Assert.Equal(448, actions.Bounds.Right, 6);
    }

    [Fact]
    public void AStatusThatFitsStaysLeftOfTheActions()
    {
        Probe actions = Box(200, 30), status = Box(120, 18);
        DialogFooterRow row = new() { LastChildFill = true };
        DockPanel.SetDock(actions, Dock.Right);
        row.Children.Add(actions);
        row.Children.Add(status);
        row.Measure(new Size(448, 60));
        row.Arrange(new Rect(0, 0, 448, 60));
        Assert.False(row.Stacked);
        Assert.True(status.Bounds.Right <= actions.Bounds.Left + 0.01);
    }

    [Fact]
    public void TheDialogFooterUsesTheStackingRow()
    {
        Border footer = DialogFooter.Build(new Button(), status: new TextBlock { Text = "3 of 5" });
        Assert.IsType<DialogFooterRow>(footer.Child);
    }

    // ---- the header display -------------------------------------------------------------------------------------

    private static IoStatsDisplay.HeaderLayout TextRows => IoStatsDisplay.ResolveHeaderLayout(null, 900);
    private static IoStatsDisplay.HeaderLayout GraphRows => IoStatsDisplay.ResolveHeaderLayout(null, 1100);

    [Fact]
    public void TheDisplayWidthFollowsTheHeaderXaml()
    {
        Assert.Equal(IoStatsDisplay.HeaderArrangement.TextRows, TextRows.Arrangement);
        // 12 padding + "Down" 30 + 4 + rate 56 + 4 + 8 + "Write" 32 + 4 + rate 56.
        Assert.Equal(12 + 30 + 4 + 56 + 4 + 8 + 32 + 4 + 56, HeaderMonitorFit.Width(TextRows, 30, 14, 28, 32, 40), 6);
        // The graphs add their column and gap; a wide rate widens both rate columns.
        Assert.Equal(HeaderMonitorFit.Width(TextRows, 30, 14, 28, 32, 70) + HeaderMonitorFit.RowGraphWidth + HeaderMonitorFit.TextGap,
            HeaderMonitorFit.Width(GraphRows, 30, 14, 28, 32, 70), 6);
        Assert.Equal(2 * 14, HeaderMonitorFit.Width(TextRows, 30, 14, 28, 32, 70) - HeaderMonitorFit.Width(TextRows, 30, 14, 28, 32, 56), 6);
        Assert.Equal(0, HeaderMonitorFit.Width(IoStatsDisplay.ResolveHeaderLayout(IoStatsDisplay.HeaderGraphOff, 900), 30, 14, 28, 32, 56));
    }

    [Fact]
    public void BlocksStackTheirDirectionsAndKeepTheBlockGap()
    {
        IoStatsDisplay.HeaderLayout blocks = IoStatsDisplay.ResolveHeaderLayout(null, 1400);
        Assert.True(blocks.TwoLineLabels);
        double net = HeaderMonitorFit.BlockGraphWidth + HeaderMonitorFit.TextGap + 30 + 4 + 56;
        double disk = HeaderMonitorFit.BlockGraphWidth + HeaderMonitorFit.TextGap + 32 + 4 + 56;
        Assert.Equal(12 + net + HeaderMonitorFit.BlockGap + disk, HeaderMonitorFit.Width(blocks, 30, 14, 28, 32, 50), 6);
        IoStatsDisplay.HeaderLayout network = IoStatsDisplay.ResolveHeaderLayout(IoStatsDisplay.HeaderGraphNetwork, 900);
        Assert.Equal(12 + 30 + 4 + 56, HeaderMonitorFit.Width(network, 30, 14, 28, 32, 50), 6); // one block, no gap, no graph
    }

    [Fact]
    public void WideMetricTitlesCanWidenOnlyTheirOwnBlock()
    {
        IoStatsDisplay.HeaderLayout blocks = IoStatsDisplay.ResolveHeaderLayout(null, 1400);
        double baseline = HeaderMonitorFit.Width(blocks, 30, 14, 28, 32, 50);
        double titled = HeaderMonitorFit.Width(blocks, 30, 14, 28, 32, 50, networkTitle: 180, diskTitle: 0);

        Assert.Equal(180, titled - (12 + HeaderMonitorFit.BlockGap + HeaderMonitorFit.BlockGraphWidth + HeaderMonitorFit.TextGap
            + HeaderMonitorFit.BlockGraphWidth + HeaderMonitorFit.TextGap + 32 + 4 + 56), 6);
        Assert.True(titled > baseline);
    }

    [Fact]
    public void UnmeasuredPartsNeverHideTheDisplay()
    {
        Assert.True(HeaderMonitorFit.Fits(0, 300, 200, 8));
        Assert.True(HeaderMonitorFit.Fits(double.NaN, 300, 200, 8));
        Assert.True(HeaderMonitorFit.Fits(600, 0, 200, 8));
        Assert.True(HeaderMonitorFit.Fits(600, 300, 0, 8));
        Assert.True(HeaderMonitorFit.Fits(600, 300, 291, 8));
        Assert.False(HeaderMonitorFit.Fits(600, 300, 292, 8));
    }

    [Fact]
    public void TheReservedRateIsTheWidestRateText()
    {
        Assert.Equal("9.99 MiB/s", HeaderMonitorFit.WidestRateText());
        Assert.True(DialogFooterBudgetTests.Em(HeaderMonitorFit.WidestRateText()) >= DialogFooterBudgetTests.Em(ByteUnits.Rate(512 * 1024)));
    }

    // The header XAML (WorkspaceHeader, Workspace.Size.HeaderIcon, Workspace.Size.Caption) with the per-glyph model: brand, three header icons, the
    // menu and the three caption buttons; the words in the caption step (11 dip), the brand in 19 dip semibold, the version in 12 dip.
    private static double HeaderRoom(double window) => window - 3 * 44 - 2 * 16;
    private static readonly double FixedParts = 34 + 8 + DialogFooterBudgetTests.Em("MeowChan") * 19 * 1.06 + 8 + DialogFooterBudgetTests.Em("v1.1.0") * 12
        + 32 + (3 * 30 + 2 * 8) + 3 * 12;

    private static double Display(IoStatsDisplay.HeaderLayout layout) => HeaderMonitorFit.Width(layout,
        DialogFooterBudgetTests.Em(Texts.T("stats.header.down")) * 11, DialogFooterBudgetTests.Em(Texts.T("stats.header.up")) * 11,
        DialogFooterBudgetTests.Em(Texts.T("stats.header.read")) * 11, DialogFooterBudgetTests.Em(Texts.T("stats.header.write")) * 11,
        DialogFooterBudgetTests.Em(HeaderMonitorFit.WidestRateText()) * 11);

    [Fact]
    public void EnglishKeepsTheDisplayAtTheSimpleMinimumWidth()
    {
        Assert.True(HeaderMonitorFit.Fits(HeaderRoom(760), FixedParts, Display(TextRows), 8),
            $"the English display ({Display(TextRows):0} dip) no longer fits at 760 dip");
    }

    [Fact]
    public void EveryLanguageShowsTheDisplayAgainBeforeTheGraphBreakpointAndKeepsTheGraphsThere()
    {
        foreach (string code in Texts.Languages.Select(language => language.Code))
        {
            Texts.SetLanguage(code);
            Assert.True(HeaderMonitorFit.Fits(HeaderRoom(IoStatsDisplay.HeaderGraphsMinimumWidth - 1), FixedParts, Display(TextRows), 8),
                $"{code}: the text rows ({Display(TextRows):0} dip) do not fit below the graph breakpoint");
            Assert.True(HeaderMonitorFit.Fits(HeaderRoom(IoStatsDisplay.HeaderGraphsMinimumWidth), FixedParts, Display(GraphRows), 8),
                $"{code}: the graph rows ({Display(GraphRows):0} dip) do not fit at the graph breakpoint");
        }
    }

    [Fact]
    public void ALongerLanguageGivesTheDisplayUpAtAWiderWindow()
    {
        double english = Display(TextRows);
        Texts.SetLanguage("cs");
        double czech = Display(TextRows);
        Assert.True(czech > english, "Stahování / Odesílání are wider than Down / Up");
        // A window between the two thresholds: the room is the fixed parts, the gap and the mean of the two displays (plus the tolerance).
        double between = FixedParts + 8 + (english + czech) / 2 + HeaderTaglineFit.Tolerance + 2 * 16 + 3 * 44;
        Assert.False(HeaderMonitorFit.Fits(HeaderRoom(between), FixedParts, czech, 8));
        Assert.True(HeaderMonitorFit.Fits(HeaderRoom(between), FixedParts, english, 8));
    }

    [Fact]
    public void TheWindowCombinesTheBreakpointWithTheMeasuredFit()
    {
        string transfer = Source("Desktop/Shell/MainWindow.Transfer.cs");
        Assert.Contains("&& HeaderMonitorFitsMeasured();", transfer);
        Assert.Contains("ApplyHeaderMonitorVisibility(); // the words of another language", transfer);
        XDocument main = XDocument.Parse(Source("Desktop/Shell/MainWindow.axaml"));
        XElement header = Named(main, "WorkspaceHeader");
        Assert.Equal("True", (string?)header.Attribute("ClipToBounds")); // nothing paints over the window controls
        foreach (string part in new[] { "WorkspaceBrand", "WorkspaceHeaderTools", "WorkspaceMenuBar" })
            Assert.Same(header, Named(main, part).Parent);
    }

    // ---- the shell rows in the XAML -------------------------------------------------------------------------------

    [Fact]
    public void TheStatusLineIsOneShrinkingRowWithTheStateFirst()
    {
        XDocument main = XDocument.Parse(Source("Desktop/Shell/MainWindow.axaml"));
        XElement row = Named(main, "WorkspaceStatusRow");
        Assert.Equal("ShrinkRowPanel", row.Name.LocalName);
        Assert.Equal("True", (string?)row.Attribute("FillFirst"));
        Assert.Equal(new[] { "WorkspaceStatus", "WorkspaceStoreWarningButton", "WorkspaceCounts" },
            row.Elements().Select(element => (string?)element.Attribute(X + "Name")).ToArray());
        foreach (string text in new[] { "WorkspaceStatus", "WorkspaceCounts", "WorkspaceStoreWarning" })
            Assert.Equal("CharacterEllipsis", (string?)Named(main, text).Attribute("TextTrimming"));
        Assert.DoesNotContain(row.Descendants(), element => element.Attribute("Grid.Column") != null);
        string updates = Source("Desktop/Shell/MainWindow.Updates.cs");
        Assert.Contains("status.Children.Insert(Math.Min(1, status.Children.Count), _updateNotice);", updates);
        Assert.DoesNotContain("ColumnDefinitions.Insert", updates);
    }

    [Fact]
    public void TheSourceBoardCellShortensTheHostAndTheHeadsEndInAnEllipsis()
    {
        string main = Source("Desktop/Shell/MainWindow.axaml");
        Assert.Contains("<TextBlock Text=\"{Binding HostName}\" VerticalAlignment=\"Center\" TextTrimming=\"CharacterEllipsis\" ToolTip.Tip=\"{Binding HostName}\" />", main);
        Assert.DoesNotContain("<Grid ColumnDefinitions=\"Auto,*\" ColumnSpacing=\"{StaticResource Workspace.Space.8}\" VerticalAlignment=\"Center\">", main);
        Assert.Contains("<Style Selector=\"DataGridColumnHeader TextBlock\"><Setter Property=\"TextTrimming\" Value=\"CharacterEllipsis\" /></Style>",
            Source("Desktop/WorkspaceStyles.axaml"));
    }

    [Fact]
    public void TheFixedWidthViewSelectorTrimsItsName()
    {
        ComboBox modes = new();
        MainWindow.RefreshWorkspaceModeSelectionPresentation(modes);
        ContentPresenter box = new() { Content = new ComboBoxItem { Tag = "Advanced", Content = "Advanced" }, ContentTemplate = modes.SelectionBoxItemTemplate };
        box.UpdateChild();
        Assert.Equal(Avalonia.Media.TextTrimming.CharacterEllipsis, Assert.IsType<TextBlock>(box.Child).TextTrimming);
    }

    [Fact]
    public void DialogTitlesAndListRowsCannotRunPastTheirWindow()
    {
        // The caption title carries its full text while it is shortened; the reading history rows wrap inside the list.
        Assert.Contains("_title = new MeowChan.Desktop.Controls.TrimTipTextBlock", Source("Desktop/WindowChromeBar.cs"));
        string history = Source("Desktop/Shell/ReadingHistoryWindow.cs");
        Assert.Contains("Grid row = new() { ColumnDefinitions = new ColumnDefinitions(\"Auto,*\")", history);
        Assert.DoesNotContain("Orientation = Orientation.Horizontal", history);
        string shortcuts = Source("Desktop/Views/Shortcuts/ShortcutBindingsEditor.cs");
        Assert.Contains("var title = new TrimTipTextBlock { VerticalAlignment = VerticalAlignment.Center };", shortcuts);
        Assert.DoesNotContain("new ColumnDefinitions(\"Auto,Auto,*,Auto\")", shortcuts);
        Assert.Contains("TextTrimming = TextTrimming.CharacterEllipsis });", Source("Desktop/Views/Collections/TagManagerDialog.cs"));
    }

    [Fact]
    public void TheShortcutFiltersAreWiderThanTheF1WindowInTheLongestLanguage()
    {
        // Why the filters wrap: at the F1 window's minimum (L, 480 dip less 2 x 16) the two filters and the badge did not fit in one row.
        double room = WorkspaceMetrics.DialogMinimumSize(DialogSize.L).Width - 32;
        double widest = 0;
        foreach (string code in Texts.Languages.Select(language => language.Code))
        {
            Texts.SetLanguage(code);
            double row = 2 * 28 + DialogFooterBudgetTests.Em(Texts.T("shortcuts.window.assignedOnly")) * 13
                + DialogFooterBudgetTests.Em(Texts.T("shortcuts.window.grouped")) * 13 + 2 * 16
                + DialogFooterBudgetTests.Em(Texts.T("shortcuts.window.conflicts", 12)) * 13 + 24;
            widest = Math.Max(widest, row);
        }
        Assert.True(widest > room, $"the widest filter row ({widest:0} dip) fits into {room:0} dip: the wrap is no longer needed");
    }

    private static XElement Named(XDocument document, string name) =>
        document.Descendants().Single(element => (string?)element.Attribute(X + "Name") == name);

    private static string Source(string relative, [CallerFilePath] string sourcePath = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "../..", relative)));
}
