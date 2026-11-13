using System;
using Avalonia;
using Avalonia.Controls;

namespace MeowChan.Desktop;

internal readonly record struct ReaderNavigationLayout(Thickness PostMargin, double TailInset)
{
    internal const double NavigationSize = 108;
    internal const double SideReserve = NavigationSize + 24 + 12;
    internal const double OverlayTailInset = NavigationSize + 12;
    // The post list has no maximum width (cards use the whole surface; only a post without media keeps its reading
    // width, inside the card). The free lane for the cross exists while the cards keep at least this much.
    internal const double MinimumCardWidth = 760;
    // Post width "fit to content": a card is as wide as its content needs, up to this reading width. The same number
    // is the MaxWidth setter of the "ListBox.reader-fit-width ListBoxItem" style in ArchivePage.axaml (a test pins both).
    internal const double CardMaxWidth = 1120;
    // Conversation indentation is local to a card. Keeping it below 40% of the readable width prevents the
    // deepest tree from consuming the text column or the navigation lane at 900/1440 DIP.
    internal const double ConversationIndentStep = 20;
    internal const double ConversationIndentMaximumShare = 0.4;

    internal static double ConversationIndent(int depth, double availableWidth, double cardMaxWidth = CardMaxWidth)
    {
        int level = Math.Clamp(depth, 0, 16);
        if (!double.IsFinite(cardMaxWidth) || cardMaxWidth <= 0) cardMaxWidth = CardMaxWidth;
        double readable = double.IsFinite(availableWidth) && availableWidth > 0 ? availableWidth : cardMaxWidth;
        readable = Math.Min(readable, cardMaxWidth);
        return Math.Min(level * ConversationIndentStep, readable * ConversationIndentMaximumShare);
    }

    internal double RightInset(double surfaceWidth, double postRight) => TailInset > 0
        && double.IsFinite(surfaceWidth) && double.IsFinite(postRight)
        ? Math.Max(24, surfaceWidth - postRight + 24) : 24;

    internal static ReaderNavigationLayout Resolve(bool largeReader, double surfaceWidth, double postMaxWidth)
    {
        if (!largeReader) return default;
        // A genuinely free wide-screen lane stays outside the text. A narrow reader
        // overlays the cross instead of subtracting a permanent full-width footer.
        bool sideSpace = double.IsFinite(surfaceWidth) && double.IsFinite(postMaxWidth)
            && surfaceWidth >= postMaxWidth + SideReserve;
        return sideSpace ? new(new Thickness(0, 0, SideReserve, 0), 0)
            : new(default, OverlayTailInset);
    }
}

/// <summary>
/// Extra scroll extent belongs only to the final realized post, never the viewport.
/// Restore a recycled container before it represents another item. No sentinel row,
/// collection mutation, custom scrolling implementation or disabled virtualization.
/// </summary>
internal sealed class ReaderPostTailSpace
{
    private Control? _container;
    private Thickness _originalMargin;
    private double _inset;

    internal void Update(Control? container, double inset)
    {
        inset = double.IsFinite(inset) ? Math.Max(0, inset) : 0;
        if (inset == 0) container = null;
        if (ReferenceEquals(container, _container) && inset == _inset) return;
        if (!ReferenceEquals(container, _container))
        {
            if (_container != null) _container.Margin = _originalMargin;
            _container = container;
            if (container != null) _originalMargin = container.Margin;
        }
        _inset = inset;
        if (_container != null)
            _container.Margin = new Thickness(_originalMargin.Left, _originalMargin.Top,
                _originalMargin.Right, _originalMargin.Bottom + inset);
    }

    internal void Release(Control container)
    {
        if (ReferenceEquals(container, _container)) Update(null, 0);
    }
}
