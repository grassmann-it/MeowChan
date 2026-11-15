using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using MeowChan.Desktop;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;

namespace MeowChan;

// MP5: the user composes the media page (a booru or "grabber" view if they like). The toolbar's "Layout ▾" button (under More in Barebones),
// the right-click on empty grid space and the entries under More share one menu, GL3's layout menu (C15): MediaLayoutMenu describes the rows
// and what each does, the flyout is a LayoutMenuPanel, the other two are LayoutMenuBuilder.ToContextMenu forms of the same rows: the tag list (switch, side, a drag handle
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

    private readonly Button _layoutButton = new() { Name = "MediaLayout" };
    private readonly Flyout _layoutFlyout = new() { Placement = PlacementMode.BottomEdgeAlignedRight };
    private LayoutMenuPanel _layoutPanel = null!;
    private bool _layoutMenuHadFocus, _layoutMenuKeyboard;
    private readonly TextBlock _layoutLabel = new() { Name = "MediaLayoutLabel", VerticalAlignment = VerticalAlignment.Center };
    private readonly GridSplitter _tagHandle = new() { Name = "MediaTagListHandle", IsVisible = false };
    private readonly GridSplitter _panelHandle = new() { Name = "MediaPanelHandle", IsVisible = false };
    private MediaPageLayout _layout = MediaPageLayout.Standard;
    private MediaPageLayoutPlan? _plan;
    private MediaTileAppearance _tileAppearance = MediaTileMetrics.Standard;
    private Grid? _bodyRow, _contentGrid;
    private Border? _bodySurface;
    private Control? _chipRow, _statusRow;
    private bool _layoutInstalled, _statusAttention;
    private double _appliedTagListWidth, _appliedPanelWidth, _panelEdge = SidebarMargin;
    private bool _panelHasItems = true;

    /// <summary>Saves the layout of the profile (the tag list switch and the rest); null: nothing is saved.</summary>
    internal Func<MediaPageLayout, Task>? SaveLayoutAsync { get; set; }
    /// <summary>The layout as it is now (the tag list switch included).</summary>
    internal MediaPageLayout PageLayout => _layout;
    /// <summary>What the layout shows now (tests and smokes).</summary>
    internal MediaPageLayoutPlan? LayoutPlan => _plan;
    /// <summary>The "Layout ▾" button of the toolbar.</summary>
    internal Button LayoutButton => _layoutButton;
    /// <summary>The flyout of the "Layout ▾" button (smokes and tests).</summary>
    internal Flyout LayoutFlyout => _layoutFlyout;
    /// <summary>The rows of that flyout (smokes and tests).</summary>
    internal LayoutMenuPanel LayoutMenuRows => _layoutPanel;
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
        _layoutButton.Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _layoutLabel, chevron } };
        _layoutPanel = new LayoutMenuPanel("MediaLayoutMenu", () => MediaLayoutMenu.Rows(_layout, PanelHint()), ApplyLayoutRow);
        _layoutPanel.CloseRequested += () => _layoutFlyout.Hide();
        _layoutFlyout.FlyoutPresenterClasses.Add("gallery-overflow");
        _layoutFlyout.Content = new ScrollViewer
        {
            Content = _layoutPanel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        _layoutButton.Flyout = _layoutFlyout;
        _layoutPanel.AddHandler(KeyDownEvent, (_, e) =>
        {
            _layoutMenuKeyboard = true;
            if (_layoutPanel.HandleMenuKey(e)) e.Handled = true;
        }, RoutingStrategies.Tunnel);
        _layoutPanel.GotFocus += (_, _) => _layoutMenuHadFocus = true;
        _layoutFlyout.Opening += (_, _) =>
        {
            _layoutMenuHadFocus = _layoutMenuKeyboard = false;
            if (_layoutFlyout.Content is ScrollViewer scroller) scroller.MaxHeight = Math.Max(160, Bounds.Height - 72);
            _layoutPanel.Refresh(); // the rows show the layout as it is at this moment
        };
        // The keyboard continues in the menu: the first usable row.
        _layoutFlyout.Opened += (_, _) => Dispatcher.UIThread.Post(() => _layoutPanel.FocusFirst(), DispatcherPriority.Loaded);
        _layoutFlyout.Closed += (_, _) =>
        {
            if (!_layoutMenuHadFocus || _lifetime.IsCancellationRequested || !_layoutButton.IsEffectivelyVisible) return;
            _layoutMenuHadFocus = false;
            // The focus returns to the button the menu came from.
            _layoutButton.Focus(_layoutMenuKeyboard ? NavigationMethod.Tab : NavigationMethod.Pointer);
        };
    }

    // A row was chosen (flyout, submenu or right-click): the layout it asks for is applied and saved.
    private void ApplyLayoutRow(LayoutMenuRow row)
    {
        if (!row.Enabled) return;
        if (MediaLayoutMenu.Apply(row.Id, _layout) is { } next) ApplyLayout(next);
    }

    // The same rows as menu items (new at every call: a control has one parent, the submenu under More and the context menu each take their own).
    internal IReadOnlyList<Control> LayoutMenuItems() =>
        ContextMenuBuilder.Build(LayoutMenuBuilder.ToContextMenu(MediaLayoutMenu.Rows(_layout, PanelHint()), ApplyLayoutRow, MediaLayoutMenu.MenuName).ToModel())?.ItemsSource
            is IEnumerable<Control> items ? items.ToArray() : [];

    // The entries of the layout menu as a submenu of the toolbar's More ("Layout" while the view or the width keeps the button under it).
    private MenuItem LayoutSubmenu()
    {
        MenuItem item = new() { Header = Texts.T("media.layout.name"), Name = "MediaLayoutSubmenu", ItemsSource = LayoutMenuItems() };
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

    // The Layout menu's panel row says why a chosen panel is not on screen: the page is narrower than the setting.
    private string? PanelHint() => _panelCollapsed ? Texts.T("media.layout.panel.collapsed", MediaPanelColumns.MinimumBodyWidth(_layout, PanelMinimumPageWidth)) : null;

    // The body row: the tag list column, its drag handle and the content column. The content column is a grid of its own: the media panel, its drag
    // handle and the grid surface (the panel sits on the left or the right of the grid, the tag list beside both on its own side).
    // Sides, widths and margins come from the plan (LayoutBody).
    private Grid CreateBodyRow(Control tagList, Border surface)
    {
        Grid content = _contentGrid = new() { Name = "MediaContentRow", ColumnDefinitions = new ColumnDefinitions("*,0,0") };
        content.Children.Add(surface); content.Children.Add(_panelHandle);
        Grid row = new() { Name = "MediaBodyRow", ColumnDefinitions = new ColumnDefinitions("0,0,*") };
        row.Children.Add(tagList); row.Children.Add(_tagHandle); row.Children.Add(content);
        _bodyRow = row;
        _bodySurface = surface;
        // The direction sets the resize cursor, which needs the platform: it is set once the handle is shown.
        _tagHandle.AttachedToVisualTree += (_, _) => _tagHandle.ResizeDirection = GridResizeDirection.Columns;
        _tagHandle.DragCompleted += (_, _) => CommitTagListWidth();
        _tagHandle.KeyUp += (_, _) => CommitTagListWidth();
        _panelHandle.Width = HandleWidth;
        _panelHandle.AttachedToVisualTree += (_, _) => _panelHandle.ResizeDirection = GridResizeDirection.Columns;
        _panelHandle.DragCompleted += (_, _) => CommitPanelWidth();
        _panelHandle.KeyUp += (_, _) => CommitPanelWidth();
        row.SizeChanged += (_, e) => { if (e.WidthChanged && _plan is { } plan) LayoutBody(plan); };
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

    /// <summary>The tag list width that the settings page saved (M1): shown now, nothing saved again. The rest of the layout stays as the page has it.</summary>
    internal void ApplySavedTagListWidth(int width) => ApplyLayout(_layout with { TagListWidth = width }, save: false);

    /// <summary>The media panel width that the settings page saved (M1): shown now, nothing saved again. The page width below which the panel hides is read again too.</summary>
    internal void ApplySavedPanelWidth(int width)
    {
        if (_layout.PanelWidth != width) ApplyLayout(_layout with { PanelWidth = width }, save: false);
        else ApplyLayoutPlan();
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
        // The summary and the selection actions are one context line (same height, same place): while tiles are selected the actions take the place
        // of the summary, unless the summary says something that must stay visible (a read problem, an incomplete index). The chip row has
        // no height while no filter is active (its "+ Filter" shows with the first chip), so it is hidden then instead of adding an empty gap.
        bool chips = ChipRowShown(plan), status = plan.StatusLine && (_grid.Selection.Count == 0 || _statusAttention);
        // A row that goes away takes the keyboard focus with it: it continues on the grid.
        bool focusLost = (!chips && _chipRow!.IsVisible && FocusWithin(_chipRow)) || (!status && _statusRow!.IsVisible && FocusWithin(_statusRow));
        _subtitle.IsVisible = plan.Subtitle;
        _chipRow!.IsVisible = chips;
        _statusRow!.IsVisible = status;
        LayoutBody(plan);
        if (before == null || before.Tiles != plan.Tiles) ApplyTileAppearance(plan.Tiles);
        if (focusLost) FocusGrid();
    }

    // The chip row shows when the layout wants it and there is something in it: a chip, or "+ Filter" (which follows the first chip).
    private bool ChipRowShown(MediaPageLayoutPlan plan) => plan.FilterChips && (ActiveFilterCount > 0 || _filterAdd.IsVisible);

    // The chips were rendered: the row appears with the first chip and goes with the last one (a layout that hides the row keeps it hidden).
    private void UpdateChipRowVisibility()
    {
        if (!_layoutInstalled || _plan is not { } plan || _lifetime.IsCancellationRequested) return;
        bool shown = ChipRowShown(plan);
        if (_chipRow!.IsVisible == shown) return;
        bool focusLost = !shown && FocusWithin(_chipRow);
        _chipRow.IsVisible = shown;
        if (focusLost) FocusGrid();
    }

    // The body row of this plan: the sides, widths and margins of the tag list and the media panel with their handles. The tag list column is its width
    // plus the page margin, a handle sits between each block and the grid (it takes the place of half the gap the grid kept before). MediaPanelColumns
    // (pure) decides the widths and whether the panel shows: it hides on a narrow page and keeps the choice; the grid keeps the rest.
    private void LayoutBody(MediaPageLayoutPlan plan)
    {
        if (_bodyRow is not { } row || _bodySurface is not { } surface || _contentGrid is not { } content) return;
        double bodyWidth = row.Bounds.Width;
        MediaPanelColumns panelColumns = MediaPanelColumns.Of(_layout, bodyWidth, PanelMinimumPageWidth);
        bool right = plan.TagListSide == MediaTagListSide.Right, shown = plan.TagList;
        int listColumn = right ? 2 : 0, contentColumn = right ? 0 : 2;
        Avalonia.Controls.Grid.SetColumn(_tagSidebar, listColumn);
        Avalonia.Controls.Grid.SetColumn(_tagHandle, 1);
        Avalonia.Controls.Grid.SetColumn(content, contentColumn);
        double width = shown ? panelColumns.TagListWidth : MediaPageLayoutPlan.EffectiveTagListWidth(plan.TagListWidth, bodyWidth);
        _appliedTagListWidth = width;
        ColumnDefinitions columns = row.ColumnDefinitions;
        columns[listColumn].MinWidth = shown ? Math.Min(MeowChan.Config.MediaTagListWidthMinimum, width) + SidebarMargin : 0;
        columns[listColumn].MaxWidth = shown ? MediaPanelColumns.MaximumTagListWidth(bodyWidth, panelColumns.PanelShown && _tiles.Count > 0, panelColumns.PanelWidth) + SidebarMargin : double.PositiveInfinity;
        columns[listColumn].Width = new GridLength(shown ? width + SidebarMargin : 0);
        columns[1].Width = new GridLength(shown ? HandleWidth : 0);
        columns[contentColumn].MinWidth = 0;
        columns[contentColumn].MaxWidth = double.PositiveInfinity;
        columns[contentColumn].Width = new GridLength(1, GridUnitType.Star);
        _tagHandle.IsVisible = shown;
        _tagSidebar.Margin = right ? new Thickness(0, 0, SidebarMargin, 8) : new Thickness(SidebarMargin, 0, 0, 8);

        // The content column: the panel on its side, then its handle, then the grid.
        _panelHasItems = _tiles.Count > 0;
        bool panelRight = plan.PanelSide == MediaPanelSide.Right, panelShown = panelColumns.PanelShown && _panelHasItems;
        int panelColumn = panelRight ? 2 : 0, surfaceColumn = panelRight ? 0 : 2;
        // The panel's outer edge keeps the page margin, or the smaller gap beside the tag list's handle when the list is on the same side.
        double edge = shown && right == panelRight ? 8 : SidebarMargin;
        _panelEdge = edge;
        _appliedPanelWidth = panelShown ? panelColumns.PanelWidth : plan.PanelWidth;
        ColumnDefinitions inner = content.ColumnDefinitions;
        Avalonia.Controls.Grid.SetColumn(surface, surfaceColumn);
        Avalonia.Controls.Grid.SetColumn(_panelHandle, 1);
        inner[panelColumn].MinWidth = panelShown ? Math.Min(MeowChan.Config.MediaPanelWidthMinimum, panelColumns.PanelWidth) + edge : 0;
        inner[panelColumn].MaxWidth = panelShown ? MediaPanelColumns.MaximumPanelWidth(_layout, bodyWidth) + edge : double.PositiveInfinity;
        inner[panelColumn].Width = new GridLength(panelShown ? panelColumns.PanelWidth + edge : 0);
        inner[1].Width = new GridLength(panelShown ? HandleWidth : 0);
        inner[surfaceColumn].MinWidth = 0;
        inner[surfaceColumn].MaxWidth = double.PositiveInfinity;
        inner[surfaceColumn].Width = new GridLength(1, GridUnitType.Star);
        _panelHandle.IsVisible = panelShown;
        // Tab follows what is seen: panel, handle, grid on the left; grid, handle, panel on the right.
        KeyboardNavigation.SetTabIndex(surface, panelRight ? 0 : 2);
        KeyboardNavigation.SetTabIndex(_panelHandle, 1);
        if (_panel != null) KeyboardNavigation.SetTabIndex(_panel, panelRight ? 2 : 0);
        // The surface keeps the page margin on a side where nothing is shown, the smaller gap beside a handle.
        bool leftBlock = shown && !right || panelShown && !panelRight, rightBlock = shown && right || panelShown && panelRight;
        surface.Margin = new Thickness(leftBlock ? 8 : 12, 0, rightBlock ? 8 : 12, 8);
        _panelCollapsed = panelColumns.CollapsedByWidth;
        bool focusLost = !panelShown && _panelShown && _panel?.IsKeyboardFocusWithin == true;
        if (panelShown) { ReaderMediaSidebar created = EnsurePanel(); if (!content.Children.Contains(created)) content.Children.Add(created); }
        if (_panel is { } panel)
        {
            Avalonia.Controls.Grid.SetColumn(panel, panelColumn);
            panel.BorderThickness = new Thickness(panelRight ? 1 : 0, 0, panelRight ? 0 : 1, 0);
            panel.IsVisible = panelShown;
        }
        if (panelShown != _panelShown)
        {
            _panelShown = panelShown;
            OnPanelShownChanged(panelShown);
        }
        if (_panel is { } aligned)
        {
            // The room the header's last line keeps for the lifted panel header: the panel's column and handle, plus the tag list's when it is on
            // the same side, less the page margin the header already has, plus the grid's gap (MediaPage.PanelAlignment.cs).
            double reserve = inner[panelColumn].Width.Value + HandleWidth + 8 - SidebarMargin
                + (shown && right == panelRight ? columns[listColumn].Width.Value + HandleWidth : 0);
            AlignPanel(aligned, panelRight, edge, reserve);
        }
        if (focusLost) FocusGrid();
    }

    // The drag (or the arrow keys) on the tag list's handle ended: the width of the list as it is now becomes the saved width (one save).
    private void CommitTagListWidth()
    {
        if (_plan is not { TagList: true } plan || _bodyRow is not { } row || _lifetime.IsCancellationRequested) return;
        double actual = row.ColumnDefinitions[plan.TagListSide == MediaTagListSide.Right ? 2 : 0].ActualWidth - SidebarMargin;
        if (!double.IsFinite(actual) || actual <= 0) return;
        int width = (int)Math.Round(actual);
        if (Math.Abs(width - _appliedTagListWidth) < 1) return;
        ApplyLayout(_layout with { TagListWidth = width });
    }

    // The same for the media panel's handle.
    private void CommitPanelWidth()
    {
        if (!_panelShown || _contentGrid is not { } content || _plan is not { } plan || _lifetime.IsCancellationRequested) return;
        double actual = content.ColumnDefinitions[plan.PanelSide == MediaPanelSide.Right ? 2 : 0].ActualWidth - _panelEdge;
        if (!double.IsFinite(actual) || actual <= 0) return;
        int width = Math.Clamp((int)Math.Round(actual), MeowChan.Config.MediaPanelWidthMinimum, MeowChan.Config.MediaPanelWidthMaximum);
        if (Math.Abs(width - _appliedPanelWidth) < 1) return;
        ApplyLayout(_layout with { PanelWidth = width });
    }

    // The realized tiles change their own size and parts, the tile panel its cell; the rows, the selection and the previews stay.
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
