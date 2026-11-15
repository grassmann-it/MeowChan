using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Tags;

namespace MeowChan;

// The second view of the tagging page (TG1): the same entries as a grid of thumbnails next to the list. Both are multi-select ListBoxes
// over the same rows and answer the same commands (menu, keys, selection bar); only one is bound and shown at a time. The switch is
// "List | Grid" in the toolbar and is saved per profile. The grid's panel and cell width are the media page's (one tile size): Ctrl+wheel and
// Ctrl+Plus / Minus / 0 over the grid change that one setting (the same input as on the media page), so both pages always show the same size.
public sealed partial class TaggingPage
{
    private readonly ListBox _grid = new() { Name = "TaggingGrid", SelectionMode = SelectionMode.Multiple, Classes = { "workspace-selection-bar" }, IsVisible = false };
    private readonly TwoWayToggle _viewToggle = new() { Name = "TaggingViewToggle" };
    private TaggingViewMode _viewMode = TaggingViewMode.List;
    private ContextMenuHost? _gridMenuHost;
    private bool _syncingView, _rendering;
    private MediaTileAppearance _tileAppearance = MediaTileMetrics.Standard;
    private MediaTileSize _currentTileSize = MediaTileSize.Medium;

    /// <summary>Reads the media page's active tile metrics; the default keeps the page usable in tests and before the shell wires it.</summary>
    internal Func<MediaTileAppearance>? ReadMediaTileAppearance { get; set; }
    internal MediaTileAppearance TileAppearance => _tileAppearance;

    /// <summary>The tile size both pages share (the media page's setting); the default keeps the page usable in tests and before the shell wires it.</summary>
    internal Func<MediaTileSize>? ReadMediaTileSize { get; set; }
    /// <summary>Applies and saves a tile size the person chose over the grid (the shell: the media page's setting); false when it was not taken.</summary>
    internal Func<MediaTileSize, bool>? ChooseMediaTileSize { get; set; }
    private TileSizeInput? _tileSizeInput;
    /// <summary>The grid's tile size input (tests and smokes).</summary>
    internal TileSizeInput? MediaTileSizeInput => _tileSizeInput;

    /// <summary>The view the person picked: the shell saves it for the profile.</summary>
    internal Action<TaggingViewMode>? SaveViewMode { get; set; }
    internal TaggingViewMode ViewMode => _viewMode;
    internal ListBox TileGrid => _grid;
    internal TwoWayToggle ViewToggle => _viewToggle;
    /// <summary>The list that shows the entries now: the table in the list view, the tile grid in the grid view.</summary>
    internal ListBox ShownList => ActiveList;

    private ListBox ActiveList => _viewMode == TaggingViewMode.Grid ? _grid : _list;
    private ContextMenuHost? ActiveMenuHost => _viewMode == TaggingViewMode.Grid ? _gridMenuHost : _menuHost;

    private void InitializeGrid()
    {
        ApplyTileAppearance(force: true);
        ScrollViewer.SetHorizontalScrollBarVisibility(_grid, Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled);
        _grid.Styles.Add(new Style(selector => selector.OfType<ListBoxItem>())
        {
            Setters =
            {
                new Setter(Avalonia.Controls.Primitives.TemplatedControl.PaddingProperty, new Thickness(0)),
                new Setter(Layoutable.MarginProperty, new Thickness(0)),
                new Setter(ContentControl.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch),
                new Setter(ContentControl.VerticalContentAlignmentProperty, VerticalAlignment.Stretch)
            }
        });
        _grid.ContainerPrepared += (_, e) =>
        {
            TaggingQueueRow? row = e.Container.DataContext as TaggingQueueRow ?? (e.Index >= 0 && e.Index < _rows.Count ? _rows[e.Index] : null);
            if (e.Container is ListBoxItem item && row != null) AutomationProperties.SetName(item, row.TileName(TagCountOf(row.Item)));
        };
        _gridMenuHost = new ContextMenuHost(_grid, ComposeRowMenu, fallbackFocus: () => FocusContent());
        _grid.AddHandler(KeyDownEvent, OnListKeyDown, RoutingStrategies.Tunnel);
        _grid.DoubleTapped += (_, e) =>
        {
            if (e.Source is Visual visual && visual.FindAncestorOfType<ListBoxItem>(includeSelf: true) != null && CanRowCommand(TaggingCommands.ContinueHere))
                OpenActions.Start(() => ExecuteRowCommandAsync(TaggingCommands.ContinueHere), "tagging.grid.doubleClick");
        };
        _grid.SelectionChanged += OnSelectionChanged;
        _tileSizeInput = TileSizeInput.Install(_grid, TileSizeCommands.Media, () => ReadMediaTileSize?.Invoke() ?? _currentTileSize,
            size =>
            {
                if (ChooseMediaTileSize?.Invoke(size) != true) return false;
                _currentTileSize = size;
                ApplyTileAppearance();
                return true;
            });
        _viewToggle.SelectionChanged += (_, _) =>
        {
            if (_syncingView || _localizing) return;
            SetViewMode(_viewToggle.SelectedIndex == 1 ? TaggingViewMode.Grid : TaggingViewMode.List, save: true);
        };
        AutomationProperties.SetName(_grid, Texts.T("tagging.page.listName"));
    }

    private void ApplyTileAppearance(bool force = false)
    {
        MediaTileAppearance next = ReadMediaTileAppearance?.Invoke() ?? MediaTileMetrics.Standard;
        if (next.CellWidth < 1 || next.CellHeight < 1 || next.PreviewHeight < 1) next = MediaTileMetrics.Standard;
        if (!force && next == _tileAppearance) return;
        // The row at the top of the view stays at the top of the view while the cells grow or shrink (the panel and the tiles are made again).
        (int Index, double Offset)? anchor = force ? null : _grid.GetVisualDescendants().OfType<MediaTilePanel>().FirstOrDefault()?.CaptureAnchor();
        _tileAppearance = next;
        _grid.ItemsPanel = new FuncTemplate<Panel?>(() => new MediaTilePanel
        {
            ColumnWidth = _tileAppearance.CellWidth,
            ItemHeight = _tileAppearance.CellHeight
        });
        _grid.ItemTemplate = new FuncDataTemplate<TaggingQueueRow>((_, _) => new TaggingQueueTileView(_thumbnails, TagCountOf, _tileAppearance));
        _grid.InvalidateMeasure();
        if (anchor is { } kept)
            Dispatcher.UIThread.Post(() =>
            {
                if (_queueWork.Ended) return;
                _grid.GetVisualDescendants().OfType<MediaTilePanel>().FirstOrDefault()
                    ?.RestoreAnchor(kept.Index, -Math.Min(-kept.Offset, _tileAppearance.CellHeight - 1));
            }, DispatcherPriority.Background);
    }

    private void LocalizeView()
    {
        _viewToggle.SetIconItems("list", Texts.T("tagging.page.view.list"), Texts.T("tagging.page.view.list.hint"),
            "grid", Texts.T("tagging.page.view.grid"), Texts.T("tagging.page.view.grid.hint"));
        AutomationProperties.SetName(_viewToggle, Texts.T("tagging.page.view"));
        AutomationProperties.SetName(_grid, Texts.T("tagging.page.listName"));
    }

    /// <summary>Restores the saved view (the shell, when the page is built); nothing is saved back.</summary>
    internal void RestoreViewMode(TaggingViewMode mode) => SetViewMode(mode, save: false);

    /// <summary>
    /// Shows the entries as the table or as tiles. The selection and the lead entry stay (by key), the keyboard goes with them when it was
    /// in the list, and the choice is saved when it came from the person.
    /// </summary>
    internal void SetViewMode(TaggingViewMode mode, bool save)
    {
        bool changed = mode != _viewMode;
        _syncingView = true;
        try { _viewToggle.SelectedIndex = mode == TaggingViewMode.Grid ? 1 : 0; }
        finally { _syncingView = false; }
        if (!changed) return;
        TagQueueKey[] keys = SelectedKeys();
        TagQueueKey? lead = Lead?.Key;
        bool focus = ActiveList.IsKeyboardFocusWithin;
        _viewMode = mode;
        Render(keys, lead);
        RefreshWorkView();
        if (save) SaveViewMode?.Invoke(mode);
        if (focus || lead != null)
            Dispatcher.UIThread.Post(() =>
            {
                if (_queueWork.Ended || !IsEffectivelyVisible) return;
                if (lead is { } key && _rows.FirstOrDefault(row => row.Key == key) is { } row) ActiveList.ScrollIntoView(row);
                if (focus) FocusContent();
            }, DispatcherPriority.Input);
    }

    // The selection of either list: the commands, the selection bar and the count follow it (not while the rows are being replaced).
    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_rendering) return;
        RefreshCommandStates();
        UpdateSelectionBar();
    }
}
