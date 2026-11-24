using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using MeowChan.Desktop;
using MeowChan.Interfaces;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;
using MeowChan.Tags;

namespace MeowChan.DesktopTests;

/// <summary>
/// Narrow-window sweep of the content pages (Tom, 16.11.2026: "headings and texts vanish into nowhere in small windows"), group Archive,
/// reader, Media and Tagging. The pure parts are checked here: the wrapping row with a minimum line height that replaced the Media status
/// line's <c>WrapPanel.ItemHeight</c> (which cut a second status line off), and the reader's find navigation under limited space.
/// </summary>
[Collection("Gallery viewer construction")]
public sealed class NarrowContentLayoutTests
{
    private static string Root(string relative, [CallerFilePath] string sourcePath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "../..", relative));

    private static string Source(string relative) => File.ReadAllText(Root(relative));

    private static Border Box(double width, double height) =>
        new() { Width = width, Height = height, VerticalAlignment = VerticalAlignment.Center };

    private static void Layout(Control control, double width)
    {
        control.Measure(new Size(width, double.PositiveInfinity));
        control.Arrange(new Rect(0, 0, width, control.DesiredSize.Height));
    }

    // ---- the wrapping row with a minimum line height ------------------------------------------------------------------------------

    [Fact]
    public void ItemsFlowLeftToRightAndANewLineStartsWhereTheNextItemNoLongerFits()
    {
        Size[] items = [new(100, 20), new(100, 20), new(100, 20)];
        List<MinLineWrapPanel.Line> lines = MinLineWrapPanel.Lines(items, 220, 8, 32);
        Assert.Equal([new MinLineWrapPanel.Line(0, 1, 208, 32), new MinLineWrapPanel.Line(2, 2, 100, 32)], lines);
        // Room for all three on one line (100 + 8 + 100 + 8 + 100 = 316).
        Assert.Single(MinLineWrapPanel.Lines(items, 316, 8, 32));
        // An unmeasured (infinite) width keeps everything on one line; no items, no lines.
        Assert.Single(MinLineWrapPanel.Lines(items, double.PositiveInfinity, 8, 32));
        Assert.Empty(MinLineWrapPanel.Lines(Array.Empty<Size>(), 300, 8, 32));
    }

    [Fact]
    public void ALineIsAtLeastTheMinimumAndNeverLowerThanItsTallestItem()
    {
        // The regression: a status text that wraps to two lines (here 40 dip high) used to be measured into the 32 dip ItemHeight.
        List<MinLineWrapPanel.Line> lines = MinLineWrapPanel.Lines([new(300, 40), new(80, 24)], 600, 8, 32);
        Assert.Equal(40, Assert.Single(lines).Height);
        Assert.Equal(32, Assert.Single(MinLineWrapPanel.Lines([new(300, 18)], 600, 8, 32)).Height);
        // An item wider than the row stands alone on its line instead of opening an empty one before it.
        List<MinLineWrapPanel.Line> wide = MinLineWrapPanel.Lines([new(500, 18), new(50, 18)], 300, 8, 0);
        Assert.Equal([new MinLineWrapPanel.Line(0, 0, 500, 18), new MinLineWrapPanel.Line(1, 1, 50, 18)], wide);
    }

    [Fact]
    public void ATallItemKeepsItsFullHeightAndAOneLineItemIsCentredInTheMinimumLine()
    {
        Border text = Box(300, 40), link = Box(80, 18);
        MinLineWrapPanel row = new() { ItemSpacing = 8, LineSpacing = 2, MinLineHeight = 32, Children = { text, link } };
        Layout(row, 600);
        Assert.Equal(40, row.DesiredSize.Height, 3);
        Assert.Equal(40, text.Bounds.Height, 3); // not cut to 32
        Assert.Equal(308, link.Bounds.Left, 3);
        Assert.Equal((40 - 18) / 2.0, link.Bounds.Top, 3); // centred in the 40 dip line

        Border single = Box(120, 18);
        MinLineWrapPanel status = new() { ItemSpacing = 8, MinLineHeight = 32, Children = { single } };
        Layout(status, 600);
        Assert.Equal(32, status.DesiredSize.Height, 3);
        Assert.Equal(7, single.Bounds.Top, 3); // (32 - 18) / 2, as ItemHeight centred it
    }

    [Fact]
    public void HiddenItemsTakeNoRoomAndNoGapAndWrappedLinesAreSpaced()
    {
        Border first = Box(100, 20), hidden = Box(100, 20), second = Box(100, 20), third = Box(100, 20);
        hidden.IsVisible = false;
        MinLineWrapPanel row = new() { ItemSpacing = 8, LineSpacing = 2, MinLineHeight = 32, Children = { first, hidden, second, third } };
        Layout(row, 220);
        Assert.Equal(108, second.Bounds.Left, 3); // the hidden box leaves no 8 dip gap behind
        Assert.Equal(0, third.Bounds.Left, 3);
        Assert.Equal(32 + 2 + (32 - 20) / 2.0, third.Bounds.Top, 3);
        Assert.Equal(32 + 2 + 32, row.DesiredSize.Height, 3);
    }

    // ---- the fixes on the pages (source contracts; the layout itself is checked by the central captures) ---------------------------

    [Fact]
    public void TheMediaStatusLineWrapsWithAMinimumLineHeightInsteadOfAFixedItemHeight()
    {
        string filters = Source("Desktop/Views/Media/MediaPage.Filters.cs");
        Assert.Contains("MinLineWrapPanel row = new() { ItemSpacing = 8, LineSpacing = 2 };", filters);
        Assert.Contains("row[!MinLineWrapPanel.MinLineHeightProperty]", filters);
        Assert.DoesNotContain("WrapPanel.ItemHeightProperty", filters);
        // The status stays a direct child of the row: the focus mode, the layout plan and the panel alignment find the row as its parent.
        Assert.Contains("row.Children.Add(status); row.Children.Add(_untaggedLink);", filters);
    }

    [Fact]
    public void TheFocusRowCounterTrimsBesideTheFilterCountInsteadOfBeingClippedWithIt()
    {
        string focus = Source("Desktop/Views/Media/MediaPage.Focus.cs");
        Assert.Contains("Grid left = new() { ColumnDefinitions = new ColumnDefinitions(\"*,Auto,Auto\")", focus);
        Assert.Contains("HorizontalAlignment = HorizontalAlignment.Left };", focus);
        Assert.DoesNotContain("StackPanel left = new() { Orientation = Orientation.Horizontal", focus);
        Assert.Contains("TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis", focus);
        Assert.Contains("ToolTip.SetTip(_focusText, string.IsNullOrWhiteSpace(_status.Text) ? null : _status.Text);", focus);
    }

    [Fact]
    public void TrimmedLabelsOfTheScopeCarryTheirFullTextAsATooltip()
    {
        BooruComponentDispatcher.Run(() =>
        {
            using IDisposable platform = BooruReviewTestPlatform.Enter();
            string longLabel = new('w', 100);
            var term = WorkspaceChips.CreateTermChip(longLabel, negatable: true, exclude: false, remove: () => { }, toggle: () => { });
            var termLabel = Assert.Single(Assert.IsType<StackPanel>(term.Content).Children.OfType<TextBlock>());
            AssertTrimmedTip(termLabel, longLabel, 80);
            var plain = WorkspaceChips.CreateTermChip(longLabel, negatable: false, exclude: false, remove: () => { });
            Assert.Contains(longLabel, Assert.IsType<string>(ToolTip.GetTip(plain)));

            var attachment = new ArchiveAttachmentView();
            var compact = new ArchiveAttachmentRow(new MeowChan.Archive.CapturedAttachment(0, "file", OriginalFileName: "file.png")) { Compact = true };
            attachment.DataContext = compact;
            var fileLine = attachment.GetLogicalDescendants().OfType<TextBlock>().Single(text => text.Text == compact.FileLine);
            Layout(fileLine, 300);
            Assert.Equal(compact.FileLine, ToolTip.GetTip(fileLine));
            attachment.DataContext = compact with { Compact = false };
            Layout(fileLine, 300);
            Assert.Null(ToolTip.GetTip(fileLine));
            Assert.Equal(2, fileLine.MaxLines);

            MediaTagSession tags = new(new MediaTagSubject.Bound(MediaLibraryFilterEngine.TagTarget(QuickTagSurfaceTests.Entry("a", 'a'))!),
                () => new PrivateTagLoadSnapshot(PrivateTagLoadState.Unavailable, null, null),
                (_, _, _) => throw new InvalidOperationException(), (_, _, _, _) => throw new InvalidOperationException());
            using QuickTagSession quickSession = new(QuickTagTarget.For(tags));
            using QuickTagSurface quick = new(quickSession);
            var status = quick.GetLogicalDescendants().OfType<TextBlock>().Single(text => text.Name == "QuickTagStatus");
            Assert.Equal(2, status.MaxLines);
            AssertTrimmedTip(status, MeowChan.Desktop.Localization.Texts.T("tags.unavailable"), 80);

            var sidebar = new MediaTagSidebar();
            var title = sidebar.GetLogicalDescendants().OfType<TextBlock>().Single(text => text.Name == "MediaTagSidebarTitle");
            Assert.False(string.IsNullOrWhiteSpace(title.Text));
            title.Text = longLabel;
            AssertTrimmedTip(title, longLabel, 80);

            using TempDirectory temp = new();
            DesktopSessionHost host = new(temp.Path, Array.Empty<IImageBoard>());
            try
            {
                using TaggingPage page = new(host);
                Control[] controls = ((ILogical)page).GetLogicalDescendants().OfType<Control>().ToArray();
                Grid columnHeader = Assert.IsType<Grid>(controls.Single(control => control.Name == "TaggingColumns"));
                TextBlock header = columnHeader.Children.OfType<TextBlock>().Single(text => Grid.GetColumn(text) == 3);
                Assert.False(string.IsNullOrWhiteSpace(header.Text));
                header.Text = longLabel;
                AssertTrimmedTip(header, longLabel, 80);
                Assert.IsType<WrapPanel>(controls.Single(control => control.Name == "TaggingEmptyActions"));
                Grid progress = Assert.IsType<Grid>(controls.Single(control => control.Name == "TaggingProgress"));
                Assert.Equal(GridLength.Star, progress.ColumnDefinitions[0].Width);
                Assert.Equal(GridLength.Auto, progress.ColumnDefinitions[1].Width);
                Assert.Equal(64, progress.Children[0].MinWidth);
                Grid boxes = Assert.IsType<Grid>(controls.Single(control => control.Name == "TaggingResultFilters"));
                Assert.Equal(new[] { 120d, 112d }, boxes.ColumnDefinitions.Select(column => column.MinWidth));
                Assert.Equal(new[] { 160d, 144d }, boxes.ColumnDefinitions.Select(column => column.MaxWidth));
                Assert.Equal(new[] { "TaggingProgressBar", "TaggingProgressText" }, progress.Children.Select(control => control.Name));
            }
            finally { host.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        });
    }

    private static void AssertTrimmedTip(TextBlock text, string fullText, double width)
    {
        Assert.False(string.IsNullOrWhiteSpace(fullText));
        Layout(text, width);
        Assert.Equal(fullText, ToolTip.GetTip(text));
    }

    [Fact]
    public void TheTipOfATrimTipTextBlockFollowsTheAlwaysSwitch()
    {
        TrimTipTextBlock text = new();
        Assert.False(text.AlwaysTip);
        Assert.Equal(Avalonia.Media.TextWrapping.NoWrap, text.TextWrapping);
        Assert.Equal(Avalonia.Media.TextTrimming.CharacterEllipsis, text.TextTrimming);
        text.AlwaysTip = true;
        Assert.True(text.AlwaysTip);
    }

    [Theory]
    [InlineData(112, true)]
    [InlineData(240, false)]
    public void ReaderFindPositionKeepsItsFullTextAndLeavesRoomForBothArrows(int width, bool trimmed) => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        ArchivePage page = new();
        try
        {
            Control navigation = page.FindControl<Control>("ReaderFindNavigation")!;
            Button previous = page.FindControl<Button>("ReaderFindPrevious")!;
            Button next = page.FindControl<Button>("ReaderFindNext")!;
            TrimTipTextBlock position = page.FindControl<TrimTipTextBlock>("ReaderFindPosition")!;
            string fullText = Texts.T("reader.sidebar.position", 123456, 234567);
            position.Text = fullText;
            position.IsVisible = true;
            Layout(navigation, width);

            Assert.True(previous.Bounds.Width > 0 && next.Bounds.Width > 0);
            Assert.True(previous.Bounds.Right <= position.Bounds.Left && position.Bounds.Right <= next.Bounds.Left);
            Assert.True(next.Bounds.Right <= width);
            Assert.Equal(trimmed, position.IsTrimmed);
            Assert.Equal(trimmed ? fullText : null, ToolTip.GetTip(position));
            Assert.Equal(fullText, position.Text);
            Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(position));
        }
        finally { page.EndLifetime(); }
    });

    [Fact]
    public void TheReaderTitleThePreviewTitleAndTheThreadRowStatusTrimWithTheirFullTextAsTooltip()
    {
        string archive = Source("Desktop/Views/Archive/ArchivePage.axaml");
        Assert.Contains("<controls:TrimTipTextBlock x:Name=\"ArchiveReaderTitle\" Classes=\"section-title\" VerticalAlignment=\"Center\" />", archive);
        Assert.Contains("<controls:TrimTipTextBlock x:Name=\"PreviewTitle\" Classes=\"section-title\" VerticalAlignment=\"Center\" />", archive);
        Assert.Contains("<controls:TrimTipTextBlock Text=\"{Binding Status}\" Classes=\"workspace-meta\" />", archive);
        Assert.Contains("<controls:TrimTipTextBlock Text=\"{Binding MatchGroupHeader}\" Classes=\"workspace-meta\" FontWeight=\"SemiBold\" />", archive);
        // "Show more replies" and its range share a line while they fit and wrap in a narrow card.
        Assert.Contains("<WrapPanel ItemSpacing=\"{StaticResource Workspace.Space.8}\" LineSpacing=\"{StaticResource Workspace.Space.4}\" IsVisible=\"{Binding HasMoreReplies}\">", archive);
    }
}
