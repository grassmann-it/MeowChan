using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;

namespace MeowChan;

// The thread set of the Media page (MediaFilterField.Threads): the media of exactly the threads another page handed over, the results of
// an Archive search or the threads picked in a list. It is runtime state in the term set, so it shows as a chip ("12 threads from
// Archive search"), can be removed with the chip's mark, narrows together with every other filter and the in: scope, and has no query text.
public sealed partial class MediaPage
{
    /// <summary>
    /// How many thread titles the chip's tip lists before it says "+N more". A presentation limit of the tip only: the filter itself holds every
    /// thread of the set, however many (it is a set lookup), so this decides how much text a tip shows, not what is filtered.
    /// </summary>
    internal const int ThreadSetTipTitles = 10;

    // The title of every thread of the index, built once per snapshot when a tip first asks for it.
    private MediaLibrarySnapshot? _threadTitlesFor;
    private Dictionary<ResourceId, string>? _threadTitles;
    private ResourceId? _mediaCountThread;
    private MediaLibrarySnapshot? _mediaCountIndex;
    private ThreadMediaPositions? _threadMedia;
    private CancellationTokenSource? _mediaCountRequest;
    private Task _mediaCountTask = Task.CompletedTask;

    private void RefreshThreadMedia(MediaLibraryFilter? filter)
    {
        ResourceId? thread = filter?.Thread;
        if (filter?.Threads is { } threads)
            thread = thread != null ? (threads.Contains(thread) ? thread : null) : threads.Count == 1 ? threads.First() : null;
        if (thread == _mediaCountThread && (thread == null || ReferenceEquals(_mediaCountIndex, _snapshot))) return;
        _mediaCountRequest?.Cancel();
        _mediaCountRequest = null;
        if (thread != _mediaCountThread) _threadMedia = null;
        _mediaCountThread = thread;
        _mediaCountIndex = thread == null ? null : _snapshot;
        if (thread == null || _lifetime.IsCancellationRequested) return;

        // The catalog has the captured posts before a library scan can reach this thread or open its media volumes.
        CancellationTokenSource request = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _mediaCountRequest = request;
        _mediaCountTask = CancellationWork.RunAsync(request, token => ReadThreadMediaAsync(thread, token), () =>
        {
            if (ReferenceEquals(_mediaCountRequest, request)) _mediaCountRequest = null;
        });
    }

    private async Task ReadThreadMediaAsync(ResourceId thread, CancellationToken token)
    {
        ThreadMediaPositions? media;
        try
        {
            await using ArchiveReader reader = await _openReader(token);
            ArchivedThreadView? view = await reader.ReadThreadAsync(thread, token);
            media = view?.MediaPositions;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException
            or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        {
            return; // The library's own index operation reports read errors.
        }
        if (token.IsCancellationRequested) return;
        _threadMedia = media;
        UpdateStatusText();
        if (_panel is { } panel && _panelRow is { } row) panel.SetThreadPosition(ThreadPositionOf(row.Entry));
    }

    private string? ThreadMediaSummary() => _threadMedia is { Count: > 0 } media
        ? Texts.T("thread.media.total", media.Count) : null;

    private int? ThreadMediaNumberOf(MediaLibraryEntry entry) =>
        entry.Thread == _mediaCountThread ? _threadMedia?.NumberOf(entry.PostId, entry.Position) : null;

    private string? ThreadPositionOf(MediaLibraryEntry entry) => ThreadMediaNumberOf(entry) is int number
        ? Texts.T("reader.sidebar.position", number, _threadMedia!.Count) : null;

    internal async Task<ThreadMediaPositions?> ThreadMediaForGalleryAsync(IReadOnlyList<MediaLibraryEntry> window)
    {
        ResourceId? thread = _mediaCountThread;
        if (thread == null || window.Any(entry => entry.Thread != thread)
            || _terms.Any(term => term.Field is not (MediaFilterField.Thread or MediaFilterField.Threads or MediaFilterField.Missing))
            || MediaLibraryFilter.HasText(_nameFilter.Text)) return null;
        await _mediaCountTask;
        return thread == _mediaCountThread ? _threadMedia : null;
    }

    /// <summary>
    /// "Open in Media" / "Show in Media" of several threads: the thread set becomes the only filter (a clean view of these threads' media),
    /// like <see cref="ShowOnlyThread"/> does for one thread. An empty set is allowed and shows nothing, which the page then explains like any
    /// filter without a match.
    /// </summary>
    internal void ShowOnlyThreads(MediaThreadSet threads)
    {
        ArgumentNullException.ThrowIfNull(threads);
        ResetFilters();
        EditTerms(terms => MediaFilterTermSet.With(terms, MediaFilterTerm.InThreads(threads)));
    }

    /// <summary>The chip text of a thread set: "12 threads from Archive search" or "3 selected threads" (the plural forms of the language).</summary>
    internal static string ThreadSetChipText(MediaThreadSet threads) => threads.Origin == MediaThreadSetOrigin.ArchiveSearch
        ? Texts.Count("media.filter.threadsChip.archive", threads.Count) : Texts.Count("media.filter.threadsChip.selection", threads.Count);

    /// <summary>
    /// The tip of the chip: the titles of the first <see cref="ThreadSetTipTitles"/> threads of the set (the number of a thread the index has
    /// no title for), then "+N more" for the rest. Null for an empty set.
    /// </summary>
    internal string? ThreadSetTip(MediaThreadSet threads)
    {
        if (threads.Count == 0) return null;
        MediaLibrarySnapshot? snapshot = _snapshot;
        if (!ReferenceEquals(_threadTitlesFor, snapshot) || _threadTitles == null)
        {
            Dictionary<ResourceId, string> titles = new();
            foreach (MediaLibraryEntry entry in snapshot?.Entries ?? Array.Empty<MediaLibraryEntry>())
                if (!string.IsNullOrWhiteSpace(entry.ThreadTitle)) titles.TryAdd(entry.Thread, entry.ThreadTitle);
            _threadTitles = titles;
            _threadTitlesFor = snapshot;
        }
        string[] lines = [.. threads.Ordered.Take(ThreadSetTipTitles)
            .Select(thread => _threadTitles.TryGetValue(thread, out string? title) ? title : "#" + thread.Thread)];
        int more = threads.Count - lines.Length;
        return string.Join('\n', more > 0 ? lines.Append(Texts.T("media.filter.threadsMore", more)) : lines);
    }
}
