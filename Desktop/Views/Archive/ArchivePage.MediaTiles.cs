using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Interfaces;

namespace MeowChan;

public partial class ArchivePage
{
    private readonly record struct MediaIdentity(ResourceId? Thread, string Directory, string Asset);
    private sealed record MediaPosition(MediaIdentity[] Order, int Index, double Offset, MediaIdentity? Selection);
    private IReadOnlyList<ArchiveAssetRow> _shownMedia = Array.Empty<ArchiveAssetRow>();
    private ResourceId? _shownMediaThread;
    private MediaPosition? _retainedMediaPosition;
    private int _mediaPositionRequest;
    private bool _rebindingMedia;
    private bool _mediaProjectionPending;
    private bool _applyingMediaRestore;
    private MediaIdentity? _pendingMediaSelection;
    private int _pendingMediaSelectionRequest;

    // DataGrid can publish its initial SelectedItem event after applying its template.
    // An echo of this request's own selection must not cancel the deferred scroll.
    private bool IsPendingMediaSelectionEcho(object? sender)
    {
        if (_pendingMediaSelectionRequest != _mediaPositionRequest) return false;
        object? selected = sender is DataGrid ? Control<DataGrid>("ArchiveMediaGrid").SelectedItem : Control<ListBox>("ArchiveMediaTiles").SelectedItem;
        MediaIdentity? key = selected is ArchiveAssetRow row ? MediaKey(_shownMediaThread, row) : null;
        return key == _pendingMediaSelection;
    }

    private void InitializeMediaPositionInput()
    {
        foreach (Control control in new Control[] { Control<ListBox>("ArchiveMediaTiles"), Control<DataGrid>("ArchiveMediaGrid") })
        {
            control.AddHandler(InputElement.PointerWheelChangedEvent, OnMediaPositionInput, RoutingStrategies.Tunnel, handledEventsToo: true);
            control.AddHandler(InputElement.PointerPressedEvent, OnMediaPositionInput, RoutingStrategies.Tunnel, handledEventsToo: true);
            control.AddHandler(InputElement.PointerReleasedEvent, OnMediaPositionInput, RoutingStrategies.Tunnel, handledEventsToo: true);
            control.AddHandler(InputElement.KeyDownEvent, OnMediaPositionInput, RoutingStrategies.Tunnel, handledEventsToo: true);
            control.AddHandler(InputElement.PointerMovedEvent, (_, e) =>
            {
                if (e.GetCurrentPoint(control).Properties.IsLeftButtonPressed) ++_mediaPositionRequest;
            }, RoutingStrategies.Tunnel, handledEventsToo: true);
        }
    }

    private void OnMediaPositionInput(object? sender, RoutedEventArgs e)
    {
        ++_mediaPositionRequest;
    }

    private static string CanonicalMediaDirectory(string path)
    {
        string directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        return OperatingSystem.IsWindows() ? directory.ToUpperInvariant() : directory;
    }

    private static MediaIdentity MediaKey(ResourceId? thread, ArchiveAssetRow row)
        => new(thread, CanonicalMediaDirectory(row.Manifest.Directory), row.Asset.AssetId);

    private MediaTilePanel? TilePanel() => Control<ListBox>("ArchiveMediaTiles")
        .GetVisualDescendants().OfType<MediaTilePanel>().FirstOrDefault();

    private static double MediaHeaderHeight(DataGrid grid)
    {
        DataGridRowsPresenter? rows = grid.GetVisualDescendants().OfType<DataGridRowsPresenter>().FirstOrDefault();
        if (rows?.TranslatePoint(default, grid) is { } origin) return origin.Y;
        return grid.GetVisualDescendants().OfType<DataGridColumnHeader>()
            .Select(header => (header.TranslatePoint(default, grid)?.Y ?? 0) + header.Bounds.Height)
            .DefaultIfEmpty(0).Max();
    }

    private MediaPosition? CaptureMediaPosition()
    {
        if (_shownMedia.Count == 0)
            return _retainedMediaPosition?.Order.FirstOrDefault().Thread == _selectedThread ? _retainedMediaPosition : null;
        if (_shownMediaThread != _selectedThread) return null;
        int index = 0;
        double offset = 0;
        if (_mediaTiles && TilePanel()?.CaptureAnchor() is { } tile)
        {
            index = Math.Clamp(tile.Index, 0, _shownMedia.Count - 1);
            offset = -tile.Offset;
        }
        else if (!_mediaTiles)
        {
            DataGrid grid = Control<DataGrid>("ArchiveMediaGrid");
            var first = grid.GetVisualDescendants().OfType<DataGridRow>()
                .Where(row => row.IsVisible && row.DataContext is ArchiveAssetRow)
                .Select(row => (Row: row, Point: row.TranslatePoint(default, grid)))
                .Where(row => row.Point.HasValue && row.Point.Value.Y + row.Row.Bounds.Height > MediaHeaderHeight(grid))
                .OrderBy(row => row.Point!.Value.Y).FirstOrDefault();
            if (first.Row?.DataContext is ArchiveAssetRow asset)
            {
                index = _shownMedia.ToList().IndexOf(asset);
                offset = Math.Max(0, MediaHeaderHeight(grid) - first.Point!.Value.Y);
            }
        }
        MediaIdentity? selection = SelectedMediaRow() is { } selected ? MediaKey(_shownMediaThread, selected) : null;
        return new MediaPosition(_shownMedia.Select(row => MediaKey(_shownMediaThread, row)).ToArray(), Math.Max(0, index), offset, selection);
    }

    private void RestoreMediaPosition(MediaPosition? position)
    {
        int request = ++_mediaPositionRequest;
        if (position == null || _shownMedia.Count == 0) return;
        Dictionary<MediaIdentity, int> indices = new();
        for (int index = 0; index < _shownMedia.Count; index++)
            indices.TryAdd(MediaKey(_shownMediaThread, _shownMedia[index]), index);
        int target = 0;
        bool retained = false;
        for (int old = position.Index; old < position.Order.Length; old++)
            if (indices.TryGetValue(position.Order[old], out target)) { retained = true; break; }
        double retainedOffset = retained ? position.Offset : 0;
        target = Math.Clamp(target, 0, _shownMedia.Count - 1);
        int selectedIndex = position.Selection is { } selected && indices.TryGetValue(selected, out int found) ? found : -1;
        _pendingMediaSelection = selectedIndex >= 0 ? MediaKey(_shownMediaThread, _shownMedia[selectedIndex]) : null;
        _pendingMediaSelectionRequest = request;
        _rebindingMedia = true;
        try
        {
            object? selection = selectedIndex >= 0 ? _shownMedia[selectedIndex] : null;
            Control<DataGrid>("ArchiveMediaGrid").SelectedItem = selection;
            Control<ListBox>("ArchiveMediaTiles").SelectedItem = selection;
        }
        finally { _rebindingMedia = false; }
        Dispatcher.UIThread.Post(() =>
        {
            if (_ended || request != _mediaPositionRequest || _shownMedia.Count == 0) return;
            _applyingMediaRestore = true;
            try
            {
                if (_mediaTiles) TilePanel()?.RestoreAnchor(target, -Math.Min(retainedOffset, MediaTilePanel.CellHeight - 1));
                else
                {
                    DataGrid grid = Control<DataGrid>("ArchiveMediaGrid");
                    grid.ScrollIntoView(_shownMedia[target], null);
                    grid.UpdateLayout();
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (_ended || request != _mediaPositionRequest) return;
                        _applyingMediaRestore = true;
                        try
                        {
                            DataGridRow? row = grid.GetVisualDescendants().OfType<DataGridRow>()
                                .FirstOrDefault(row => ReferenceEquals(row.DataContext, _shownMedia[target]));
                            ScrollBar? scroll = grid.GetVisualDescendants().OfType<ScrollBar>()
                                .FirstOrDefault(scroll => scroll.Name == "PART_VerticalScrollbar");
                            if (row?.TranslatePoint(default, grid) is not { } point || scroll == null) return;
                            double delta = point.Y - MediaHeaderHeight(grid) + Math.Min(retainedOffset, Math.Max(0, row.Bounds.Height - 1));
                            // DataGrid owns a pixel scrollbar rather than a ScrollViewer.
                            // Public PageUp/Down forwards Scroll to its native row presenter;
                            // a temporary page size lets that presenter apply the exact delta.
                            double page = scroll.LargeChange;
                            try
                            {
                                scroll.SetCurrentValue(ScrollBar.LargeChangeProperty, Math.Abs(delta));
                                if (delta > 0) scroll.PageDown(); else if (delta < 0) scroll.PageUp();
                            }
                            finally { scroll.SetCurrentValue(ScrollBar.LargeChangeProperty, page); }
                            grid.UpdateLayout();
                        }
                        finally { _applyingMediaRestore = false; }
                    }, DispatcherPriority.Loaded);
                }
            }
            finally { _applyingMediaRestore = false; }
        }, DispatcherPriority.Loaded);
    }
}
