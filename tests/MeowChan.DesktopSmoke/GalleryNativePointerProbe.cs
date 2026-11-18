using Avalonia;

namespace MeowChan.DesktopSmoke;

// Geometry shared by the closing-root native probe and its focused tests.
internal static class GalleryNativePointerProbe
{
    internal static Point[] EntryPoints(Size size)
    {
        if (!double.IsFinite(size.Width) || !double.IsFinite(size.Height) || size.Width < 20 || size.Height <= 0)
            throw new ArgumentException("Closing-root target has no distinct pointer entry geometry.", nameof(size));
        Point center = new(size.Width / 2, size.Height / 2);
        // More than twice the acknowledgement tolerance: delivery at one point
        // must never acknowledge motion requested at the other.
        return [new Point(center.X - Math.Min(8, size.Width / 4), center.Y), center];
    }

    internal static bool IsAt(Point actual, Point expected) =>
        Math.Abs(actual.X - expected.X) <= 2 && Math.Abs(actual.Y - expected.Y) <= 2;
}
