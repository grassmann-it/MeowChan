using System;
using MeowChan.Desktop;

namespace MeowChan;

// QW-A #7 (review of 15.11.2026): "Show in Media" of the thread menus of Downloads, Archive and the reader. One shell path for all three:
// it leaves the large reader (the page behind it is not reachable from there) and opens the Media page with the thread: chip as the only filter.
public partial class MainWindow
{
    /// <summary>
    /// Shows the media of one thread on the Media page. <paramref name="title"/> names the chip; without one the chip says "#number".
    /// A thread without a number (a board) has no media to show here and is ignored.
    /// </summary>
    internal void ShowThreadInMedia(ResourceId thread, string? title)
    {
        ArgumentNullException.ThrowIfNull(thread);
        if (_isClosing || !_restoreCompleted || WorkspaceCommandBusy || thread.Thread == null) return;
        // From the large reader (its "Thread" menu) the Media page behind it is not reachable: leave the reader first, without going back to its origin.
        if (_largeReaderOpen && _gallery == null) CloseLargeReader(restoreOrigin: false);
        if (_largeReaderOpen || !NavigateWorkspace(WorkspacePage.Media) || _mediaPage is not { } page) return;
        page.ShowOnlyThread(thread, title ?? string.Empty);
    }
}
