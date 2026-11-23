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
using MeowChan.Interfaces;

namespace MeowChan;

public partial class ArchivePage
{
    // InlineReplyPageSize, ReplyCardMaxLines and ReplyCardMaxCharacters are clamped instance properties (ArchivePage.ReaderLimits.cs).
    private CancellationTokenSource? _inlineRepliesCancellation;
    private int _inlineRepliesRequest;
    private ArchivePostRow[] _inlineReplyRows = Array.Empty<ArchivePostRow>();
    internal Func<CancellationToken, Task>? BeforeInlineReplyCountsPublicationAsync { get; set; }

    private void RefreshInlineReplies(ArchivePostRow[] rows)
    {
        CancelInlineReplies();
        _inlineReplyRows = rows;
        if (rows.Length == 0 || _thread == null || _openReader == null || !CanRead || _ended) return;
        int request = _inlineRepliesRequest;
        ResourceId thread = _thread.Summary.Id;
        CancellationTokenSource loading = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _inlineRepliesCancellation = loading;
        _ = LoadInlineReplyCountsAsync(rows, thread, request, loading.Token);
    }

    private void CancelInlineReplies()
    {
        ++_inlineRepliesRequest;
        _inlineRepliesCancellation?.Cancel();
        _inlineRepliesCancellation?.Dispose();
        _inlineRepliesCancellation = null;
        _inlineReplyRows = Array.Empty<ArchivePostRow>();
    }

    private async Task LoadInlineReplyCountsAsync(ArchivePostRow[] rows, ResourceId thread,
        int request, CancellationToken token)
    {
        try
        {
            await using ArchiveReader reader = await _openReader!(token);
            IReadOnlyDictionary<string, int> counts = await reader.GetBacklinkCountsAsync(thread,
                rows.Select(row => row.Id).ToArray(), token);
            if (!InlineReplyPageCurrent(rows, thread, request)) return;
            if (BeforeInlineReplyCountsPublicationAsync is { } beforePublication)
            {
                await beforePublication(token);
                if (!InlineReplyPageCurrent(rows, thread, request)) return;
            }
            Func<Task>? restoreLayout = CaptureReaderCountLayoutCorrection();
            foreach (ArchivePostRow row in rows) row.SetBacklinkCount(counts.GetValueOrDefault(row.Id));
            UpdateFocusedReplies();
            foreach (ArchivePostRow row in rows)
                if (row.RepliesExpanded && ArchivePosts.ContainerFromItem(row) != null) LoadExpandedReplies(row);
            if (ArchivePosts.SelectedItem is ArchivePostRow selected) LoadSidebarPostReplies(selected);
            if (restoreLayout != null) await restoreLayout();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (!InlineReplyPageCurrent(rows, thread, request)) return;
            foreach (ArchivePostRow row in rows) row.SetRepliesError(exception.Message);
        }
    }

    private bool InlineReplyPageCurrent(ArchivePostRow[] rows, ResourceId thread, int request)
        => !_ended && request == _inlineRepliesRequest && ReferenceEquals(rows, _inlineReplyRows)
            && _thread?.Summary.Id == thread;

    // The header counter opens and closes the nested reply cards; the first opening loads the first page.
    private async void OnReplyToggleClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ArchivePostRow row }) await SetRepliesExpandedAsync(row, !row.RepliesExpanded);
    }

    internal async Task SetRepliesExpandedAsync(ArchivePostRow row, bool open)
    {
        if (_ended || !ContainsInlineReplyRow(row)) return;
        Func<Task>? restore = CaptureReaderCountLayoutCorrection();
        _replyAutoExpand.RememberChoice(row, open);
        if (restore != null) await restore();
        if (row.RepliesExpanded && !row.RepliesLoaded) await LoadInlineRepliesAsync(row);
    }
    private async void OnMoreRepliesClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ArchivePostRow row }) await LoadInlineRepliesAsync(row);
    }

    private void LoadExpandedReplies(ArchivePostRow row)
    {
        if (row is { RepliesExpanded: true, HasBacklinks: true, RepliesLoaded: false, HasRepliesError: false })
            OpenActions.Start(() => LoadInlineRepliesAsync(row), "archive.reader.replies");
    }

    private bool ContainsInlineReplyRow(ArchivePostRow row) =>
        _inlineReplyRows.Any(item => ReferenceEquals(item, row));

    private async Task LoadInlineRepliesAsync(ArchivePostRow row)
    {
        if (_ended || _busy || !CanRead || _openReader == null || _thread == null || row.RepliesLoading ||
            !ContainsInlineReplyRow(row)) return;
        int request = _inlineRepliesRequest;
        ArchivePostRow[] rows = _inlineReplyRows;
        ResourceId thread = _thread.Summary.Id;
        CancellationToken token = _inlineRepliesCancellation?.Token ?? _lifetime.Token;
        int loadedBefore = row.Replies.Count;
        row.SetRepliesLoading(true);
        row.SetRepliesError(null);
        Func<Task>? restore = null;
        try
        {
            await using ArchiveReader reader = await _openReader(token);
            ArchiveQuotePage page = await reader.GetBacklinksAsync(new ArchivePostId(thread, row.Id),
                loadedBefore, InlineReplyPageSize, token);
            // A history restore may have put loaded pages on the row while this one was on its way; its offset is then stale.
            if (!InlineReplyPageCurrent(rows, thread, request) || row.Replies.Count != loadedBefore) return;
            restore = row.RepliesExpanded ? CaptureReaderCountLayoutCorrection() : null;
            row.SetReplies(Array.AsReadOnly(row.Replies.Concat(page.Items.Select(quote => ReplyCard(quote, thread))).ToArray()),
                page.TotalCount, page.Offset);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (InlineReplyPageCurrent(rows, thread, request))
            {
                restore = CaptureReaderCountLayoutCorrection();
                row.SetRepliesError(exception.Message);
            }
        }
        finally { if (InlineReplyPageCurrent(rows, thread, request)) row.SetRepliesLoading(false); }
        if (restore != null) await restore();
    }

    // A reply of this thread becomes a card with its header and the first lines of its text; a reply from another
    // thread (or one that was never captured) keeps the plain reference.
    private ArchiveQuoteRow ReplyCard(ArchiveQuote quote, ResourceId thread)
    {
        ArchiveQuoteRow row = new(quote, true, thread);
        if (PreviewPost(row) is not { } post) return row;
        (string header, string excerpt) = ArchivePresentation.PostExcerpt(post, ReplyCardMaxLines, ReplyCardMaxCharacters,
            ReaderDateStyle, ExcerptOpeningPostAt(row), DateTimeOffset.Now);
        return row with { ReplyHeader = header, ReplyExcerpt = PostFilterReplyExcerpt(post, excerpt) };
    }
}
