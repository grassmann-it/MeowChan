using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using MeowChan.Desktop;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;

namespace MeowChan;

// MP5: the user composes the media page (a booru or "grabber" view if they like). The toolbar's "Layout ▾" button (under More in Barebones),
// the right-click on empty grid space and the entries under More share one menu (MediaLayoutMenu): the tag list (switch, side, a drag handle
// on its edge for the width), the rows above the grid (filter chips, status line, subtitle), the parts of a tile's caption and the tile size,
// three ready-made layouts and "Reset layout". The layout is a pure value (MediaPageLayout) turned into what is visible by a pure plan
// (MediaPageLayoutPlan: state in, blocks out) and kept per profile. Applying a plan only changes visibility, the columns of the body row and the
// size of the tiles that are realized; the grid is never presented again (PresentCount stays), a tile keeps its preview, and the tag counts run
// only while the tag list is visible (MediaPage.BooruMode.cs starts none while it is off). The focus mode keeps working as before and the
// chosen blocks come back when it ends. The toggle on the toolbar is the same "Tag list" block as the menu entry.
public sealed partial class MediaPage
{
    /// <summary>The space the tag list keeps from the page edge (the sidebar's own margin); the column is this much wider than the list.</summary>
    private const double SidebarMargin = 12;
    /// <summary>The width of the drag handle between the tag list and the grid.</summary>
    internal const double HandleWidth = 6;

    private readonly Button _layoutButton = new() { Name = "MediaLayout", Flyout = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedRight } };
    private readonly TextBlock _layoutLabel = new() { Name = "MediaLayoutLabel", VerticalAlignment = VerticalAlignment.Center };
    private readonly GridSplitter _tagHandle = new() { Name = "MediaTagListHandle", IsVisible = false };
    private MediaPageLayout _layout = MediaPageLayout.Standard;
    private MediaPageLayoutPlan? _plan;
    private MediaTileAppearance _tileAppearance = MediaTileMetrics.Standard;
    private Grid? _bodyRow;
    private Border? _bodySurface;
    private Control? _chipRow, _statusRow;
    private bool _layoutInstalled, _statusAttention;
    private double _appliedTagListWidth;

    /// <summary>Saves the layout of the profile (the tag list switch and the rest); null: nothing is saved.</summary>
    internal Func<MediaPageLayout, Task>? SaveLayoutAsync { get; set; }
    /// <summary>The layout as it is now (the tag list switch included).</summary>
    internal MediaPageLayout PageLayout => _layout;
    /// <summary>What the layout shows now (tests and smokes).</summary>
    internal MediaPageLayoutPlan? LayoutPlan => _plan;
    /// <summary>The "Layout ▾" button of the toolbar.</summary>
    internal Button LayoutButton => _layoutButton;
    /// <summary>The drag handle on the edge of the tag list (smokes).</summary>
    internal GridSplitter TagListHandle => _tagHandle;
    /// <summary>What the tiles look like now.</summary>
    internal MediaTileAppearance TileAppearance => _tileAppearance;

    // Called once by the constructor, before the toolbar is built: the button is a text button with a drawn chevron and the layout menu as its flyout.
    private void InitializeLayoutButton()
    {
        Avalonia.Controls.Shapes.Path chevron = new() { Width = 10, Height = 6, Stretch = Stretch.None, StrokeThickness = 1.5, StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        chevron.Bind(Avalonia.Controls.Shapes.Shape.StrokeProperty, chevron.GetObservable(TextElement.ForegroundProperty));
        // The geometry needs the render platform: it is made once the button is shown.
        chevron.AttachedToVisualTree += (_, _) => chevron.Data ??= Geometry.Parse("M 1,1 L 5,5 L 9,1");
        AutomationProperties.SetAccessibilityView(chevron, AccessibilityView.Raw);
        _layoutButton.Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { _layoutLabel, chevron } };
        ((MenuFlyout)_layoutButton.Flyout!).Opening += (_, _) => FillLayoutFlyout((MenuFlyout)_layoutButton.Flyout!);
    }

    private void FillLayoutFlyout(MenuFlyout flyout)
    {
        flyout.Items.Clear();
        foreach (Control item in MediaLayoutMenu.BuildItems(_layout, layout => ApplyLayout(layout))) flyout.Items.Add(item);
    }

    // The entries of the layout menu as a submenu of the toolbar's More ("Layout" while the view or the width keeps the button under it).
    private MenuItem LayoutSubmenu()
    {
        MenuItem item = new() { Header = Texts.T("media.layout.name"), Name = "MediaLayoutSubmenu", ItemsSource = MediaLayoutMenu.BuildItems(_layout, layout => ApplyLayout(layout)).ToArray() };
        AutomationProperties.SetName(item, Texts.T("media.layout.name"));
        if (ToolTip.GetTip(_layoutButton) is string tip) ToolTip.SetTip(item, tip);
        return item;
    }

    private void LocalizeLayout()
    {
        string name = Texts.T("media.layout.name"), tip = Texts.T("media.layout.tip");
        _layoutLabel.Text = name;
        AutomationProperties.SetName(_layoutButton, name);
        AutomationProperties.SetHelpText(_layoutButton, tip);
        ToolTip.SetTip(_layoutButton, tip);
        string resize = Texts.T("media.layout.resize");
        AutomationProperties.SetName(_tagHandle, resize);
        ToolTip.SetTip(_tagHandle, resize);
    }

    // The body row: the tag list column, the drag handle and the grid column; the side and the width come from the plan (LayoutTagList).
    private Grid CreateBodyRow(Control tagList, Border surface)
    {
        Grid row = new() { Name = "MediaBodyRow", ColumnDefinitions = new ColumnDefinitions("0,0,*") };
        row.Children.Add(tagList); row.Children.Add(_tagHandle); row.Children.Add(surface);
        _bodyRow = row;
        _bodySurface = surface;
        // The direction sets the resize cursor, which needs the platform: it is set once the handle is shown.
        _tagHandle.AttachedToVisualTree += (_, _) => _tagHandle.ResizeDirection = GridResizeDirection.Columns;
        _tagHandle.DragCompleted += (_, _) => CommitTagListWidth();
        _tagHandle.KeyUp += (_, _) => CommitTagListWidth();
        row.SizeChanged += (_, e) => { if (e.WidthChanged && _plan is { TagList: true } plan) LayoutTagList(plan); };
        return row;
    }

    // After the header and the body are composed: finds the rows the layout shows or hides, applies the layout read before (if any).
    private void InstallLayout()
    {
        _chipRow = (Control)_filterChips.Parent!;
        _statusRow = (Control)_status.Parent!;
        _layoutInstalled = true;
        ApplyLayoutPlan();
    }

    /// <summary>The saved layout of the profile (the shell, once); nothing is saved back.</summary>
    internal void RestoreLayout(MediaPageLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        if (_lifetime.IsCancellationRequested) return;
        _layout = layout.Normalized();
        SetBooruMode(_layout.TagList, save: false);
        ApplyLayoutPlan();
    }

    /// <summary>Applies and saves a layout (the menu, the presets, the width handle, tests). The tag list switch goes through the one Tag list mode.</summary>
    internal void ApplyLayout(MediaPageLayout next, bool save = true)
    {
        ArgumentNullException.ThrowIfNull(next);
        if (_lifetime.IsCancellationRequested) return;
        MediaPageLayout layout = next.Normalized();
        bool tagListChanged = layout.TagList != _booruModeOn;
        if (layout == _layout && !tagListChanged) return;
        _layout = layout;
        if (tagListChanged) SetBooruMode(layout.TagList, save: false);
        ApplyLayoutPlan();
        if (save) SaveLayout();
    }

    private void SaveLayout()
    {
        if (SaveLayoutAsync is not { } saveLayout || _lifetime.IsCancellationRequested) return;
        _ = SaveQuietlyAsync(saveLayout(_layout));
    }

    // Visibility, columns and tile size only: no presentation, no rebuilt row, no thumbnail loaded again.
    private void ApplyLayoutPlan()
    {
        if (!_layoutInstalled || _lifetime.IsCancellationRequested) return;
        MediaPageLayoutPlan plan = MediaPageLayoutPlan.Of(_layout, new MediaPageLayoutState(_focusMode, _statusAttention));
        MediaPageLayoutPlan? before = _plan;
        _plan = plan;
        // A row that goes away takes the keyboard focus with it: it continues on the grid.
        bool focusLost = (!plan.FilterChips && _chipRow!.IsVisible && FocusWithin(_chipRow)) || (!plan.StatusLine && _statusRow!.IsVisible && FocusWithin(_statusRow));
        _subtitle.IsVisible = plan.Subtitle;
        _chipRow!.IsVisible = plan.FilterChips;
        _statusRow!.IsVisible = plan.StatusLine;
        LayoutTagList(plan);
        if (before == null || before.Tiles != plan.Tiles) ApplyTileAppearance(plan.Tiles);
        if (focusLost) FocusGrid();
    }

    // The side, the width and the margins of the tag list and its handle. The list column is its width plus the page margin, the handle sits between
    // the list and the grid (it takes the place of half the gap the grid kept before).
    private void LayoutTagList(MediaPageLayoutPlan plan)
    {
        if (_bodyRow is not { } row || _bodySurface is not { } surface) return;
        bool right = plan.TagListSide == MediaTagListSide.Right, shown = plan.TagList;
        int listColumn = right ? 2 : 0, gridColumn = right ? 0 : 2;
        Avalonia.Controls.Grid.SetColumn(_tagSidebar, listColumn);
        Avalonia.Controls.Grid.SetColumn(_tagHandle, 1);
        Avalonia.Controls.Grid.SetColumn(surface, gridColumn);
        double width = MediaPageLayoutPlan.EffectiveTagListWidth(plan.TagListWidth, row.Bounds.Width);
        _appliedTagListWidth = width;
        ColumnDefinitions columns = row.ColumnDefinitions;
        columns[listColumn].MinWidth = shown ? Math.Min(MeowChan.Config.MediaTagListWidthMinimum, width) + SidebarMargin : 0;
        columns[listColumn].MaxWidth = shown ? MediaPageLayoutPlan.EffectiveTagListMaximum(row.Bounds.Width) + SidebarMargin : double.PositiveInfinity;
        columns[listColumn].Width = new GridLength(shown ? width + SidebarMargin : 0);
        columns[1].Width = new GridLength(shown ? HandleWidth : 0);
        columns[gridColumn].MinWidth = 0;
        columns[gridColumn].MaxWidth = double.PositiveInfinity;
        columns[gridColumn].Width = new GridLength(1, GridUnitType.Star);
        _tagHandle.IsVisible = shown;
        _tagSidebar.Margin = right ? new Thickness(0, 0, SidebarMargin, 8) : new Thickness(SidebarMargin, 0, 0, 8);
        surface.Margin = !shown ? new Thickness(12, 0, 12, 8) : right ? new Thickness(12, 0, 6, 8) : new Thickness(6, 0, 12, 8);
    }

    // The drag (or the arrow keys) on the handle ended: the width of the list as it is now becomes the saved width (one save).
    private void CommitTagListWidth()
    {
        if (_plan is not { TagList: true } plan || _bodyRow is not { } row || _lifetime.IsCancellationRequested) return;
        double actual = row.ColumnDefinitions[plan.TagListSide == MediaTagListSide.Right ? 2 : 0].ActualWidth - SidebarMargin;
        if (!double.IsFinite(actual) || actual <= 0) return;
        int width = (int)Math.Round(actual);
        if (Math.Abs(width - _appliedTagListWidth) < 1) return;
        ApplyLayout(_layout with { TagListWidth = width });
    }

    // The realized tiles change their own size and parts, the panel its cell; the rows, the selection and the previews stay.
    private void ApplyTileAppearance(MediaTileAppearance appearance)
    {
        MediaTilePanel? panel = TilePanel();
        (int Index, double Offset)? anchor = panel?.CaptureAnchor();
        _tileAppearance = appearance;
        foreach (Control container in _grid.GetRealizedContainers())
            if (container is ContentControl { Presenter.Child: MediaLibraryTileView tile }) tile.Appearance = appearance;
        if (panel == null) return;
        panel.ColumnWidth = appearance.CellWidth;
        panel.ItemHeight = appearance.CellHeight;
        panel.InvalidateMeasure();
        // The row at the top of the view stays at the top of the view while the cells grow or shrink.
        if (anchor is { } kept)
            Dispatcher.UIThread.Post(() =>
            {
                if (!_lifetime.IsCancellationRequested) panel.RestoreAnchor(kept.Index, -Math.Min(-kept.Offset, appearance.CellHeight - 1));
            }, DispatcherPriority.Background);
    }

    // The status line the user left out still shows while it says something that is not a count (a read problem, missing threads, the index over its
    // limit) or while there is nothing to count yet.
    private void UpdateStatusAttention()
    {
        bool attention = _failed || _snapshot == null || (_snapshot is { Complete: false } && _building == null) || CacheLimitNote() != null;
        if (attention == _statusAttention) return;
        _statusAttention = attention;
        ApplyLayoutPlan();
    }
}
