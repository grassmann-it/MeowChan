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
    private bool _loadingReplyCounts;
    private ReaderPreviewReservation.PostWindow _inlineReplyWindow;
    internal Func<CancellationToken, Task>? BeforeInlineReplyCountsPublicationAsync { get; set; }

    private void BeginInlineReplies(ReaderPostRows? rows)
    {
        if (rows is { Count: > 0 } && _openReader != null && CanRead && !_ended)
            _inlineRepliesCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
    }

    private void CancelInlineReplies()
    {
        _inlineRepliesCancellation?.Cancel();
        _inlineRepliesCancellation?.Dispose();
        _inlineRepliesCancellation = null;
        _loadingReplyCounts = false;
        _inlineReplyWindow = default;
    }

    private void RequestInlineReplyCounts(ReaderPostRows source, ReaderPreviewReservation.PostWindow window)
    {
        _inlineReplyWindow = window;
        if (_loadingReplyCounts || _inlineRepliesCancellation is not { } loading || _thread == null) return;
        List<ArchivePostRow> pending = new();
        // GetBacklinkCountsAsync accepts at most 500 IDs per read. A larger viewport is picked up in the next pass.
        for (int index = window.Start; index < window.End && pending.Count < 500; index++)
        {
            ArchivePostRow row = source[index];
            if (!row.IsStub && !row.SuppressReplyToggle && row.BacklinkCount == null && !row.HasRepliesError && !row.RepliesLoading)
                pending.Add(row);
        }
        if (pending.Count == 0) return;
        _loadingReplyCounts = true;
        _ = LoadInlineReplyCountsAsync(source, pending, _thread.Summary.Id, loading.Token);
    }

    private async Task LoadInlineReplyCountsAsync(ReaderPostRows source, List<ArchivePostRow> pending, ResourceId thread,
        CancellationToken token)
    {
        bool InWindow(ArchivePostRow row) => _inlineReplyWindow.Contains(source.IndexOf(row));
        try
        {
            await using ArchiveReader reader = await _openReader!(token);
            IReadOnlyDictionary<string, int> counts = await reader.GetBacklinkCountsAsync(thread, pending.Select(row => row.Id).ToArray(), token);
            if (!InlineReplyRowsCurrent(source, token)) return;
            if (BeforeInlineReplyCountsPublicationAsync is { } beforePublication) await beforePublication(token);
            if (!InlineReplyRowsCurrent(source, token)) return;
            ArchivePostRow[] applicable = pending.Where(InWindow).ToArray();
            if (applicable.Length == 0) return;
            Func<Task>? restoreLayout = CaptureReaderCountLayoutCorrection();
            foreach (ArchivePostRow row in applicable)
                if (row.BacklinkCount == null) row.SetBacklinkCount(counts.GetValueOrDefault(row.Id));
            ApplyAutomaticRepliesToRealised(applicable);
            if (restoreLayout != null) await restoreLayout();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (!InlineReplyRowsCurrent(source, token)) return;
            foreach (ArchivePostRow row in pending)
                if (row.BacklinkCount == null && InWindow(row)) row.SetRepliesError(exception.Message);
        }
        finally
        {
            if (InlineReplyRowsCurrent(source, token))
            {
                _loadingReplyCounts = false;
                SchedulePreviewPrefetch();
            }
        }
    }

    private bool InlineReplyRowsCurrent(ReaderPostRows rows, CancellationToken token)
        => !_ended && !token.IsCancellationRequested && ReferenceEquals(rows, ArchivePosts.ItemsSource);

    // The header counter opens and closes the nested reply cards; the first opening loads the first page.
    private async void OnReplyToggleClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ArchivePostRow row }) await SetRepliesExpandedAsync(row, !row.RepliesExpanded);
    }

    internal async Task SetRepliesExpandedAsync(ArchivePostRow row, bool open)
    {
        if (_ended || ArchivePosts.ItemsSource is not ReaderPostRows rows || rows.IndexOf(row) < 0) return;
        RememberReplyChoice(row, open);
        row.RepliesExpanded = open;
        if (open && !row.RepliesLoaded) await LoadInlineRepliesAsync(row);
    }
    private async void OnMoreRepliesClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ArchivePostRow row }) await LoadInlineRepliesAsync(row);
    }

    private async Task LoadInlineRepliesAsync(ArchivePostRow row)
    {
        if (_ended || _busy || !CanRead || _openReader == null || _thread == null || row.RepliesLoading ||
            ArchivePosts.ItemsSource is not ReaderPostRows rows || rows.IndexOf(row) < 0) return;
        ResourceId thread = _thread.Summary.Id;
        CancellationToken token = _inlineRepliesCancellation?.Token ?? _lifetime.Token;
        bool Current() => InlineReplyRowsCurrent(rows, token) && rows.IndexOf(row) >= 0;
        int loadedBefore = row.Replies.Count;
        row.SetRepliesLoading(true);
        row.SetRepliesError(null);
        try
        {
            await using ArchiveReader reader = await _openReader(token);
            ArchiveQuotePage page = await reader.GetBacklinksAsync(new ArchivePostId(thread, row.Id),
                row.Replies.Count, InlineReplyPageSize, token);
            // A history restore may have put loaded pages on the row while this one was on its way; its offset is then stale.
            if (!Current() || row.Replies.Count != loadedBefore) return;
            row.SetReplies(Array.AsReadOnly(row.Replies.Concat(page.Items.Select(quote => ReplyCard(quote, thread))).ToArray()),
                page.TotalCount, page.Offset);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (Current()) row.SetRepliesError(exception.Message);
        }
        finally { if (Current()) row.SetRepliesLoading(false); }
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
