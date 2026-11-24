using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace MeowChan.Desktop.Controls;

internal static class ViewportToolTip
{
    public static readonly CustomPopupPlacementCallback Place = PlaceInViewport;

    private static void PlaceInViewport(CustomPopupPlacement placement)
    {
        if (placement.Target is not Control target
            || target.FindAncestorOfType<ScrollContentPresenter>() is not { } viewport
            || viewport.TranslatePoint(default, target) is not { } origin)
            return;

        Rect area = new(origin, viewport.Bounds.Size);
        Rect visibleTarget = area.Intersect(new Rect(target.Bounds.Size));
        Size size = placement.PopupSize;
        if (visibleTarget.Width <= 0 || visibleTarget.Height <= 0 || size.Width > area.Width || size.Height > area.Height)
        {
            // The popup is in the middle of positioning. Close it after that call returns.
            Dispatcher.UIThread.Post(() => ToolTip.SetIsOpen(target, false), DispatcherPriority.Render);
            return;
        }

        double x = Math.Clamp(visibleTarget.Center.X - size.Width / 2, area.Left, area.Right - size.Width);
        double y = visibleTarget.Bottom + size.Height <= area.Bottom ? visibleTarget.Bottom : visibleTarget.Top - size.Height;
        // Avalonia has already translated the anchor rectangle into the popup parent's coordinates.
        double popupCenter = x + placement.Deflate.Left + size.Deflate(placement.Deflate).Width / 2;
        placement.Anchor = PopupAnchor.Top;
        placement.Gravity = PopupGravity.Bottom;
        placement.Offset = new Point(popupCenter - placement.AnchorRectangle.Width / 2,
            Math.Clamp(y, area.Top, area.Bottom - size.Height) + placement.Deflate.Top);
    }
}
