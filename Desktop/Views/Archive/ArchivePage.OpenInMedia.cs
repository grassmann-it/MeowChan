using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;

namespace MeowChan;

// "Open in Media" of the archive: from what the Archive search found to the Media page, which then shows the media of exactly those
// threads (the thread set chip, MediaFilterField.Threads). Two entry points share one handoff: the results bar's action (every thread the
// search finds, all pages) and a selection in the list ("Show in Media", Alt+M). The existing "Media from matching threads" action stays:
// it opens the gallery viewer over the same threads, this one opens the Media page.
public partial class ArchivePage
{
    private bool _openingResultsInMedia;

    /// <summary>The shell opens the Media page with a thread set (see <see cref="MainWindow.ShowThreadsInMedia"/>).</summary>
    internal Action<IReadOnlyList<ResourceId>, MediaThreadSetOrigin>? ShowThreadsInMediaRequested { get; set; }

    private bool CanOpenResultsInMedia => ResultActionsReady && _listTotal > 0 && _openReader != null && ShowThreadsInMediaRequested != null
        && !_openingResultsInMedia;

    private void UpdateResultsOpenInMediaAction() => Control<Button>("ArchiveResultsOpenInMedia").IsEnabled = CanOpenResultsInMedia;

    private async void OnResultsOpenInMediaClick(object? sender, RoutedEventArgs e) =>
        await OpenActions.RunAsync(OpenResultsInMediaAsync, "archive.results.media");

    /// <summary>
    /// Opens the Media page filtered to every thread of the applied search, not only the rows of the visible page: the applied query and
    /// the work filter are frozen at the click, every page is read by the query (<see cref="CollectResultThreadsAsync"/>), and the shell gets
    /// the thread ids. A search that no longer finds anything leaves the page where it is and says so.
    /// </summary>
    internal async Task OpenResultsInMediaAsync()
    {
        if (!CanOpenResultsInMedia || _openReader is not { } openReader || ShowThreadsInMediaRequested is not { } show) return;
        ArchiveSearchRequest query = _appliedSearch ?? ArchiveSearchRequest.Create(_query);
        // Work membership is evaluated now (not later) from the session's current work state, like the export of all results.
        IReadOnlySet<string>? eligibleUrls = CaptureExportWorkUrls(_workFilter, GetWorkSnapshots?.Invoke() ?? Array.Empty<ThreadWorkSnapshot>());
        _openingResultsInMedia = true;
        UpdateResultsOpenInMediaAction();
        try
        {
            ResourceId[] threads = await CollectResultThreadsAsync(openReader, query, eligibleUrls, _lifetime.Token);
            if (_ended || _lifetime.IsCancellationRequested) return;
            if (threads.Length == 0) { Message(Texts.T("archive.noResults")); return; }
            show(threads, MediaThreadSetOrigin.ArchiveSearch);
        }
        finally
        {
            _openingResultsInMedia = false;
            if (!_ended) UpdateResultsOpenInMediaAction();
        }
    }

    /// <summary>
    /// Every thread of a search, over all its pages, in the order of the query: the archive query is asked, never the 50 rows on screen.
    /// <paramref name="eligibleUrls"/> is the frozen work membership (<see cref="CaptureExportWorkUrls"/>; null: no work filter).
    /// </summary>
    internal static async Task<ResourceId[]> CollectResultThreadsAsync(Func<CancellationToken, Task<ArchiveReader>> openReader,
        ArchiveSearchRequest query, IReadOnlySet<string>? eligibleUrls, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(openReader);
        ArgumentNullException.ThrowIfNull(query);
        await using ArchiveReader reader = await openReader(token);
        return await ResolveExportResultIdsAsync(reader, query, eligibleUrls, token);
    }

    /// <summary>
    /// "Show in Media" for the threads selected in the list (the menu entry and Alt+M): one thread keeps the thread: chip, several open as
    /// one thread set. The selection is the frozen one of the menu or the key press; it never reaches beyond the rows of the page.
    /// </summary>
    private void ShowArchiveSelectionInMedia(IReadOnlyList<ResourceId> selection, ArchiveThreadRow lead) =>
        MediaThreadTargets.Open(selection, lead.Summary.Id, lead.Summary.Title,
            (thread, title) => ShowThreadInMediaRequested?.Invoke(thread, title),
            threads => ShowThreadsInMediaRequested?.Invoke(threads, MediaThreadSetOrigin.Selection));
}
