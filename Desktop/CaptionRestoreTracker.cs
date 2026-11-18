using Avalonia;
using Avalonia.Controls;

namespace MeowChan.Desktop;

// Decision logic that keeps a secondary window from staying at its maximized size after a caption
// restore. X11 acknowledges WindowState.Normal before the window manager's ConfigureNotify has been
// drained, and the old maximum can be reported again afterwards (once as Width/Height, once as
// a layout pass that resizes the native window to it). MainWindow has the same guard with its own
// workspace-mode rules; this one only knows sizes, so it is testable without a window.
internal sealed class CaptionRestoreTracker
{
    private Size? _normal;
    private Size _maximized;
    private bool _pending;

    internal bool Pending => _pending;

    /// <summary>A caption maximize is about to start from the current normal size.</summary>
    internal void BeforeMaximize(Size current)
    {
        // A queued stale maximum must not replace the real normal snapshot.
        if (!_pending || current != _maximized) _normal = current;
    }

    /// <summary>A caption restore is about to start from the current maximized size.</summary>
    internal void BeforeRestore(Size current)
    {
        _maximized = current;
        _pending = _normal.HasValue;
    }

    /// <summary>A real state transition ended the watch for a stale maximum, unless it is the acknowledgement of Normal.</summary>
    internal void StateChanged(WindowState state)
    {
        if (state != WindowState.Normal) _pending = false;
    }

    /// <summary>
    /// The size to apply now when the window is Normal but still (or again) reports the maximized size;
    /// otherwise null. An unrelated resize ends the watch, the restored size itself keeps watching.
    /// </summary>
    internal Size? StaleMaximum(WindowState state, Size bounds, double width, double height)
    {
        if (!_pending || state != WindowState.Normal || _normal is not Size normal) return null;
        if ((bounds != normal && bounds != _maximized) || !Reports(width, normal.Width, _maximized.Width)
            || !Reports(height, normal.Height, _maximized.Height))
        {
            _pending = false;
            return null;
        }
        return bounds == _maximized && width == _maximized.Width && height == _maximized.Height ? normal : null;
    }

    private static bool Reports(double value, double normal, double maximized) => value == normal || value == maximized;
}
