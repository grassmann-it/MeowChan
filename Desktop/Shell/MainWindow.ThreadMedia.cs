using System;
using System.Collections.Generic;
using System.Linq;
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

    /// <summary>
    /// Shows the media of several threads on the Media page: the thread set becomes the only filter ("12 threads from Archive search" or
    /// "3 selected threads"). Unlike a single thread it may be empty (the page then shows no media and says so); entries without a thread number
    /// (boards) have no media of their own and are left out.
    /// </summary>
    internal void ShowThreadsInMedia(IReadOnlyList<ResourceId> threads, MediaThreadSetOrigin origin)
    {
        ArgumentNullException.ThrowIfNull(threads);
        if (_isClosing || !_restoreCompleted || WorkspaceCommandBusy) return;
        if (_largeReaderOpen && _gallery == null) CloseLargeReader(restoreOrigin: false);
        if (_largeReaderOpen || !NavigateWorkspace(WorkspacePage.Media) || _mediaPage is not { } page) return;
        page.ShowOnlyThreads(new MediaThreadSet(threads.Where(thread => thread.Thread != null), origin));
    }

    /// <summary>
    /// "Show in Media" of a list selection (the Downloads menu and Alt+M): one thread shows as the thread: chip (with <paramref name="leadTitle"/> when
    /// it is the lead), several as one thread set. The selection is read by the caller before it runs, so a later change does not move it.
    /// </summary>
    internal void ShowSelectedThreadsInMedia(IReadOnlyList<ResourceId> selected, ResourceId lead, string? leadTitle) =>
        MediaThreadTargets.Open(selected, lead, leadTitle, ShowThreadInMedia, threads => ShowThreadsInMedia(threads, MediaThreadSetOrigin.Selection));
}
