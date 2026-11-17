using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Presenters;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.WorkViews;
using MeowChan.Interfaces;
using Semi.Avalonia;
using Ursa.Themes.Semi;

namespace MeowChan.DesktopTests;

[Collection("Gallery viewer construction")]
public sealed class TaggingRowStructureTests
{
    private static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private sealed class Root : Decorator, ILogicalRoot;

    private static void WithPage(Action<TaggingPage> test) => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable scope = AvaloniaLocator.EnterScope();
        AvaloniaLocator.CurrentMutable.Bind<ICursorFactory>().ToConstant(new DeadAffordanceGuard.RecordingCursorFactory());
        TokenApplication.Bind();
        Texts.Initialize();
        Texts.SetLanguage("en");
        using TempDirectory temp = new();
        DesktopSessionHost host = new(temp.Path, Array.Empty<IImageBoard>());
        TaggingPage page = new(host);
        page.Styles.Add(new StyleInclude(new Uri("avares://MeowChan/"))
            { Source = new Uri("avares://MeowChan/Desktop/WorkspaceStyles.axaml") });
        try { test(page); }
        finally { page.End(); host.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    });

    private static T Find<T>(TaggingPage page, string name) where T : Control =>
        ((ILogical)page).GetLogicalDescendants().OfType<T>().Single(c => c.Name == name);

    [Fact]
    public void ExactSectionAndRowOrderHasNoStandaloneProgressStatusSecondaryToolbarOrFooter() => WithPage(page =>
    {
        Grid root = Assert.IsType<Grid>(page.Content);
        Assert.Equal(2, root.RowDefinitions.Count);
        Assert.Equal(2, root.Children.Count);
        StackPanel sections = Find<StackPanel>(page, "TaggingSections");
        Assert.Equal(new[] { "TaggingHeading", "TaggingError", "TaggingQueueControls", "TaggingStamp" }, sections.Children.Select(c => c.Name));
        StackPanel queue = Find<StackPanel>(page, "TaggingQueueControls");
        Assert.Equal(new[] { "TaggingQueueActionRow", "TaggingClearConfirm" }, queue.Children.Select(c => c.Name));
        TaggingControlRow actions = Find<TaggingControlRow>(page, "TaggingQueueActionRow");
        Assert.Equal(new[] { "TaggingActions", "TaggingFeedback" }, actions.Children.Select(c => c.Name));
        Assert.Equal(new[] { "TaggingProgress", "TaggingStatus" }, Find<StackPanel>(page, "TaggingFeedback").Children.Select(c => c.Name));
        Assert.Contains(page.ProgressText, ((ILogical)actions).GetLogicalDescendants());
        Assert.Contains(Find<ProgressBar>(page, "TaggingProgressBar"), ((ILogical)actions).GetLogicalDescendants());
        Assert.Contains(Find<TextBlock>(page, "TaggingPercent"), ((ILogical)actions).GetLogicalDescendants());
        Assert.Same(page.StartButton.Parent, page.LastShownContext.Parent);
        Assert.IsType<TextBlock>(page.LastShownContext); // One continue entry point, not a second link.
        Assert.Equal(new[] { "TaggingStartContext", "TaggingAdd", "TaggingClearDone", "TaggingClear", "TaggingHelp" }, page.Actions.Items.Cast<Control>().Select(c => c.Name));
        Assert.DoesNotContain(((ILogical)page).GetLogicalDescendants().OfType<Control>(), c => c.Name is "TaggingKeysHint" or "TaggingSecondaryToolbar");
        Grid listArea = Find<Grid>(page, "TaggingListArea");
        Assert.Equal(new[] { "TaggingListControls", "TaggingSelectionBar", "TaggingListSurface" }, listArea.Children.Select(c => c.Name));
        Assert.Equal(new[] { 0, 1, 2 }, listArea.Children.Select(Grid.GetRow));
        Assert.Equal(new[] { "TaggingGroupPanel", "TaggingFilterPanel", "TaggingListPresentation" }, page.ListControls.Children.Select(c => c.Name));
        Assert.Same(page.ListControls.Children[0], page.GroupBox.Parent);
        Assert.Same(page.ListControls.Children[1], page.FilterBox.Parent);
        ActionToolBar presentation = Find<ActionToolBar>(page, "TaggingListPresentation");
        Assert.Equal(new Control[] { page.ColumnsButton, page.ViewToggle }, presentation.Items.Cast<Control>());
    });

    [Fact]
    public void ClearResponseImmediatelyFollowsItsRowAndStatusStaysInFeedbackEvenForEmptyQueue() => WithPage(page =>
    {
        StackPanel queue = Find<StackPanel>(page, "TaggingQueueControls");
        typeof(TaggingPage).GetMethod("AskClear", Private)!.Invoke(page, null);
        Assert.True(page.ClearQuestionOpen);
        Assert.Same(Find<Border>(page, "TaggingClearConfirm"), queue.Children[1]);
        Assert.Same(Find<TaggingControlRow>(page, "TaggingQueueActionRow"), queue.Children[0]);
        page.ExecuteRowCommandAsync("tagging.mark-done", explain: true).GetAwaiter().GetResult();
        Assert.True(page.StatusText.IsVisible);
        Assert.True(page.StatusText.Parent is Control { IsVisible: true });
        Assert.Same(Find<StackPanel>(page, "TaggingFeedback"), page.StatusText.Parent);
    });

    [Theory]
    [InlineData("en")]
    [InlineData("de")]
    public void HelpOwnsTheDynamicShortcutReferenceInEveryWorkView(string language) => WithPage(page =>
    {
        Texts.SetLanguage(language);
        typeof(TaggingPage).GetMethod("Localize", Private)!.Invoke(page, null);
        foreach (WorkView view in WorkViewSetting.All)
        {
            page.ApplyWorkView(view);
            HelpContent content = page.HelpButton.Resolve()!;
            // Use the live catalog rather than a duplicated gesture in the expectation.
            string expected = Texts.T("tagging.page.keys", Key("tagging.continue-here"), Key("tagging.remove"), Key("tagging.move-up"), Key("tagging.move-down"));
            Assert.Contains(expected, content.Paragraphs);
            Assert.DoesNotContain("under the list", content.PlainText, StringComparison.OrdinalIgnoreCase);
        }
        static string Key(string id) => MeowChan.Desktop.Commands.CommandCatalog.ShortcutDisplay(id)
            ?? MeowChan.Desktop.Commands.CommandCatalog.Label(id);
    });

    [Theory]
    [InlineData("Dark")]
    [InlineData("Light")]
    public void HeaderIsInsideTheBorderedSurfaceAndUsesTheShared32DipRaisedPresentation(string theme) => WithPage(page =>
    {
        Application.Current!.RequestedThemeVariant = theme == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light;
        Border header = Find<Border>(page, "TaggingColumnHeaderSurface");
        Border surface = Find<Border>(page, "TaggingListSurface");
        Assert.Contains(header, ((ILogical)surface).GetLogicalDescendants());
        Assert.Same(header, page.ColumnHeader.Parent);
        Grid table = Assert.IsType<Grid>(surface.Child);
        Assert.Equal(2, table.RowDefinitions.Count);
        Assert.Equal(0, Grid.GetRow(header));
        Assert.Equal(1, Grid.GetRow(Assert.IsAssignableFrom<Control>(page.List.Parent)));
        Assert.Contains("workspace-table-header", header.Classes);
        Assert.True(header.TryFindResource("Workspace.Size.Input", out object? height));
        Assert.Equal(height, header.Height);
        Assert.Equal(32, header.Height);
        Assert.Equal(new Thickness(0, 0, 0, 1), header.BorderThickness);
        Assert.True(header.TryFindResource("Workspace.Raised", out object? raised));
        Assert.Equal(((ISolidColorBrush)raised!).Color, ((ISolidColorBrush)header.Background!).Color);
        Assert.True(header.TryFindResource("Workspace.Border", out object? rule));
        Assert.Equal(((ISolidColorBrush)rule!).Color, ((ISolidColorBrush)header.BorderBrush!).Color);
        // Actual measure/arrange, not merely the Height setter.
        header.IsVisible = page.ColumnHeader.IsVisible = true;
        foreach (TextBlock text in page.ColumnHeader.Children.OfType<TextBlock>()) text.IsVisible = false;
        header.Measure(new Size(1100, double.PositiveInfinity));
        header.Arrange(new Rect(0, 0, 1100, header.DesiredSize.Height));
        Assert.Equal(32, header.Bounds.Height);
    });

    [Theory]
    [InlineData(false, false, 44)]
    [InlineData(false, true, 44)]
    [InlineData(true, false, 32)]
    [InlineData(true, true, 32)]
    public void RealSemiItemCompleteOuterHeightIncludesPaddingAndSelectionBorder(bool compact, bool selected, double expected) => WithPage(page =>
    {
        Application app = Application.Current!;
        app.Styles.Insert(0, new SemiTheme());
        app.Styles.Insert(1, new UrsaSemiTheme());
        page.List.ItemsSource = null;
        page.List.Classes.Set("compact", compact);
        TaggingQueueRowView row = new(compact: compact);
        // Real row and thumbnail geometry; isolate from native text shaping, which this non-window fixture does not initialize.
        foreach (TextBlock text in ((ILogical)row).GetLogicalDescendants().OfType<TextBlock>()) text.IsVisible = false;
        ListBoxItem item = new() { Content = row, IsSelected = selected };
        page.List.Items.Add(item);
        Root root = new() { Child = page };
        item.Theme = (ControlTheme)app.FindResource(typeof(ListBoxItem))!;
        item.ApplyStyling();
        item.ApplyTemplate();
        foreach (Control child in item.GetVisualDescendants().OfType<Control>().ToArray())
        {
            child.ApplyStyling();
            if (child is TemplatedControl templated) templated.ApplyTemplate();
        }
        try
        {
            item.Measure(new Size(1100, double.PositiveInfinity));
            item.Arrange(new Rect(0, 0, 1100, item.DesiredSize.Height));
            Assert.Equal(selected, item.IsSelected);
            Assert.Equal(expected, item.DesiredSize.Height);
            Assert.Equal(expected, item.Bounds.Height);
            Assert.Equal(0, item.Margin.Top + item.Margin.Bottom);
            Assert.Equal(0, item.Padding.Top + item.Padding.Bottom + item.BorderThickness.Top + item.BorderThickness.Bottom);
            Assert.Equal(new Thickness(3, 0, 0, 0), item.BorderThickness);
            Assert.Equal(new Thickness(9, 0, 12, 0), item.Padding);
            ContentPresenter presenter = item.GetVisualDescendants().OfType<ContentPresenter>().Single(p => p.Name == "PART_ContentPresenter");
            Assert.Equal(item.Padding, presenter.Padding);
            Assert.Equal(item.BorderThickness, presenter.BorderThickness);
            Assert.Equal(expected, presenter.Bounds.Height);
            Console.WriteLine($"Tagging outer height compact={compact} selected={selected}: {item.Bounds.Height} dip; presenter={presenter.Bounds.Height}; content={row.Bounds.Height}");
        }
        finally { root.Child = null; }
    });

    [Fact]
    public void IdenticalGroupsWrapAccordingToTheirOwnMeasuredSizesNotAWindowBreakpoint() => BooruComponentDispatcher.Run(() =>
    {
        TaggingControlRow row = new();
        Border first = new() { Width = 240, Height = 32 };
        Border second = new() { Width = 180, Height = 32 };
        Border presentation = new() { Width = 160, Height = 32 };
        row.Children.Add(first); row.Children.Add(second); row.Children.Add(presentation);
        Layout(row, 620);
        Assert.Equal(0, presentation.Bounds.Y);
        Assert.Equal(620, presentation.Bounds.Right);
        Layout(row, 600);
        Assert.Equal(40, presentation.Bounds.Y); // sum = 612: wrap, even far below the old 1100 threshold.
        first.Width = 280;
        Layout(row, 640);
        Assert.Equal(40, presentation.Bounds.Y); // same controls, same viewport, different content size.
        first.Width = 200;
        Layout(row, 640);
        Assert.Equal(0, presentation.Bounds.Y);
        Layout(row, 580);
        Assert.Equal(0, presentation.Bounds.Y); // measured total is now 572.
        Assert.Equal(new Control[] { first, second, presentation }, row.Children);
    });

    [Fact]
    public void WrapHysteresisPreventsOnePixelResizeFlickerWithoutReparentingOrOverlap() => BooruComponentDispatcher.Run(() =>
    {
        TaggingControlRow row = new();
        Border first = new() { Width = 300, Height = 32 };
        Border second = new() { Width = 200, Height = 32 };
        row.Children.Add(first); row.Children.Add(second);
        Layout(row, 600);
        Assert.Equal(0, second.Bounds.Y);
        Layout(row, 515);
        Assert.Equal(40, second.Bounds.Y);
        foreach (double width in new double[] { 516, 517, 515, 531, 516, 531 })
        {
            Layout(row, width);
            Assert.Equal(40, second.Bounds.Y);
            Assert.Same(row, second.Parent);
        }
        Layout(row, 532); // measured 516 + a 16-dip group-gap of hysteresis.
        Assert.Equal(0, second.Bounds.Y);
        foreach (double width in new double[] { 531, 517, 516 })
        {
            Layout(row, width);
            Assert.Equal(0, second.Bounds.Y);
            Assert.True(first.Bounds.Right + row.GroupSpacing <= second.Bounds.Left);
        }
    });

    private static void Layout(TaggingControlRow row, double width)
    {
        row.InvalidateMeasure();
        row.Measure(new Size(width, double.PositiveInfinity));
        row.Arrange(new Rect(0, 0, width, row.DesiredSize.Height));
    }
}
