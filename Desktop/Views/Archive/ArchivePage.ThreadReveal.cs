using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using MeowChan.Archive;
using MeowChan.Desktop;

namespace MeowChan;

// "Show in thread" from the Media and the Tagging page (Tom's hand test, 16.11.2026): the thread the jump opens stands selected in the archive's
// list, so the list, the reader and what closing the large reader comes back to are about the same thread. The least surprising step that does it:
//   1. the thread is a row of the page shown: that row alone is selected;
//   2. the applied search finds it on another page: that page is shown (the search stays the user's);
//   3. the applied search does not find it: the search becomes the thread's board (source and board, every other filter dropped, the sort kept)
//      on the page that holds it. Only this step changes the user's search; the shell says so in a toast whose action puts the previous search
//      and page back (the reader keeps its thread).
// The archive not holding the thread at all changes nothing: the reader still shows it and its commands act on it (they follow the loaded
// thread, not the list).
public partial class ArchivePage
{
    internal enum ThreadRevealKind { None, Selected, Paged, SearchReplaced }

    internal sealed record ThreadRevealPlan(ThreadRevealKind Kind, ArchiveSearchRequest? Request = null, int Offset = 0);

    /// <summary>What a reveal did; <see cref="RestoreSearch"/> (only after <see cref="ThreadRevealKind.SearchReplaced"/>) puts the replaced search back.</summary>
    internal sealed record ThreadReveal(ThreadRevealKind Kind, Func<Task<bool>>? RestoreSearch = null)
    {
        internal static readonly ThreadReveal None = new(ThreadRevealKind.None);
    }

    /// <summary>The thread list as the place keys go back to when the large reader the jump opened is closed.</summary>
    internal Control ThreadListFocusTarget => ArchiveSelectionList;

    /// <summary>The search a thread the applied search does not find is shown with: its board in its source, sorted as before.</summary>
    internal static ArchiveSearchSpec BoardSearch(ResourceId thread, ArchiveThreadSort sort) =>
        ArchiveSearchSpec.FromExactIdentities(boards: [new ArchiveBoardConstraint(thread.Source, thread.Board)], sort: sort);

    /// <summary>
    /// Decides the step without touching the page: <paramref name="visible"/> are the rows shown, <paramref name="applied"/> with
    /// <paramref name="eligibleUrls"/> the applied search and its frozen work membership (null: no work filter). Offsets are list pages.
    /// </summary>
    internal static async Task<ThreadRevealPlan> PlanThreadRevealAsync(ArchiveReader reader, ResourceId thread, IReadOnlyCollection<ResourceId> visible,
        ArchiveSearchRequest applied, IReadOnlySet<string>? eligibleUrls, Func<ArchiveSearchSpec, ArchiveSearchRequest> createRequest,
        int pageSize, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(applied);
        ArgumentNullException.ThrowIfNull(createRequest);
        if (pageSize <= 0) throw new ArgumentOutOfRangeException(nameof(pageSize));
        if (visible.Contains(thread)) return new(ThreadRevealKind.Selected);
        int index = Array.IndexOf(await ResolveExportResultIdsAsync(reader, applied, eligibleUrls, token), thread);
        if (index >= 0) return new(ThreadRevealKind.Paged, applied, index / pageSize * pageSize);
        ArchiveSearchRequest board = createRequest(BoardSearch(thread, applied.Spec.Sort));
        index = Array.IndexOf(await ResolveExportResultIdsAsync(reader, board, null, token), thread);
        return index < 0 ? new(ThreadRevealKind.None) : new(ThreadRevealKind.SearchReplaced, board, index / pageSize * pageSize);
    }

    /// <summary>
    /// Shows <paramref name="thread"/> selected in the list (see the steps above). None when the page cannot act now (busy, a dialog, no
    /// catalog), when the search changed meanwhile, or when the archive does not hold the thread.
    /// </summary>
    internal async Task<ThreadReveal> RevealThreadInListAsync(ResourceId thread)
    {
        if (_ended || !CanRead || _busy || IsFilenameSearchOpen || IsReaderDialogOpen || _openReader is not { } open) return ThreadReveal.None;
        ArchiveSearchRequest? appliedBefore = _appliedSearch;
        ArchiveSearchSpec queryBefore = _query;
        int offsetBefore = _listOffset;
        ArchiveSearchRequest applied = appliedBefore ?? ArchiveSearchRequest.Create(queryBefore);
        ResourceId[] visible = ArchiveVisibleRows.Select(row => row.Summary.Id).ToArray();
        IReadOnlySet<string>? eligible = CaptureExportWorkUrls(_workFilter, GetWorkSnapshots?.Invoke() ?? Array.Empty<ThreadWorkSnapshot>());
        ThreadRevealPlan plan;
        try
        {
            await using ArchiveReader reader = await open(_lifetime.Token);
            plan = await PlanThreadRevealAsync(reader, thread, visible, applied, eligible, CreateSearchRequest, PageSize, _lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return ThreadReveal.None; }
        catch (Exception exception)
        {
            if (!_ended) Message(exception.Message, true);
            return ThreadReveal.None;
        }
        // Someone else searched meanwhile: their result stands.
        if (_ended || _busy || !ReferenceEquals(appliedBefore, _appliedSearch) || !Equals(queryBefore, _query)) return ThreadReveal.None;
        if (plan.Kind == ThreadRevealKind.Selected) return SelectOnlyThread(thread) ? new(plan.Kind) : ThreadReveal.None;
        if (plan.Request is not { } request) return ThreadReveal.None;
        bool replaced = plan.Kind == ThreadRevealKind.SearchReplaced;
        // The jump loads its own thread next: the list load does not read the one the reader showed before again.
        bool committed = await LoadListAsync(NewListLoad(request, plan.Offset, afterCommit: () =>
        {
            if (replaced) WriteSearchDraft(request.Spec);
            SelectOnlyThread(thread);
        }) with { ReloadShownThread = false });
        if (!committed || !ArchiveVisibleRows.Any(row => row.Summary.Id == thread)) return ThreadReveal.None;
        if (!replaced) return new(plan.Kind);
        // The way back: the previous search and page, as long as the replaced search is still the applied one (a search of the user's own
        // meanwhile stands). The reader keeps the thread it shows (re-read at its position, as after any list page); the list selects it
        // again where the previous page holds it.
        return new(plan.Kind, async () =>
        {
            if (_ended || _busy || !CanRead || !ReferenceEquals(_appliedSearch, request)) return false;
            return await LoadListAsync(NewListLoad(applied, offsetBefore, afterCommit: () =>
            {
                WriteSearchDraft(applied.Spec);
                SelectOnlyThread(thread);
            }));
        });
    }

    /// <summary>The board the shell names when the jump replaced the search ("4chan /kot/"), as the search's board chip names it.</summary>
    internal static string RevealedBoardLabel(ResourceId thread) => BoardLabel(new ArchiveBoardConstraint(thread.Source, thread.Board));

    /// <summary>
    /// After a jump into the large reader keys must work at once: a post that could not take the focus (gone, hidden by the view) must not
    /// leave it on nothing or on the page behind the reader. Then the reader's way back (Close) has it, as after a load without posts.
    /// </summary>
    internal void EnsureLargeReaderFocus()
    {
        if (_ended || !_largeReader) return;
        Visual reader = _largeReaderFrame ?? (Visual)ArchiveReaderPane;
        if (TopLevel.GetTopLevel(reader)?.FocusManager?.GetFocusedElement() is Visual focused && focused.GetSelfAndVisualAncestors().Contains(reader)) return;
        ArchiveCloseReader.Focus();
    }

    private bool SelectOnlyThread(ResourceId thread)
    {
        ArchiveThreadRow[] rows = ArchiveVisibleRows;
        ArchiveThreadRow? row = rows.FirstOrDefault(candidate => candidate.Summary.Id == thread);
        if (row == null) return false;
        _archiveSelectionIds.Clear();
        _archiveSelectionIds.Add(thread);
        _archiveLeadId = thread;
        SynchronizeArchiveSelection(rows);
        if (_threadTable) ArchiveThreadTable.ScrollIntoView(row, null);
        else ArchiveThreads.ScrollIntoView(row);
        return true;
    }
}
