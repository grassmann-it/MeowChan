using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using MeowChan.Desktop;
using MeowChan.Gallery;
using MeowChan.Storage;

namespace MeowChan;

// Reserve known preview sizes before cards are measured. Probe unknown headers and predecode thumbnails
// around the viewport, under the existing prefetch setting and decode/cache budgets.
public partial class ArchivePage
{
    private readonly record struct PendingSize(string PostId, string Key, ArchiveAttachmentRow Row);

    private int _readerPreviewPrefetchPosts = ReaderPreviewReservation.PrefetchPostsDefault;
    private CancellationTokenSource? _previewSizePass;
    private ReaderPreviewReservation.PostWindow _previewSizeWindow;
    private readonly Dictionary<ArchiveAttachmentRow, (int Post, CancellationTokenSource Cancellation)> _previewPrefetches =
        new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<ArchiveAttachmentRow, int> _previewPrefetched = new(ReferenceEqualityComparer.Instance);
    private bool _previewPrefetchHooked, _previewPrefetchScheduled, _previewPrefetchForward = true;
    private int _previewPrefetchGeneration;
    private readonly ReaderPreviewPrefetchQueue<(ArchiveAttachmentRow Row, int Post)> _previewPrefetchQueue = new();

    /// <summary>Posts before and after the visible ones whose previews are decoded ahead ("Preload previews"); 0 turns it off.</summary>
    internal int ReaderPreviewPrefetchPosts
    {
        get => _readerPreviewPrefetchPosts;
        set
        {
            _readerPreviewPrefetchPosts = ReaderPreviewReservation.ClampPrefetchPosts(value);
            SchedulePreviewPrefetch();
        }
    }

    /// <summary>The current viewport header-read pass (smoke and test seam); completed when there is none.</summary>
    internal Task PreviewSizePassTask { get; private set; } = Task.CompletedTask;

    /// <summary>Preloads running now (diagnostic and smoke seam).</summary>
    internal int PreviewPrefetchesRunning => _previewPrefetches.Count;

    /// <summary>Preloads planned but not started (diagnostic and smoke seam).</summary>
    internal int PreviewPrefetchesWaiting => _previewPrefetchQueue.Waiting;

    private void ReservePreviewSizes(IReadOnlyList<ArchivePostRow> posts)
    {
        CancelPreviewWork();
        HookPreviewPrefetch();
        if (_thread == null || posts.Count == 0) return;
        ResourceId resource = _thread.Summary.Id;
        ReaderPreviewAspectMemo memo = ReaderPreviewAspectMemo.Shared;
        _inlineVideo.ExpandedImages.BeginThread(string.Join('/', resource.Source, resource.Board, resource.Thread));
        foreach (ArchivePostRow post in posts)
        {
            if (!post.ShowsMedia) continue;
            foreach (ArchiveAttachmentRow row in post.Attachments)
            {
                if (ReaderPreviewReservation.AspectKey(resource, post.Id, row.Attachment) is not { } key) continue;
                row.RememberAspect = aspect => memo.Remember(key, aspect);
                row.ExpandKey = key;
                if (row.KnownAspect == null && memo.TryGet(key, out double aspect)) row.KnownAspect = aspect;
            }
        }
        SchedulePreviewPrefetch();
    }

    private void ReserveViewportPreviewSizes(IReadOnlyList<ArchivePostRow> rows, int first, int last,
        ReaderPreviewReservation.PostWindow window)
    {
        if (_thread == null || window == _previewSizeWindow) return;
        if (_previewSizePass != null)
        {
            CancelPreviewSizePass();
            return;
        }
        _previewSizeWindow = window;
        List<PendingSize> unknown = new();
        void Collect(int index)
        {
            ArchivePostRow post = rows[index];
            if (!post.ShowsMedia) return;
            foreach (ArchiveAttachmentRow row in post.Attachments)
                if (row.KnownAspect == null && row.ExpandKey is { } key) unknown.Add(new(post.Id, key, row));
        }

        for (int index = first; index <= last; index++) Collect(index);
        foreach (int index in ReaderPreviewReservation.NearestFirst(first, last, window, _previewPrefetchForward)) Collect(index);
        if (unknown.Count == 0) return;
        ResourceId resource = _thread.Summary.Id;
        CancellationTokenSource pass = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _previewSizePass = pass;
        PreviewSizePassTask = CancellationWork.RunAsync(pass,
            token => ReadPreviewSizesAsync(resource, unknown.ToArray(), _mediaReady.Task, token), () =>
            {
                _previewSizePass = null;
                SchedulePreviewPrefetch();
            });
    }

    private static async Task ReadPreviewSizesAsync(ResourceId resource, PendingSize[] pending,
        Task<IReadOnlyList<ArchiveAssetRow>> ready, CancellationToken token)
    {
        try
        {
            // Waits only for the existing local manifest scan; never starts one.
            IReadOnlyList<ArchiveAssetRow> assets = await ready.WaitAsync(token).ConfigureAwait(false);
            await Task.Run(() =>
            {
                MediaAssetSnapshot[] manifests = assets.Select(asset => asset.Manifest).Distinct().ToArray();
                List<(ArchiveAttachmentRow Row, double Aspect)> batch = new();
                foreach (PendingSize item in pending)
                {
                    token.ThrowIfCancellationRequested();
                    ResolvedMediaAttachment? resolved = MediaAttachmentResolver.Resolve(manifests, resource, item.PostId, item.Row.Attachment);
                    if (resolved == null
                        || ReaderPreviewHeaderProbe.TryReadAspect(resolved.Asset.Path, resolved.Asset.Length, token) is not { } aspect) continue;
                    ReaderPreviewAspectMemo.Shared.Remember(item.Key, aspect);
                    batch.Add((item.Row, aspect));
                    // Small batches: the nearest rows are reserved within a few header reads, the rest without a post per file.
                    if (batch.Count >= 8) { PublishPreviewSizes(batch.ToArray(), token); batch.Clear(); }
                }
                if (batch.Count > 0) PublishPreviewSizes(batch.ToArray(), token);
            }, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception) { } // A reservation is only a hint: the visible preview still loads and reports its own problems.
    }

    // A size the decoded thumbnail set meanwhile stays (it is the picture that is drawn).
    private static void PublishPreviewSizes((ArchiveAttachmentRow Row, double Aspect)[] sizes, CancellationToken token) =>
        Dispatcher.UIThread.Post(() =>
        {
            if (token.IsCancellationRequested) return;
            foreach ((ArchiveAttachmentRow row, double aspect) in sizes) row.KnownAspect ??= aspect;
        });

    private void CancelPreviewSizePass()
    {
        _previewSizeWindow = default;
        // An in-progress read keeps the slot until it unwinds. Completion schedules the latest viewport.
        _previewSizePass?.Cancel();
    }

    private void CancelPreviewWork()
    {
        CancelPreviewSizePass();
        ++_previewPrefetchGeneration;
        _previewPrefetchQueue.Clear();
        CancelPreviewPrefetches(_ => true);
        _previewPrefetched.Clear();
    }

    private void HookPreviewPrefetch()
    {
        if (_previewPrefetchHooked) return;
        _previewPrefetchHooked = true;
        ArchivePosts.AddHandler(ScrollViewer.ScrollChangedEvent, (_, e) =>
        {
            if (e is ScrollChangedEventArgs { OffsetDelta.Y: var delta } && delta != 0) _previewPrefetchForward = delta > 0;
            SchedulePreviewPrefetch();
        }, RoutingStrategies.Bubble);
    }

    // Coalesced: one pass after the layout of a scroll step (or a rebuilt view), however many scroll events it raised.
    private void SchedulePreviewPrefetch()
    {
        if (_ended || _previewPrefetchScheduled || !_previewPrefetchHooked) return;
        _previewPrefetchScheduled = true;
        Dispatcher.UIThread.Post(RunPreviewPrefetch, DispatcherPriority.Background);
    }

    private void RunPreviewPrefetch()
    {
        _previewPrefetchScheduled = false;
        int posts = _readerPreviewPrefetchPosts;
        if (_ended || ArchivePosts.ItemsSource is not IReadOnlyList<ArchivePostRow> rows || rows.Count == 0
            || !ArchivePosts.IsEffectivelyVisible || ArchiveAttachmentView.GetLoadingSuspended(ArchivePosts)
            || !VisiblePostRange(out int first, out int last))
        {
            CancelPreviewSizePass();
            _previewPrefetchQueue.Clear();
            CancelPreviewPrefetches(_ => true);
            _previewPrefetched.Clear();
            return;
        }
        ReaderPreviewReservation.PostWindow window = ReaderPreviewReservation.PrefetchWindow(first, last, rows.Count, posts);
        ReserveViewportPreviewSizes(rows, first, last, posts == 0 ? new(first, last + 1) : window);
        foreach ((ArchiveAttachmentRow row, int index) in _previewPrefetched.ToArray())
            if (!window.Contains(index)) _previewPrefetched.Remove(row);
        // Replace the old queue before cancelling stale work, since completion can hand a slot to the queue immediately.
        _previewPrefetchQueue.Replace(PlanPreviewPrefetches(rows, first, last, window, posts));
        CancelPreviewPrefetches(post => !window.Contains(post));
        FeedPreviewPrefetches();
    }

    private List<(ArchiveAttachmentRow Row, int Post)> PlanPreviewPrefetches(IReadOnlyList<ArchivePostRow> rows, int first, int last,
        ReaderPreviewReservation.PostWindow window, int posts)
    {
        int budget = ReaderPreviewReservation.PrefetchBudget(MediaTileThumbnails.Cache.Capacity), planned = 0;
        List<(ArchiveAttachmentRow Row, int Post)> plan = new();
        foreach (int index in ReaderPreviewReservation.NearestFirst(first, last, window, _previewPrefetchForward))
        {
            if (!rows[index].ShowsMedia) continue;
            foreach (ArchiveAttachmentRow row in rows[index].Attachments)
            {
                if (row.PrefetchThumbnail == null || row.Attachment.IsDeleted || !ReaderPreviewReservation.Prefetches(row.LargePreview, posts)
                    || SavedMediaTypes.Classify(row.Attachment.OriginalFileName ?? string.Empty) == SavedMediaKind.Video) continue;
                if (++planned > budget) return plan;
                if (_previewPrefetched.ContainsKey(row) || _previewPrefetches.ContainsKey(row)) continue;
                plan.Add((row, index));
            }
        }
        return plan;
    }

    // Starts waiting preloads while fewer than the in-flight cap run. Called after a planning pass and whenever a preload finishes.
    private void FeedPreviewPrefetches()
    {
        if (_ended || _lifetime.IsCancellationRequested || ArchiveAttachmentView.GetLoadingSuspended(ArchivePosts)) return;
        while (_previewPrefetchQueue.TryTake(_previewPrefetches.Count, out (ArchiveAttachmentRow Row, int Post) next))
            if (!_previewPrefetches.ContainsKey(next.Row) && !_previewPrefetched.ContainsKey(next.Row)) StartPreviewPrefetch(next.Row, next.Post);
    }

    // The posts whose card intersects the list's viewport. The realized set can hold more (a focused card stays realized far away).
    private bool VisiblePostRange(out int first, out int last)
    {
        first = int.MaxValue; last = -1;
        double height = ArchivePosts.Bounds.Height;
        foreach (Control container in ArchivePosts.GetRealizedContainers())
        {
            if (!container.IsVisible || container.TranslatePoint(default, ArchivePosts) is not { } top
                || top.Y + container.Bounds.Height <= 0 || top.Y >= height) continue;
            int index = ArchivePosts.IndexFromContainer(container);
            if (index < 0) continue;
            first = Math.Min(first, index); last = Math.Max(last, index);
        }
        return last >= 0;
    }

    private void StartPreviewPrefetch(ArchiveAttachmentRow row, int post)
    {
        CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _previewPrefetches[row] = (post, cancellation);
        _ = PrefetchPreviewAsync(row, post, cancellation, _previewPrefetchGeneration);
    }

    private async Task PrefetchPreviewAsync(ArchiveAttachmentRow row, int post, CancellationTokenSource cancellation, int generation)
    {
        try
        {
            Bitmap? bitmap = await row.PrefetchThumbnail!(cancellation.Token);
            // Kept in the preview cache under its bytes; this lease is only given back.
            if (bitmap != null)
            {
                if (!cancellation.IsCancellationRequested && generation == _previewPrefetchGeneration)
                {
                    row.KnownAspect = ReaderPreviewReservation.Settle(row.KnownAspect, bitmap.PixelSize.Width, bitmap.PixelSize.Height);
                    _previewPrefetched[row] = post;
                }
                MediaTileThumbnails.Release(bitmap);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception)
        {
            // The visible load owns error reporting. A failed preload never counts as a cached preview.
        }
        finally
        {
            if (_previewPrefetches.TryGetValue(row, out var running) && ReferenceEquals(running.Cancellation, cancellation))
                _previewPrefetches.Remove(row);
            cancellation.Dispose();
            FeedPreviewPrefetches();
        }
    }

    private void CancelPreviewPrefetches(Func<int, bool> cancel)
    {
        if (_previewPrefetches.Count == 0) return;
        foreach (KeyValuePair<ArchiveAttachmentRow, (int Post, CancellationTokenSource Cancellation)> entry in _previewPrefetches.ToArray())
        {
            if (!cancel(entry.Value.Post)) continue;
            entry.Value.Cancellation.Cancel();
        }
    }
}
