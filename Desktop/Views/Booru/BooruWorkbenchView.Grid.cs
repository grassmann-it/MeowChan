using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.VisualTree;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.PluginSdk;

namespace MeowChan.Desktop.Booru;

/// <summary>How the Booru page shows its results (E1): the list of cards, a grid of tiles, or the inline gallery (one large image). A view preference of the profile.</summary>
internal enum BooruResultView { List, Grid, Gallery }

/// <summary>The saved spelling of <see cref="BooruResultView"/> (<c>Config.BooruPageView</c>): unknown or empty text means the list.</summary>
internal static class BooruResultViews
{
    internal static BooruResultView Parse(string? text) =>
        Enum.TryParse(text?.Trim(), ignoreCase: true, out BooruResultView view) && Enum.IsDefined(view) ? view : BooruResultView.List;
}

/// <summary>
/// The measures of the result tiles (pure). The cell widths are the media page's (<see cref="MediaTileMetrics"/>: 150, 190 and 250 dip), so a tile
/// size means the same thing on both pages; they are layout measures of the tile, not limits of the user's data. The picture is the cell minus the
/// tile's padding and border and is square.
/// </summary>
internal static class BooruTileMetrics
{
    /// <summary>The padding inside a tile on each side, in dip.</summary>
    internal const double Padding = 8;
    /// <summary>The border of the card style on each side, in dip.</summary>
    internal const double Border = 1;
    /// <summary>The accent bar at the start edge of a selected tile, in dip (the media page's selection bar).</summary>
    internal const double SelectionBar = 3;
    /// <summary>The space between tiles, in dip.</summary>
    internal const double Spacing = 8;
    /// <summary>A check mark (the corner mark "saved locally").</summary>
    internal const string LocalMark = "M2,6.5 L5,9.5 L10,2.5";

    internal static double CellWidth(MediaTileSize size) => size switch
    {
        MediaTileSize.Small => MediaTileMetrics.SmallCellWidth,
        MediaTileSize.Large => MediaTileMetrics.LargeCellWidth,
        _ => MediaTileMetrics.MediumCellWidth
    };

    /// <summary>The edge of the square picture of a tile at <paramref name="size"/>.</summary>
    internal static double PictureSize(MediaTileSize size) => CellWidth(size) - 2 * (Padding + Border);

    /// <summary>How many tiles of <paramref name="cellWidth"/> fit into <paramref name="available"/> (at least one; an unbounded width gives one column).</summary>
    internal static int Columns(double available, double cellWidth, double spacing = Spacing)
    {
        if (double.IsNaN(available) || double.IsInfinity(available) || cellWidth <= 0) return 1;
        return Math.Max(1, (int)Math.Floor((available + spacing) / (cellWidth + spacing)));
    }

    /// <summary>The rating as the provider's one-letter code (G, S, Q, E; a question mark when unknown): a code of the provider, the same in every language.</summary>
    internal static string RatingCode(BooruRating rating) => rating switch
    {
        BooruRating.General => "G",
        BooruRating.Sensitive => "S",
        BooruRating.Questionable => "Q",
        BooruRating.Explicit => "E",
        _ => "?"
    };

    /// <summary>The one line under a tile's picture: "#12345 · Q · 87" (the score is left out when the provider sent none).</summary>
    internal static string Line(string postId, BooruRating rating, int? score) =>
        score is { } value
            ? string.Join(" · ", "#" + postId, RatingCode(rating), value.ToString(Texts.Culture))
            : string.Join(" · ", "#" + postId, RatingCode(rating));
}

/// <summary>
/// The panel of the result tiles: as many equal columns as fit the width (one column at least), each tile as wide as its column and as high as the
/// tallest tile of its row. <see cref="Columns"/> is what the last layout used, so the arrow keys can step by a row.
/// </summary>
internal sealed class BooruTilePanel : Panel
{
    internal static readonly StyledProperty<double> ColumnWidthProperty =
        AvaloniaProperty.Register<BooruTilePanel, double>(nameof(ColumnWidth), BooruTileMetrics.CellWidth(MediaTileSize.Medium));

    /// <summary>The narrowest a column is made (the cell width of the tile size).</summary>
    internal double ColumnWidth { get => GetValue(ColumnWidthProperty); set => SetValue(ColumnWidthProperty, value); }

    /// <summary>The number of columns of the last layout (1 before the first).</summary>
    internal int Columns { get; private set; } = 1;

    static BooruTilePanel() => AffectsMeasure<BooruTilePanel>(ColumnWidthProperty);

    private double ChildWidth(double width, int columns) =>
        double.IsFinite(width) ? Math.Max(0, (width - BooruTileMetrics.Spacing * (columns - 1)) / columns) : ColumnWidth;

    protected override Size MeasureOverride(Size availableSize)
    {
        Columns = BooruTileMetrics.Columns(availableSize.Width, ColumnWidth);
        double childWidth = ChildWidth(availableSize.Width, Columns);
        double total = 0, row = 0;
        int placed = 0;
        foreach (Control child in Children)
        {
            child.Measure(new Size(childWidth, double.PositiveInfinity));
            row = Math.Max(row, child.DesiredSize.Height);
            if (++placed % Columns != 0) continue;
            total += row + BooruTileMetrics.Spacing;
            row = 0;
        }
        if (placed % Columns != 0) total += row + BooruTileMetrics.Spacing;
        double width = double.IsFinite(availableSize.Width) ? availableSize.Width : Columns * ColumnWidth + (Columns - 1) * BooruTileMetrics.Spacing;
        return new Size(width, Math.Max(0, total - BooruTileMetrics.Spacing));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Columns = BooruTileMetrics.Columns(finalSize.Width, ColumnWidth);
        double childWidth = ChildWidth(finalSize.Width, Columns);
        double top = 0;
        for (int start = 0; start < Children.Count; start += Columns)
        {
            int end = Math.Min(Children.Count, start + Columns);
            double height = 0;
            for (int index = start; index < end; index++) height = Math.Max(height, Children[index].DesiredSize.Height);
            for (int index = start; index < end; index++)
                Children[index].Arrange(new Rect((index - start) * (childWidth + BooruTileMetrics.Spacing), top, childWidth, height));
            top += height + BooruTileMetrics.Spacing;
        }
        return finalSize;
    }
}

/// <summary>
/// The second view of the Booru results (E1): the same hits as tiles next to the list. A tile is the picture (square, never enlarged beyond its own
/// pixels, with the corner marks of the card), one line "#id · rating · score", the selection check and "Open in gallery"; everything else of the
/// hit (download, show in Media, post link) is in the tile's context menu. The switch "List | Tiles" and the tile size S/M/L sit above the results
/// and are saved per profile by the shell (<see cref="SaveResultView"/>, <see cref="SaveTileSize"/>). Ctrl/Cmd+Plus, Minus, 0 and Ctrl+wheel over the
/// tiles step the size through the media page's three commands (<see cref="TileSizeCommands.Media"/>, no new key). The arrow keys move between tiles
/// (BooruWorkbenchView.Hits.cs: Left/Right by one, Up/Down by a row).
/// </summary>
internal sealed partial class BooruWorkbenchView
{
    /// <summary>The decoded width of a preview: the largest tile picture (Large) is smaller, so a tile never shows an enlarged bitmap.</summary>
    private const int PreviewDecodeWidth = 256;

    private readonly ThreeWayToggle _viewToggle = new() { Name = "BooruViewToggle" };
    private readonly ToggleButton _sizeSmall = new() { Name = "BooruTileSmall", Classes = { "segment", "segment-first" } };
    private readonly ToggleButton _sizeMedium = new() { Name = "BooruTileMedium", Classes = { "segment" } };
    private readonly ToggleButton _sizeLarge = new() { Name = "BooruTileLarge", Classes = { "segment", "segment-last" } };
    private readonly StackPanel _sizeGroup = new() { Name = "BooruTileSize", Orientation = Orientation.Horizontal };
    private readonly StackPanel _viewBar = new() { Name = "BooruViewBar", Orientation = Orientation.Horizontal, Spacing = 12, HorizontalAlignment = HorizontalAlignment.Right, IsVisible = false };
    private BooruResultView _resultView = BooruResultView.List;
    private MediaTileSize _tileSize = MediaTileSize.Medium;
    private BooruTilePanel? _tiles;
    private bool _syncingView;

    /// <summary>The shell saves the view the person picked (the profile's <c>BooruPageView</c>).</summary>
    internal Action<BooruResultView>? SaveResultView { get; set; }
    /// <summary>The shell saves the tile size the person chose (the profile's <c>BooruTileSize</c>).</summary>
    internal Action<MediaTileSize>? SaveTileSize { get; set; }
    internal BooruResultView ResultView => _resultView;
    internal MediaTileSize TileSize => _tileSize;
    /// <summary>The tile panel while the tiles are shown and there are hits (tests and smokes).</summary>
    internal BooruTilePanel? TilePanel => _tiles;
    internal ThreeWayToggle ViewToggle => _viewToggle;

    /// <summary>The hit cards or tiles that are shown now, in order.</summary>
    private IReadOnlyList<Control> ResultCards => _tiles is { } tiles && ReferenceEquals(tiles.Parent, _results) ? tiles.Children : _results.Children;

    /// <summary>Restores the saved view and tile size (the shell, when the page is built); nothing is saved back.</summary>
    internal void RestoreResultView(BooruResultView view, MediaTileSize size)
    {
        if (_closed) return;
        _resultView = view;
        _tileSize = Enum.IsDefined(size) ? size : MediaTileSize.Medium;
        Render(force: true);
    }

    /// <summary>Shows the hits as the list or as tiles; the choice is saved when it came from the person.</summary>
    internal void SetResultView(BooruResultView view, bool save)
    {
        if (_closed || view == _resultView) { SyncViewBar(); return; }
        bool focus = _results.IsKeyboardFocusWithin;
        string? focused = focus ? (TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() as Visual)?.GetSelfAndVisualAncestors()
            .OfType<Control>().FirstOrDefault(control => control.Name?.StartsWith("BooruResult:", StringComparison.Ordinal) == true)?.Name : null;
        // Leaving the gallery with the focus on its stage or buttons: the cursor's hit takes the focus; entering it from a hit: the stage does.
        if (focused is null && focus && _resultView == BooruResultView.Gallery && _cursorPostId is { } leaving) focused = "BooruResult:" + leaving;
        _resultView = view;
        Render(force: true);
        if (save) SaveResultView?.Invoke(view);
        if (focused is not null) FocusHit(focused["BooruResult:".Length..]);
        else if (view == BooruResultView.Gallery && _results.IsKeyboardFocusWithin) FocusGalleryStage();
    }

    /// <summary>Sets the tile size (shown at once, saved once); false when it is the current one.</summary>
    internal bool ChooseTileSize(MediaTileSize size)
    {
        if (_closed || !Enum.IsDefined(size) || size == _tileSize) { SyncViewBar(); return false; }
        _tileSize = size;
        Render(force: true);
        SaveTileSize?.Invoke(size);
        return true;
    }

    private StackPanel BuildResultViewBar()
    {
        _sizeGroup.Children.Add(_sizeSmall);
        _sizeGroup.Children.Add(_sizeMedium);
        _sizeGroup.Children.Add(_sizeLarge);
        AutomationProperties.SetControlTypeOverride(_sizeGroup, Avalonia.Automation.Peers.AutomationControlType.Group);
        foreach ((ToggleButton button, MediaTileSize size) in new[] { (_sizeSmall, MediaTileSize.Small), (_sizeMedium, MediaTileSize.Medium), (_sizeLarge, MediaTileSize.Large) })
            button.Click += (_, _) => ChooseTileSize(size);
        _viewToggle.SelectionChanged += (_, _) =>
        {
            if (!_syncingView) SetResultView(_viewToggle.SelectedIndex switch { 1 => BooruResultView.Grid, 2 => BooruResultView.Gallery, _ => BooruResultView.List }, save: true);
        };
        _viewBar.Children.Add(_sizeGroup);
        _viewBar.Children.Add(_viewToggle);
        return _viewBar;
    }

    private void RenderResultViewBar(BooruSearchView view)
    {
        _viewBar.IsVisible = view.Rows.Any();
        _viewToggle.SetIconItem(0, "list", Texts.T("tagging.page.view.list"), Texts.T("booru.ui.view.listHint"));
        _viewToggle.SetIconItem(1, "grid", Texts.T("tagging.page.view.grid"), Texts.T("booru.ui.view.gridHint"));
        _viewToggle.SetIconItem(2, "image", Texts.T("booru.gallery.name"), Texts.T("booru.gallery.hint"));
        AutomationProperties.SetName(_viewToggle, Texts.T("tagging.page.view"));
        AutomationProperties.SetName(_sizeGroup, Texts.T("booru.ui.tile.sizeName"));
        Describe(_sizeSmall, "S", "media.layout.size.small");
        Describe(_sizeMedium, "M", "media.layout.size.medium");
        Describe(_sizeLarge, "L", "media.layout.size.large");
        _sizeGroup.IsVisible = _resultView == BooruResultView.Grid;
        SyncViewBar();

        // The letters S, M and L are the size marks themselves (the same in every language); the words are the tooltip and the accessible name.
        static void Describe(ToggleButton button, string letter, string nameKey)
        {
            button.Content = letter;
            AutomationProperties.SetName(button, Texts.T(nameKey));
            ToolTip.SetTip(button, Texts.T(nameKey));
        }
    }

    private void SyncViewBar()
    {
        _syncingView = true;
        try
        {
            _viewToggle.SelectedIndex = _resultView switch { BooruResultView.Grid => 1, BooruResultView.Gallery => 2, _ => 0 };
            _sizeSmall.IsChecked = _tileSize == MediaTileSize.Small;
            _sizeMedium.IsChecked = _tileSize == MediaTileSize.Medium;
            _sizeLarge.IsChecked = _tileSize == MediaTileSize.Large;
        }
        finally { _syncingView = false; }
    }

    /// <summary>Builds the hits of <paramref name="view"/> into the results panel: the cards of the list, or the one tile panel.</summary>
    private void RebuildResults(BooruSearchView view)
    {
        if (_resultView == BooruResultView.Gallery && view.Rows.Any())
        {
            RebuildGallery(view);
            return;
        }
        ReleaseGallery();
        if (_resultView != BooruResultView.Grid || !view.Rows.Any())
        {
            _tiles = null;
            RebuildPanel(_results, () => NumberHits(view.Rows.Select(CreateResultCard).ToList()), fallback: () => null);
            ApplyCursor();
            return;
        }
        BooruTilePanel tiles = new() { Name = "BooruTiles", ColumnWidth = BooruTileMetrics.CellWidth(_tileSize) };
        // The size keys and Ctrl+wheel answer only over these tiles (the handlers sit on the panel, which is made again with every rebuild).
        TileSizeInput.Install(tiles, TileSizeCommands.Media, () => _tileSize, ChooseTileSize);
        AutomationProperties.SetName(tiles, Texts.T("booru.ui.tile.listName"));
        AutomationProperties.SetControlTypeOverride(tiles, Avalonia.Automation.Peers.AutomationControlType.List);
        _tiles = tiles;
        RebuildPanel(_results, () =>
        {
            foreach (Control tile in NumberHits(view.Rows.Select(CreateResultTile).ToList())) tiles.Children.Add(tile);
            return new List<Control> { tiles };
        }, fallback: () => null);
        ApplyCursor();
    }

    /// <summary>
    /// One tile. It is a keyboard stop like a card (the same name, Enter, double click, middle click and context menu through
    /// <see cref="ConfigureHitCard"/>). The actions of the hit that the tile does not show (download or cancel or retry, show in Media, open and
    /// copy the post link) stay in the hit's own card, built by <see cref="CreateResultCard"/> and kept hidden inside the tile: the context menu
    /// and the middle click find their buttons there, so a tile can do exactly what its card does and never decides anything on its own.
    /// </summary>
    private Control CreateResultTile(BooruResultRowView row)
    {
        BooruPostView post = row.Post;
        string postId = post.PostId;
        double picture = BooruTileMetrics.PictureSize(_tileSize);

        Control card = CreateResultCard(row);
        card.IsVisible = false;
        // The card's own gallery and selection controls would be a second pair with the same names; the tile shows the one pair below.
        if (card.GetVisualDescendants().OfType<Control>().FirstOrDefault(control => control.Name == "BooruViewerActions:" + postId) is { Parent: Panel owner } viewerActions)
            owner.Children.Remove(viewerActions);
        Button? showInMedia = ButtonOf(card, ShowInMediaPrefix + postId);

        Panel frame = new() { Width = picture, Height = picture, HorizontalAlignment = HorizontalAlignment.Center };
        frame.Children.Add(WithPreviewCorners(CreatePreview(row, picture), post, picture));
        // Bottom right: the original is saved locally (the same fact as the card's "Show in Media" button).
        Border local = WorkspaceChips.CreateCornerMark("BooruResultLocal:" + postId, HorizontalAlignment.Right, VerticalAlignment.Bottom);
        local.Margin = new Thickness(0, 0, 4, 4);
        WorkspaceChips.SetCornerMark(local, showInMedia != null ? BooruTileMetrics.LocalMark : null, Texts.T("booru.ui.tile.local"));
        frame.Children.Add(local);

        TextBlock line = new()
        {
            Name = "BooruTileLine:" + postId, Text = BooruTileMetrics.Line(postId, post.Rating, post.Score),
            Classes = { "workspace-meta", "tnum" }, TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis, HorizontalAlignment = HorizontalAlignment.Center
        };
        AutomationProperties.SetAccessibilityView(line, AccessibilityView.Raw);

        // As on the media page: a selected tile shows the accent check on its picture and the 3-px accent bar at its start edge (never the tint
        // alone). The CheckBox stays the one control that changes the selection; the marks only show it.
        bool selected = _selectedResults.Contains(postId);
        Border check = WorkspaceChips.CreateSelectionCheck("BooruTileSelected:" + postId);
        check.HorizontalAlignment = HorizontalAlignment.Right;
        check.Margin = new Thickness(0, 4, 4, 0);
        WorkspaceChips.ShowSelection(check, selected);
        frame.Children.Add(check);

        // The actions are quiet: shown while the pointer is over the tile, the keyboard focus is inside it, or the hit is selected. They are only
        // transparent otherwise (not hidden), so Tab and the screen reader still reach them.
        Control actions = CreateResultViewerActions(postId);
        actions.Classes.Add("booru-tile-actions");

        StackPanel body = new() { Spacing = 8 };
        body.Children.Add(frame);
        body.Children.Add(line);
        body.Children.Add(actions);
        body.Children.Add(card);

        Border bar = new()
        {
            Name = "BooruTileBar:" + postId, Classes = { "booru-tile-bar" }, Width = BooruTileMetrics.SelectionBar, IsVisible = selected,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Stretch, IsHitTestVisible = false,
            Margin = new Thickness(-(BooruTileMetrics.Padding + BooruTileMetrics.Border), -(BooruTileMetrics.Padding + BooruTileMetrics.Border), 0, -(BooruTileMetrics.Padding + BooruTileMetrics.Border))
        };
        AutomationProperties.SetAccessibilityView(bar, AccessibilityView.Raw);
        Panel content = new();
        content.Children.Add(body);
        content.Children.Add(bar);

        Border tile = new() { Name = "BooruResult:" + postId, Child = content, Padding = new Thickness(BooruTileMetrics.Padding) };
        tile.Classes.Add("booru-tile");
        tile.Classes.Set("booru-tile-selected", selected);
        ToolTip.SetTip(tile, BooruText.Rating(post.Rating));
        AttachCardMenu(tile, () => ComposeResultMenu(tile, postId), () => FocusHit(postId));
        ConfigureHitCard(tile, row, showInMedia);
        return tile;
    }
}
