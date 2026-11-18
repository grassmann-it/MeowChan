using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Layout;
using Avalonia.VisualTree;

namespace MeowChan.Desktop;

// The reading line ("N new posts since your last visit") as a row of its own between two post cards, never inside one.
// The marked post's container reserves the room as extra top margin (ArchivePage.DecorateReaderPost); this zero-size host
// sits in that container and arranges its line into the reserved gap, from the list's left gutter to its right gutter
// (the full list width, not the width of the card). The geometry is read from the settled layout and only applied when it
// changed, so a resize or a recycled container converges within one more pass.
public sealed class ReaderUnreadLineHost : Panel
{
    // Height of the line itself and the air between it and the card above and below (8-dip grid).
    internal const double LineHeight = 24;
    internal const double Air = 8;
    // Where the line starts and ends inside the list: the cards' own side margin.
    internal const double Gutter = 8;

    // From the top edge of the marked card up to the top edge of the line.
    internal const double Reach = Air + LineHeight;

    // The extra top margin the marked card needs so that the gap to the card above is Air + line + Air, given the gap
    // two plain cards already have between them.
    internal static double ExtraTopMargin(double plainGap) => Math.Max(0, LineHeight + 2 * Air - plainGap);

    private Rect _slot;
    private bool _subscribed;

    static ReaderUnreadLineHost() => IsVisibleProperty.Changed.AddClassHandler<ReaderUnreadLineHost>((host, _) => host.Track());

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Track();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Unsubscribe();
        base.OnDetachedFromVisualTree(e);
    }

    // Only a visible line follows the layout: every other post's host stays out of the layout-updated event.
    private void Track()
    {
        if (IsVisible && TopLevel.GetTopLevel(this) != null)
        {
            if (!_subscribed) { LayoutUpdated += OnLayoutUpdated; _subscribed = true; }
            OnLayoutUpdated(this, EventArgs.Empty);
        }
        else
        {
            Unsubscribe();
        }
    }

    private void Unsubscribe()
    {
        if (_subscribed) { LayoutUpdated -= OnLayoutUpdated; _subscribed = false; }
        if (_slot != default) { _slot = default; InvalidateMeasure(); }
    }

    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        Rect slot = ComputeSlot(this);
        if (slot == _slot) return;
        _slot = slot;
        InvalidateMeasure();
    }

    // The line's rectangle in this host's own coordinates; empty while the layout is not settled or the host is no post.
    internal static Rect ComputeSlot(Visual host)
    {
        if (host.FindAncestorOfType<ListBoxItem>() is not { } item
            || host.FindAncestorOfType<ScrollContentPresenter>() is not { } viewport
            || host.TranslatePoint(default, viewport) is not { } origin
            || item.TranslatePoint(default, viewport) is not { } card) return default;
        double width = viewport.Bounds.Width - 2 * Gutter;
        if (width <= 0 || !double.IsFinite(width)) return default;
        return new Rect(Gutter - origin.X, card.Y - Reach - origin.Y, width, LineHeight);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (Control child in Children)
            child.Measure(_slot.Width > 0 ? _slot.Size : default);
        return default; // The line takes no room of its own: the marked card's margin holds it.
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        foreach (Control child in Children)
            child.Arrange(_slot.Width > 0 ? _slot : default);
        return default;
    }
}
