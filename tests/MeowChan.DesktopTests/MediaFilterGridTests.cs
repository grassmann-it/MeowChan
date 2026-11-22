using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

// M5: the filter section of the media page as a column grid. The page needs Avalonia's platform (icons), which this test host does not
// start, so what the real controls look like (labels, no overflow at 1440/1100/760/640, the tab key in the window) is driven in the media-page
// smoke. Here: the pure rules (columns per width, the height share, the column-by-column placement), the panel that applies them without a
// window (equal columns, closing up, tab order = tree order = column order, nothing wider than its width), the two-way switch, the texts of every bundled
// language and the source contracts that pin the labels, the stretch, the missing reset button and the missing width literals.
[Collection("Localization")]
public sealed class MediaFilterGridTests : IDisposable
{
    public MediaFilterGridTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage("en"); }

    private static string Source(string relative, [System.Runtime.CompilerServices.CallerFilePath] string sourcePath = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "../..", relative)));

    // ---- columns per width ----

    [Theory]
    [InlineData(1440, 4)]
    [InlineData(1100, 4)]
    [InlineData(1099.5, 3)]
    [InlineData(900, 3)]
    [InlineData(760, 3)]
    [InlineData(759.5, 2)]
    [InlineData(700, 2)]
    [InlineData(640, 2)]
    [InlineData(639.5, 1)]
    [InlineData(600, 1)]
    [InlineData(320, 1)]
    public void TheColumnCountFollowsThePageWidth(double width, int columns) => Assert.Equal(columns, MediaFilterGridLayout.Columns(width));

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void AWidthNotMeasuredYetIsTheWidestLayout(double width) => Assert.Equal(4, MediaFilterGridLayout.Columns(width));

    [Fact]
    public void TheColumnCountNeverGrowsWithANarrowerPage()
    {
        int previous = int.MaxValue;
        for (double width = 2000; width >= 200; width -= 20)
        {
            int columns = MediaFilterGridLayout.Columns(width);
            Assert.InRange(columns, 1, MediaFilterGridLayout.MaximumColumns);
            Assert.True(columns <= previous, width + ": " + columns + " after " + previous);
            previous = columns;
        }
    }

    // ---- the height the section may take ----

    [Theory]
    [InlineData(920, 260)]
    [InlineData(640, 260)]       // 640 itself is not "under 640"
    [InlineData(639, 255.6)]     // 40 % of 639 is below the 260 ceiling
    [InlineData(500, 200)]
    [InlineData(400, 160)]
    public void AShortPageGivesTheSectionFortyPercentAndATallOneTheFixedCeiling(double pageHeight, double expected) =>
        Assert.Equal(expected, MediaFilterGridLayout.MaxHeight(pageHeight, MediaPage.FilterSectionMaxHeight), 3);

    [Theory]
    [InlineData(0)]
    [InlineData(double.NaN)]
    public void AHeightNotMeasuredYetKeepsTheFixedCeiling(double pageHeight) =>
        Assert.Equal(MediaPage.FilterSectionMaxHeight, MediaFilterGridLayout.MaxHeight(pageHeight, MediaPage.FilterSectionMaxHeight));

    [Fact]
    public void TheCeilingIsNeverRaisedByTheShare() =>
        Assert.True(Enumerable.Range(100, 900).All(height => MediaFilterGridLayout.MaxHeight(height, 260) <= 260));

    // ---- placement ----

    [Fact]
    public void EightCellsInFourColumnsFillTwoRowsDownTheColumns()
    {
        IReadOnlyList<MediaFilterGridLayout.Slot> slots = MediaFilterGridLayout.Place(8, 4);
        Assert.Equal([(0, 0), (0, 1), (1, 0), (1, 1), (2, 0), (2, 1), (3, 0), (3, 1)], slots.Select(slot => (slot.Column, slot.Row)));
    }

    [Theory]
    [InlineData(8, 3, 3, 3)]
    [InlineData(8, 2, 2, 4)]
    [InlineData(8, 1, 1, 8)]
    [InlineData(5, 4, 3, 2)]   // five cells do not need a fourth column
    [InlineData(1, 4, 1, 1)]
    [InlineData(4, 4, 4, 1)]
    public void TheColumnsAreAsEvenAsTheyCanBe(int count, int columns, int usedColumns, int rows)
    {
        IReadOnlyList<MediaFilterGridLayout.Slot> slots = MediaFilterGridLayout.Place(count, columns);
        Assert.Equal(count, slots.Count);
        Assert.Equal(usedColumns, slots.Max(slot => slot.Column) + 1);
        Assert.Equal(rows, slots.Max(slot => slot.Row) + 1);
        Assert.True(slots.Max(slot => slot.Column) < columns);
    }

    [Fact]
    public void ThePlacementIsColumnByColumnForEveryCountAndWidth()
    {
        // The order of the list is the order of the tab key: down one column, then on to the next, never back.
        for (int count = 0; count <= 12; count++)
            for (int columns = 1; columns <= MediaFilterGridLayout.MaximumColumns; columns++)
            {
                MediaFilterGridLayout.Slot[] slots = MediaFilterGridLayout.Place(count, columns).ToArray();
                Assert.Equal(count, slots.Length);
                Assert.Equal(slots.Length, slots.Distinct().Count());
                for (int index = 1; index < slots.Length; index++)
                    Assert.True(slots[index].Column > slots[index - 1].Column || slots[index].Column == slots[index - 1].Column && slots[index].Row == slots[index - 1].Row + 1,
                        count + " cells in " + columns + " columns: " + slots[index - 1] + " then " + slots[index]);
            }
    }

    [Fact]
    public void NoCellsAndNonsenseColumnsGiveNoOrOneColumnPlaces()
    {
        Assert.Empty(MediaFilterGridLayout.Place(0, 4));
        Assert.Empty(MediaFilterGridLayout.Place(-3, 4));
        Assert.Equal([(0, 0), (0, 1), (0, 2)], MediaFilterGridLayout.Place(3, 0).Select(slot => (slot.Column, slot.Row)));
    }

    // ---- the panel ----

    // The cells have a height of their own and, like the real ones, no width: the column gives it to them. With measured widths they ask for 40, 50, …
    private static MediaFilterFieldGrid Grid(int cells, int columns, out Border[] children, bool measuredWidths = false)
    {
        children = Enumerable.Range(0, cells).Select(index => new Border { MinHeight = 24 + index % 2 * 16, MinWidth = measuredWidths ? 40 + 10 * index : 0 }).ToArray();
        MediaFilterFieldGrid grid = new() { Columns = columns };
        foreach (Border cell in children) grid.Children.Add(cell);
        return grid;
    }

    private static void Layout(MediaFilterFieldGrid grid, double width)
    {
        grid.Measure(new Size(width, double.PositiveInfinity));
        grid.Arrange(new Rect(default, new Size(width, grid.DesiredSize.Height)));
    }

    [Theory]
    [InlineData(1440, 8)]
    [InlineData(1100, 8)]
    [InlineData(760, 8)]
    [InlineData(640, 8)]
    [InlineData(1100, 5)]
    [InlineData(320, 8)]
    public void TheTreeOrderIsTheColumnOrderAtEveryWidthSoTheTabKeyGoesDownThenAcross(double width, int cells)
    {
        MediaFilterFieldGrid grid = Grid(cells, MediaFilterGridLayout.Columns(width), out Border[] children);
        Layout(grid, width);
        // Walking the children in tree order (what the tab key follows) goes down a column and then to the column on its right.
        for (int index = 1; index < children.Length; index++)
        {
            Rect before = children[index - 1].Bounds, at = children[index].Bounds;
            bool nextInColumn = Math.Abs(at.X - before.X) < 0.01 && at.Y > before.Y;
            bool nextColumn = at.X > before.X + 0.01 && at.Y < before.Y + 0.01 && before.Y > 0 - 0.01 && Math.Abs(at.Y) < 0.01;
            Assert.True(nextInColumn || nextColumn, width + ": cell " + index + " at " + at + " after " + before);
        }
    }

    [Theory]
    [InlineData(1440)]
    [InlineData(1100)]
    [InlineData(760)]
    [InlineData(640)]
    [InlineData(500)]
    public void TheCellsAreEqualColumnsThatFitTheWidth(double width)
    {
        int columns = MediaFilterGridLayout.Columns(width);
        MediaFilterFieldGrid grid = Grid(8, columns, out Border[] children);
        Layout(grid, width);
        double cell = (width - 8 * (columns - 1)) / columns;
        Assert.All(children, child =>
        {
            Assert.Equal(cell, child.Bounds.Width, 3);
            Assert.True(child.Bounds.Right <= width + 0.001, "wider than the grid: " + child.Bounds);
            Assert.True(child.Bounds.X >= 0);
        });
        Assert.InRange(grid.UsedColumns, 1, columns);
        Assert.Equal(width, grid.DesiredSize.Width, 3);
    }

    [Fact]
    public void ARowIsAsHighAsItsTallestCellAndTheRowsAreEightApart()
    {
        MediaFilterFieldGrid grid = Grid(4, 2, out Border[] children); // heights 24, 40, 24, 40; two columns of two rows
        Layout(grid, 400);
        Assert.Equal(0, children[0].Bounds.Y, 3);
        Assert.Equal(0, children[2].Bounds.Y, 3);
        // Row 0 holds cells 0 and 2 (24 and 24), row 1 holds cells 1 and 3 (40 and 40).
        Assert.Equal(24 + 8, children[1].Bounds.Y, 3);
        Assert.Equal(24 + 8, children[3].Bounds.Y, 3);
        Assert.Equal(24 + 8 + 40, grid.DesiredSize.Height, 3);
    }

    [Fact]
    public void AHiddenCellLeavesNoGapAndTheOthersCloseUp()
    {
        MediaFilterFieldGrid grid = Grid(8, 4, out Border[] children);
        children[1].IsVisible = false; children[2].IsVisible = false; children[3].IsVisible = false; // a work view holds three fields back
        Layout(grid, 1100);
        Assert.Equal(5, grid.Cells.Count);
        // Five cells in four columns: two rows, three columns used, the cells in tree order.
        Assert.Equal(3, grid.UsedColumns);
        Assert.Equal([children[0], children[4], children[5], children[6], children[7]], grid.Cells);
        Assert.Equal(0, children[0].Bounds.X, 3);
        Assert.Equal(grid.Cells[2].Bounds.X, children[5].Bounds.X, 3);
        Assert.True(children[4].Bounds.Y > children[0].Bounds.Y);
        Assert.Equal(0, children[5].Bounds.Y, 3);
        children[2].IsVisible = true; // coming back: it takes its place again
        Layout(grid, 1100);
        Assert.Equal(6, grid.Cells.Count);
    }

    [Fact]
    public void ANarrowerColumnCountReflowsTheSameCells()
    {
        MediaFilterFieldGrid grid = Grid(8, 4, out Border[] children);
        Layout(grid, 1100);
        double wide = grid.DesiredSize.Height;
        grid.Columns = 1;
        Layout(grid, 500);
        Assert.True(grid.DesiredSize.Height > wide * 2, "one column must be much taller");
        Assert.All(children, child => Assert.Equal(0, child.Bounds.X, 3));
        Assert.Equal(children.Select(child => child.Bounds.Y).Order(), children.Select(child => child.Bounds.Y));
    }

    [Fact]
    public void AnEmptyGridHasNoSize()
    {
        MediaFilterFieldGrid grid = new() { Columns = 3 };
        Layout(grid, 800);
        Assert.Equal(0, grid.DesiredSize.Height);
        Assert.Equal(0, grid.UsedColumns);
    }

    [Fact]
    public void AnUnboundedWidthTakesTheWidestCell()
    {
        MediaFilterFieldGrid grid = Grid(4, 2, out _, measuredWidths: true);
        grid.Measure(Size.Infinity);
        Assert.Equal(2 * 70 + 8, grid.DesiredSize.Width, 3); // two columns of the widest cell (40 + 10 * 3)
    }

    // ---- the two-way switch ----

    [Fact]
    public void TheSwitchHasOneStateOnAndReportsEveryChange()
    {
        TwoWayToggle toggle = new();
        int changes = 0;
        toggle.SelectionChanged += (_, _) => changes++;
        Assert.Equal(0, toggle.SelectedIndex);
        Assert.Equal(true, toggle.First.IsChecked);
        Assert.Equal(false, toggle.Second.IsChecked);
        toggle.SelectedIndex = 1;
        Assert.Equal((1, true, false), (toggle.SelectedIndex, toggle.Second.IsChecked, toggle.First.IsChecked));
        toggle.SelectedIndex = 1; // no change, no event
        toggle.SelectedIndex = 0;
        Assert.Equal(2, changes);
        toggle.SelectedIndex = 7; // out of range is brought into it
        Assert.Equal(1, toggle.SelectedIndex);
        toggle.SelectedIndex = -4;
        Assert.Equal(0, toggle.SelectedIndex);
    }

    [Fact]
    public void AClickOnTheStateThatIsOnChangesNothingAndTheOtherOneSwitches()
    {
        TwoWayToggle toggle = new();
        int changes = 0;
        toggle.SelectionChanged += (_, _) => changes++;
        toggle.First.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.Equal((0, 0, true), (toggle.SelectedIndex, changes, toggle.First.IsChecked == true));
        toggle.Second.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.Equal((1, 1, true), (toggle.SelectedIndex, changes, toggle.Second.IsChecked == true && toggle.First.IsChecked != true));
    }

    [Fact]
    public void TheArrowKeysSwitchAndTheStateThatIsOnIsTheOnlyTabStop()
    {
        TwoWayToggle toggle = new();
        Assert.True(KeyboardNavigation.GetIsTabStop(toggle.First));
        Assert.False(KeyboardNavigation.GetIsTabStop(toggle.Second));
        toggle.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Right });
        Assert.Equal(1, toggle.SelectedIndex);
        Assert.False(KeyboardNavigation.GetIsTabStop(toggle.First));
        Assert.True(KeyboardNavigation.GetIsTabStop(toggle.Second));
        toggle.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Left });
        Assert.Equal(0, toggle.SelectedIndex);
        // Other keys and modified arrows are left alone.
        toggle.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Right, KeyModifiers = KeyModifiers.Control });
        toggle.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Down });
        Assert.Equal(0, toggle.SelectedIndex);
    }

    [Fact]
    public void TheSwitchNamesItsTwoStatesAndExplainsThem()
    {
        TwoWayToggle toggle = new();
        toggle.SetItems("all", "any", "All included tags", "At least one included tag");
        Assert.Equal(("all", "any"), (toggle.First.Content, toggle.Second.Content));
        Assert.Equal("All included tags", ToolTip.GetTip(toggle.First));
        Assert.Equal("At least one included tag", ToolTip.GetTip(toggle.Second));
        Assert.Equal("any", Avalonia.Automation.AutomationProperties.GetName(toggle.Second));
        toggle.SelectedIndex = 1; // new words keep the state
        toggle.SetItems("alle", "einer");
        Assert.Equal(1, toggle.SelectedIndex);
    }

    // ---- texts ----

    public static IEnumerable<object[]> Languages() => new[] { "en", "de" }.Concat(Texts.BundledPackCodes()).Select(code => new object[] { code });

    [Theory]
    [MemberData(nameof(Languages))]
    public void EveryLanguageHasShortSwitchWordsAndTheFieldLabels(string language)
    {
        Texts.SetLanguage(language);
        foreach (string key in new[] { "media.filter.match.all", "media.filter.match.any", "media.filter.kind", "media.filter.sound", "media.filter.source",
                     "media.filter.board", "media.filter.rating", "media.filter.group.tag", "tags.filter.matchAll", "tags.filter.matchAny", "tags.filter.matchLabel" })
            Assert.False(string.IsNullOrWhiteSpace(Texts.T(key)) || Texts.T(key) == key, language + ": " + key);
        string all = Texts.T("media.filter.match.all"), any = Texts.T("media.filter.match.any");
        Assert.NotEqual(all, any);
        // The two words sit side by side in a switch inside a 360 dip row beside the tag button: short words only.
        Assert.True(all.Length <= 12 && any.Length <= 12, language + ": " + all + " | " + any);
    }

    [Fact]
    public void TheSwitchWordsAreAllAndAnyInEnglishAndGerman()
    {
        Assert.Equal(("all", "any"), (Texts.T("media.filter.match.all"), Texts.T("media.filter.match.any")));
        Texts.SetLanguage("de");
        Assert.Equal(("alle", "einer"), (Texts.T("media.filter.match.all"), Texts.T("media.filter.match.any")));
    }

    // ---- source contracts ----

    private const string Grid_ = "Desktop/Views/Media/MediaPage.FilterGrid.cs";
    private static readonly string[] FilterFiles =
    [
        "Desktop/Views/Media/MediaPage.Filters.cs", "Desktop/Views/Media/MediaPage.FilterGrid.cs", "Desktop/Views/Media/MediaPage.FilterSurface.cs",
        "Desktop/Views/Media/MediaPage.Audio.cs", "Desktop/Views/Media/MediaPage.cs"
    ];

    [Fact]
    public void NoFilterFieldHasAWidthLiteralAnyMoreAndTheCombosStretch()
    {
        foreach (string file in FilterFiles)
        {
            string text = Source(file);
            foreach (string control in new[] { "_kindFilter", "_soundFilter", "_sourceFilter", "_boardFilter", "_minRating", "_tagMode", "_tagAdd" })
                Assert.DoesNotMatch(@"\b" + control + @"\s*=\s*new[^;\n]*MinWidth\s*=\s*\d", text);
        }
        string filters = Source("Desktop/Views/Media/MediaPage.Filters.cs"), audio = Source("Desktop/Views/Media/MediaPage.Audio.cs");
        Assert.DoesNotContain("MinWidth = 130", filters);
        Assert.DoesNotContain("MinWidth = 160", filters);
        Assert.DoesNotContain("MinWidth = 130", audio);
        string grid = Source(Grid_);
        Assert.Contains("field.HorizontalAlignment = HorizontalAlignment.Stretch;", grid);
        Assert.Contains("_tagAdd.HorizontalAlignment = HorizontalAlignment.Stretch;", grid);
    }

    [Fact]
    public void EveryComboHasALabelFromItsExistingTextAndTheFlagsAreCells()
    {
        string grid = Source(Grid_);
        foreach ((string key, string field) in new[] { ("media.filter.kind", "_kindFilter"), ("media.filter.sound", "_soundFilter"), ("media.filter.source", "_sourceFilter"),
                     ("media.filter.board", "_boardFilter"), ("media.filter.rating", "_minRating") })
            Assert.Contains($"AddFieldCell(\"{key}\", {field});", grid);
        Assert.Contains("Classes = { \"workspace-field-label\" }", grid);
        Assert.Contains("AutomationProperties.SetLabeledBy(field, label);", grid);
        Assert.Contains("foreach (CheckBox flag in new[] { _favoritesOnly, _withoutMediaTags, _missing }) AddFlagCell(flag);", grid);
        Assert.Contains("_tagLabel.Text = Texts.T(\"media.filter.group.tag\");", grid);
        // The column order is the tab order: type, sound | source, board | rating, the flags.
        Assert.True(grid.IndexOf("_kindFilter", StringComparison.Ordinal) < grid.IndexOf("_soundFilter", StringComparison.Ordinal));
        Assert.True(grid.IndexOf("_soundFilter);", StringComparison.Ordinal) < grid.IndexOf("_sourceFilter);", StringComparison.Ordinal));
        Assert.True(grid.IndexOf("_boardFilter);", StringComparison.Ordinal) < grid.IndexOf("_minRating);", StringComparison.Ordinal));
        Assert.True(grid.IndexOf("_minRating);", StringComparison.Ordinal) < grid.IndexOf("AddFlagCell(flag)", StringComparison.Ordinal));
    }

    // F05: the layout contract (chips of one height on one centre line, check boxes level with the dropdowns) is measured in the media-page smoke
    // (RequireChipRowAlignment / RequireFlagAlignment); here the source rules that produce it.
    [Fact]
    public void ChipRowItemsShareOneHeightAndTheFlagsAreCentredOnTheDropdowns()
    {
        string chips = Source("Desktop/Controls/WorkspaceChips.cs"), row = Source("Desktop/Views/Media/MediaPage.FilterChips.cs"), grid = Source(Grid_);
        Assert.Equal(MeowChan.Desktop.WorkspaceMetrics.FilterChipHeight, WorkspaceChips.MarkRowItem(new Button()).Height);
        Assert.Contains("control.Height = RowHeight;", chips);
        Assert.Contains("content.VerticalContentAlignment = VerticalAlignment.Center;", chips);
        Assert.Contains("MarkRowItem(tag);", chips); // every filter chip, both pages
        Assert.Contains("WorkspaceChips.MarkRowItem(_filterResetAll)", row);
        Assert.Contains("WorkspaceChips.MarkRowItem(_filterAdd)", row);
        Assert.DoesNotContain("MinHeight = 32", row);
        Assert.Contains("flag.VerticalAlignment = VerticalAlignment.Center;", grid);
        Assert.Contains("Height = FilterFieldHeight", grid);
        Assert.DoesNotContain("flag.VerticalAlignment = VerticalAlignment.Bottom;", grid);
    }

    [Fact]
    public void TheSectionHasNoResetButtonAndTheChipRowKeepsResetAll()
    {
        foreach (string file in FilterFiles.Append("Desktop/Views/Media/MediaPage.FilterChips.cs"))
        {
            string text = Source(file);
            Assert.DoesNotContain("_filterReset ", text);
            Assert.DoesNotContain("_filterReset.", text);
            Assert.DoesNotContain("\"MediaFilterReset\"", text);
        }
        string chips = Source("Desktop/Views/Media/MediaPage.FilterChips.cs");
        Assert.Contains("Name = \"MediaFilterResetAll\"", chips);
        Assert.Contains("WorkspaceChips.MarkRowItem(_filterResetAll)", chips); // F05: the chips' height (was MinHeight = 32)
        Assert.Contains("_filterResetAll.Content = Texts.T(\"media.filter.resetAll\");", chips);
        // The grid's empty state keeps its own reset (a filter that matches nothing).
        Assert.Contains("_emptyReset.Content = Texts.T(\"media.filter.reset\");", chips);
    }

    [Fact]
    public void TheTagModeIsTheSwitchAndStillTheOnlyPlaceTheFilterReadsTheMode()
    {
        string filters = Source("Desktop/Views/Media/MediaPage.Filters.cs"), grid = Source(Grid_), work = Source("Desktop/Views/Media/MediaPage.WorkView.cs");
        Assert.Contains("private readonly TwoWayToggle _tagMode = new() { Name = \"MediaTagMode\" };", filters);
        Assert.DoesNotContain("ComboBox _tagMode", filters);
        Assert.Contains("MediaFilterTerms.ToFilter(AllTerms(), _tagMode.SelectedIndex == 1 ? ArchiveTagMatch.Any : ArchiveTagMatch.All)", filters);
        Assert.Contains("_tagMode.SetItems(Texts.T(\"media.filter.match.all\"), Texts.T(\"media.filter.match.any\"),", grid);
        Assert.Contains("Texts.T(\"tags.filter.matchAll\"), Texts.T(\"tags.filter.matchAny\")", grid);
        // The switch matters from two included tags on (and while it says "any", so it can be set back); the work view still gates it.
        Assert.Contains("private bool TagModeNeeded => TagTerms(false).Count() > 1 || _tagMode.SelectedIndex > 0;", work);
        Assert.Contains("stateVisible: field != _tagMode || TagModeNeeded", work);
        Assert.Contains("WorkViewElement.SetId(_tagMode, \"media.filter.tag-mode\");", work);
    }

    [Fact]
    public void ThePageWidthChoosesTheColumnsAndThePageHeightTheMostTheSectionTakes()
    {
        string grid = Source(Grid_), surface = Source("Desktop/Views/Media/MediaPage.FilterSurface.cs");
        Assert.Contains("SizeChanged += (_, e) => ApplyFilterSurfaceSize(e.NewSize.Width, e.NewSize.Height);", grid);
        Assert.Contains("_filterGrid.Columns = MediaFilterGridLayout.Columns(width);", grid);
        Assert.Contains("ApplyFilterSurfaceHeight(height);", grid);
        Assert.Contains("_filterScroll.MaxHeight = MediaFilterGridLayout.MaxHeight(pageHeight, FilterSectionMaxHeight);", surface);
        Assert.Equal(260, MediaPage.FilterSectionMaxHeight);
    }

    [Fact]
    public void TheTermSetStaysTheOneTruthAndTheCombosKeepMirroringIt()
    {
        string filters = Source("Desktop/Views/Media/MediaPage.Filters.cs");
        Assert.Contains("private readonly List<MediaFilterTerm> _terms = new();", filters);
        Assert.Contains("private void SyncFilterControls()", filters);
        Assert.Contains("Texts.Count(\"media.filter.kind.count\"", filters);
        Assert.Contains("Texts.T(\"media.filter.combo.excluded\", excluded)", filters);
        // The grid only places the controls: it reads and writes nothing of the filter.
        string grid = Source(Grid_);
        foreach (string forbidden in new[] { "_terms", "EditTerms", "MediaFilterTermSet", "CurrentFilter", "FilterEdited" })
            Assert.DoesNotContain(forbidden, grid);
    }

    [Fact]
    public void TheFocusGroupsStillFindTheSurfaceAndTheSectionStaysAnInlineBorderWithCycleTabbing()
    {
        string focus = Source("Desktop/Views/Media/MediaPage.Focus.cs"), surface = Source("Desktop/Views/Media/MediaPage.FilterSurface.cs");
        Assert.Contains("Control[] bottom = { _queryHint, (Control)_status.Parent!, _filterSurface! };", focus);
        Assert.Contains("Name = \"MediaFilterSurface\"", surface);
        Assert.Contains("KeyboardNavigation.SetTabNavigation(surface, KeyboardNavigationMode.Cycle);", surface);
        Assert.Contains("return _filterSurface = surface;", surface);
    }
}
