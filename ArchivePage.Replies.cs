using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Interfaces;

namespace MeowChan;

public partial class ArchivePage
{
    private const int InlineReplyPageSize = 20;
    private CancellationTokenSource? _inlineRepliesCancellation;
    private int _inlineRepliesRequest;
    private ArchivePostRow[] _inlineReplyRows = Array.Empty<ArchivePostRow>();

    private void RefreshInlineReplies(ArchivePostRow[] rows)
    {
        CancelInlineReplies();
        _inlineReplyRows = rows;
        if (rows.Length == 0 || _thread == null || _openReader == null || !_available || _ended) return;
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
            foreach (ArchivePostRow row in rows) row.SetBacklinkCount(counts.GetValueOrDefault(row.Id));
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

    private async void OnRepliesExpanded(object? sender, RoutedEventArgs e)
    {
        if (sender is not Expander { DataContext: ArchivePostRow row } || row.RepliesLoaded) return;
        await LoadInlineRepliesAsync(row);
    }
    private void OnRepliesCollapsed(object? sender, RoutedEventArgs e) { }
    private async void OnMoreRepliesClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ArchivePostRow row }) await LoadInlineRepliesAsync(row);
    }

    private async Task LoadInlineRepliesAsync(ArchivePostRow row)
    {
        if (_ended || _busy || !_available || _openReader == null || _thread == null || row.RepliesLoading ||
            !_inlineReplyRows.Any(item => ReferenceEquals(item, row))) return;
        int request = _inlineRepliesRequest;
        ArchivePostRow[] rows = _inlineReplyRows;
        ResourceId thread = _thread.Summary.Id;
        CancellationToken token = _inlineRepliesCancellation?.Token ?? _lifetime.Token;
        row.SetRepliesLoading(true);
        row.SetRepliesError(null);
        try
        {
            await using ArchiveReader reader = await _openReader(token);
            ArchiveQuotePage page = await reader.GetBacklinksAsync(new ArchivePostId(thread, row.Id),
                row.Replies.Count, InlineReplyPageSize, token);
            if (!InlineReplyPageCurrent(rows, thread, request)) return;
            row.SetReplies(Array.AsReadOnly(row.Replies.Concat(page.Items.Select(quote => new ArchiveQuoteRow(quote, true))).ToArray()),
                page.TotalCount, page.Offset);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (InlineReplyPageCurrent(rows, thread, request)) row.SetRepliesError(exception.Message);
        }
        finally { if (InlineReplyPageCurrent(rows, thread, request)) row.SetRepliesLoading(false); }
    }
}
