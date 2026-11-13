using System;
using Avalonia;
using Avalonia.Controls;

namespace MeowChan.Desktop;

/// <summary>
/// The arrow cross is always an overlay floating at the lower right of the reader surface. Content (post list, media
/// tiles and table, references) uses the whole width up to the right edge; there is no reserved lane. The only space the
/// cross asks for is extra scroll extent at the end of a list (<see cref="TailInset"/>), so the last item can be
/// scrolled out from under it.
/// </summary>
internal readonly record struct ReaderNavigationLayout(double TailInset)
{
    internal const double NavigationSize = 108;
    internal const double OverlayTailInset = NavigationSize + 12;
    // Distance of the cross from the right edge of the reader surface (it floats inside the list's scrollbar edge).
    internal const double RightInset = 24;
    // Post width "fit to content": a card is as wide as its content needs, up to this reading width. The same number
    // is the MaxWidth setter of the "ListBox.reader-fit-width ListBoxItem" style in ArchivePage.axaml (a test pins both).
    internal const double CardMaxWidth = 1120;

    internal static ReaderNavigationLayout Resolve(bool largeReader) => largeReader ? new(OverlayTailInset) : default;
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
