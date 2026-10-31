using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace MeowChan.Desktop;

// LibVLCSharp 3.10.2 assigns a fresh, untransformed RectangleGeometry to its
// content's Clip on every owner LayoutUpdated, including identical rectangles.
// Avalonia 12.1.4's removed focus adorners retain ancestor-clip subscriptions;
// after a control crosses roots this can invalidate the old root indefinitely.
// This private, single-writer host makes that specific native clip projection
// idempotent. It neither changes the clipping area nor disables focus adorners.
internal sealed class NativeVideoOverlayRoot : Grid
{
    static NativeVideoOverlayRoot() => ClipProperty.OverrideMetadata<NativeVideoOverlayRoot>(
        new StyledPropertyMetadata<Geometry?>(coerce: (owner, value) => ((NativeVideoOverlayRoot)owner).CoerceNativeClip(value)));

    private Geometry? CoerceNativeClip(Geometry? value)
    {
        // Only the immutable-per-assignment rectangles produced by VideoView are
        // canonicalized. Transformed/non-rectangular clips retain their identity.
        if (value is not RectangleGeometry { Transform: null } rectangle)
            return value;
        // GetValue reads the current effective value; coercion runs before the
        // incoming value is published. Do not keep a potentially stale cache.
        if (Clip is RectangleGeometry { Transform: null } previous && previous.Rect == rectangle.Rect
            && previous.RadiusX == rectangle.RadiusX && previous.RadiusY == rectangle.RadiusY)
            return previous;
        return rectangle;
    }
}
