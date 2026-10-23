using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Ursa.Controls;

namespace MeowChan.Desktop;

/// <summary>
/// Fixed archive cells with Ursa's realization/recycling. The overrides work around
/// Ursa 2.2.0's pending-scroll flag, depth-dependent arrange and mutation indices.
/// </summary>
public sealed class MediaTilePanel : VirtualizingUniformGrid
{
    public const double CellWidth = 190;
    public const double CellHeight = 210;
    private bool _inLayout;
    private bool _scrolling;
    private Rect _viewport;
    private (int Index, double Offset)? _resizeAnchor;
    private int _restoreGeneration;
    private bool _resizeRestorePosted;
    private ItemsControl? _inputOwner;

    public MediaTilePanel()
    {
        ItemWidth = CellWidth;
        ItemHeight = CellHeight;
        UniformItemHeight = true;
        CacheLength = .5;
        AddHandler(KeyDownEvent, OnPageKeyDown, RoutingStrategies.Bubble);
        LayoutUpdated += OnLayoutUpdated;
        EffectiveViewportChanged += (_, e) =>
        {
            _viewport = e.EffectiveViewport;
            ViewportChanged?.Invoke(this, EventArgs.Empty);
        };
    }

    public event EventHandler? ViewportChanged;

    private void OnPageKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || e.Key is not Key.PageUp and not Key.PageDown || ItemsControl is not ListBox list) return;
        // ScrollViewer consumes Page keys while bubbling from an item, before
        // ListBox can move selection. Forward to the owning ListBox directly so
        // its native range/selection/focus logic calls our GetControl override.
        KeyEventArgs forwarded = new()
        {
            RoutedEvent = KeyDownEvent,
            Key = e.Key,
            KeyModifiers = e.KeyModifiers
        };
        list.RaiseEvent(forwarded);
        e.Handled = forwarded.Handled;
    }


    protected override Size MeasureOverride(Size availableSize)
    {
        _inLayout = true;
        try
        {
            // The presenter has already removed padding, borders and the vertical
            // scrollbar from this constraint. Never use the outer ListBox width.
            if (double.IsFinite(availableSize.Width))
            {
                double width = Math.Max(1, availableSize.Width);
                int columns = Math.Max(1, (int)Math.Floor(width / CellWidth));
                if (!_scrolling && Columns != columns && _resizeAnchor is null && CaptureAnchor() is { } anchor)
                {
                    _resizeAnchor = anchor;
                    ++_restoreGeneration;
                }
                if (Columns != columns) SetCurrentValue(ColumnsProperty, columns);
                double cellWidth = Math.Min(CellWidth, width);
                if (ItemWidth != cellWidth) SetCurrentValue(ItemWidthProperty, cellWidth);
            }
            return base.MeasureOverride(availableSize);
        }
        finally { _inLayout = false; }
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _inLayout = true;
        try
        {
            IScrollAnchorProvider? anchors = this.FindAncestorOfType<IScrollAnchorProvider>();
            if (ItemsControl is null)
            {
                for (int i = 0; i < Children.Count; i++) Children[i].Arrange(CellRect(i));
            }
            else if (FirstRealizedIndex >= 0)
            {
                // Only enumerate realized containers; absolute index arithmetic
                // keeps arrange independent of the number of preceding rows.
                foreach (Control child in GetRealizedContainers())
                {
                    int index = IndexFromContainer(child);
                    if (index < 0) continue;
                    Rect rect = CellRect(index);
                    child.Arrange(rect);
                    if (child.IsVisible && _viewport.Intersects(rect)) anchors?.RegisterAnchorCandidate(child);
                }
            }
            return finalSize;
        }
        finally
        {
            _inLayout = false;
            RaiseEvent(new RoutedEventArgs(VerticalSnapPointsChangedEvent));
        }
    }

    protected override void OnItemsChanged(IReadOnlyList<object?> items, NotifyCollectionChangedEventArgs e)
    {
        // Ursa's incremental bookkeeping does not shift its realized first index.
        // Reset preserves the owning ListBox selection while recycling containers.
        CancelResizeRestore();
        base.OnItemsChanged(items, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    private bool IsExtentReady(ScrollContentPresenter presenter) =>
        IsMeasureValid && IsArrangeValid && presenter.IsArrangeValid
        && presenter.Extent.Height + .1 >= DesiredSize.Height;

    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        if (_resizeAnchor is null || _resizeRestorePosted || _inLayout || _scrolling) return;
        ScrollContentPresenter? presenter = this.FindAncestorOfType<ScrollContentPresenter>();
        // Measure can finish before the presenter publishes its new extent.
        // Clamping a restored offset to the previous extent loses deep anchors
        // when a narrower grid adds rows. Wait for completed ancestor layout.
        if (presenter is null || !IsExtentReady(presenter)) return;
        _resizeRestorePosted = true;
        int generation = _restoreGeneration;
        Dispatcher.UIThread.Post(() =>
        {
            if (generation != _restoreGeneration) return;
            _resizeRestorePosted = false;
            if (_resizeAnchor is not { } restore) return;
            _resizeAnchor = null;
            RestoreAnchor(restore.Index, restore.Offset);
        }, DispatcherPriority.Background);
    }

    private void CancelResizeRestore()
    {
        ++_restoreGeneration;
        _resizeAnchor = null;
        _resizeRestorePosted = false;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _inputOwner = ItemsControl;
        // The scrollbar is outside the panel but inside its owning ListBox.
        // Cancel queued restoration before fresh user scroll/selection input.
        _inputOwner?.AddHandler(PointerPressedEvent, OnUserInput, RoutingStrategies.Tunnel);
        _inputOwner?.AddHandler(PointerReleasedEvent, OnUserInput, RoutingStrategies.Tunnel);
        _inputOwner?.AddHandler(PointerMovedEvent, OnPointerMoved, RoutingStrategies.Tunnel);
        _inputOwner?.AddHandler(PointerWheelChangedEvent, OnUserInput, RoutingStrategies.Tunnel);
        _inputOwner?.AddHandler(KeyDownEvent, OnUserInput, RoutingStrategies.Tunnel);
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_resizeAnchor is not null && _inputOwner is { } owner
            && e.GetCurrentPoint(owner).Properties.IsLeftButtonPressed)
            CancelResizeRestore();
    }

    private void OnUserInput(object? sender, RoutedEventArgs e)
    {
        if (_resizeAnchor is not null) CancelResizeRestore();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _inputOwner?.RemoveHandler(PointerPressedEvent, OnUserInput);
        _inputOwner?.RemoveHandler(PointerReleasedEvent, OnUserInput);
        _inputOwner?.RemoveHandler(PointerMovedEvent, OnPointerMoved);
        _inputOwner?.RemoveHandler(PointerWheelChangedEvent, OnUserInput);
        _inputOwner?.RemoveHandler(KeyDownEvent, OnUserInput);
        _inputOwner = null;
        CancelResizeRestore();
        base.OnDetachedFromVisualTree(e);
    }

    private Rect CellRect(int index) => new(index % Columns * ItemWidth, index / Columns * CellHeight, ItemWidth, CellHeight);

    protected override Control? ScrollIntoView(int index)
    {
        if (_inLayout || _scrolling || index < 0 || index >= Items.Count || !IsEffectivelyVisible) return null;
        CancelResizeRestore();
        _scrolling = true;
        try
        {
            this.UpdateLayout();
            ScrollContentPresenter? presenter = this.FindAncestorOfType<ScrollContentPresenter>();
            // Avalonia transforms the panel rectangle into content coordinates
            // (including its origin/padding), minimally scrolls and clamps offset.
            presenter?.BringDescendantIntoView(this, CellRect(index));
            // Native ListBox navigation needs the generated target immediately,
            // before GetControl returns. Layout delivers the effective viewport
            // change and the following measure realizes its buffered row range.
            this.UpdateLayout();
            return ContainerFromIndex(index);
        }
        finally { _scrolling = false; }
    }

    public (int Index, double Offset)? CaptureAnchor()
    {
        if (Items.Count == 0 || _viewport.Height <= 0) return null;
        int row = Math.Max(0, (int)Math.Floor(Math.Max(0, _viewport.Top) / CellHeight));
        int index = Math.Min(Items.Count - 1, row * Columns);
        return (index, index / Columns * CellHeight - _viewport.Top);
    }

    public void RestoreAnchor(int index, double offset)
    {
        if (_scrolling || Items.Count == 0) return;
        CancelResizeRestore();
        if (_inLayout)
        {
            _resizeAnchor = (index, offset);
            return;
        }
        _scrolling = true;
        try
        {
            this.UpdateLayout();
            ScrollContentPresenter? presenter = this.FindAncestorOfType<ScrollContentPresenter>();
            if (presenter is null) return;
            if (!IsExtentReady(presenter))
            {
                _resizeAnchor = (index, offset);
                return;
            }
            Point? origin = this.TranslatePoint(default, presenter);
            if (origin is null) return;
            double target = origin.Value.Y + presenter.Offset.Y
                + Math.Clamp(index, 0, Items.Count - 1) / Columns * CellHeight - offset;
            presenter.SetCurrentValue(ScrollContentPresenter.OffsetProperty,
                new Vector(presenter.Offset.X, Math.Clamp(target, 0, Math.Max(0, presenter.Extent.Height - presenter.Viewport.Height))));
            this.UpdateLayout();
        }
        finally { _scrolling = false; }
    }

    protected override IInputElement? GetControl(NavigationDirection direction, IInputElement? from, bool wrap)
    {
        int count = Items.Count;
        if (count == 0) return null;
        int fromIndex = -1;
        for (Visual? visual = from as Visual; visual is not null && visual != this; visual = visual.GetVisualParent())
            if (visual is Control control && (fromIndex = IndexFromContainer(control)) >= 0) break;
        if (fromIndex < 0 && direction is not NavigationDirection.First and not NavigationDirection.Last) return null;
        int pageRows = Math.Max(1, (int)Math.Floor(_viewport.Height / CellHeight));
        int lastRow = (count - 1) / Columns;
        int toIndex = direction switch
        {
            NavigationDirection.First => 0,
            NavigationDirection.Last => count - 1,
            NavigationDirection.Next => fromIndex + 1,
            NavigationDirection.Previous => fromIndex - 1,
            NavigationDirection.Left when fromIndex % Columns > 0 => fromIndex - 1,
            NavigationDirection.Right when fromIndex % Columns < Columns - 1 && fromIndex < count - 1 => fromIndex + 1,
            NavigationDirection.Up when fromIndex >= Columns => fromIndex - Columns,
            NavigationDirection.Down when fromIndex / Columns < lastRow => Math.Min(count - 1, fromIndex + Columns),
            NavigationDirection.PageUp => Math.Max(0, fromIndex - pageRows * Columns),
            NavigationDirection.PageDown => Math.Min(count - 1, fromIndex + pageRows * Columns),
            _ => -1
        };
        if (wrap && direction is NavigationDirection.Next or NavigationDirection.Previous)
            toIndex = (toIndex + count) % count;
        return toIndex >= 0 && toIndex < count ? ScrollIntoView(toIndex) : null;
    }
}
