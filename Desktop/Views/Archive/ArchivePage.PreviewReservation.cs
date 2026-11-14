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

// Stable reader previews. A post used to reserve a wide-screen box for each picture and jump to the real size when its thumbnail
// arrived. Now every row of a page is reserved at its final size before the list measures it: from the session's size memory at once,
// otherwise from a header read of the saved file (a background pass, nearest posts first), and only as a last resort from the decoded
// thumbnail. Previews of the posts just before and after the visible ones are decoded ahead into the shared preview cache, behind the
// visible ones, so scrolling shows them at once. Nothing here reads a file on the UI thread or touches the network.
public partial class ArchivePage
{
    private readonly record struct PendingSize(int Post, string PostId, string Key, ArchiveAttachmentRow Row);

    private int _readerPreviewPrefetchPosts = ReaderPreviewReservation.PrefetchPostsDefault;
    private CancellationTokenSource? _previewSizePass;
    private readonly Dictionary<ArchiveAttachmentRow, (int Post, CancellationTokenSource Cancellation)> _previewPrefetches =
        new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<ArchiveAttachmentRow> _previewPrefetched = new(ReferenceEqualityComparer.Instance);
    private bool _previewPrefetchHooked, _previewPrefetchScheduled, _previewPrefetchForward = true;
    private int _previewPrefetchGeneration;

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

    /// <summary>The header-read pass of the current page (smoke and test seam); completed when there is none.</summary>
    internal Task PreviewSizePassTask { get; private set; } = Task.CompletedTask;

    /// <summary>Preloads running now (diagnostic and smoke seam).</summary>
    internal int PreviewPrefetchesRunning => _previewPrefetches.Count;

    // Called while a page is built, before its rows are handed to the list: what is known is set now, so the first measure already
    // uses it; what is not is read in the background and reserved as it arrives (rows ahead of the viewport long before they show).
    private void ReservePreviewSizes(IReadOnlyList<ArchivePostRow> posts)
    {
        CancelPreviewWork();
        HookPreviewPrefetch();
        if (_thread == null || posts.Count == 0) return;
        ResourceId resource = _thread.Summary.Id;
        ReaderPreviewAspectMemo memo = ReaderPreviewAspectMemo.Shared;
        int anchor = Math.Max(0, IndexOfPost(posts, _readerPostId));
        List<PendingSize> unknown = new();
        for (int index = 0; index < posts.Count; index++)
        {
            ArchivePostRow post = posts[index];
            if (!post.ShowsMedia) continue;
            foreach (ArchiveAttachmentRow row in post.Attachments)
            {
                if (ReaderPreviewReservation.AspectKey(resource, post.Id, row.Attachment) is not { } key) continue;
                row.RememberAspect = aspect => memo.Remember(key, aspect);
                if (row.KnownAspect != null) continue;
                if (memo.TryGet(key, out double aspect)) row.KnownAspect = aspect;
                else unknown.Add(new PendingSize(index, post.Id, key, row));
            }
        }
        if (unknown.Count > 0)
        {
            // Nearest to the post the page opens at first; on a tie the later post (reading goes down).
            PendingSize[] ordered = unknown.OrderBy(item => Math.Abs(item.Post - anchor)).ThenBy(item => item.Post < anchor).ToArray();
            CancellationTokenSource pass = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _previewSizePass = pass;
            PreviewSizePassTask = ReadPreviewSizesAsync(resource, ordered, _mediaReady.Task, pass.Token);
        }
        SchedulePreviewPrefetch();
    }

    private static int IndexOfPost(IReadOnlyList<ArchivePostRow> posts, string? id)
    {
        if (id == null) return -1;
        for (int index = 0; index < posts.Count; index++) if (posts[index].Id == id) return index;
        return -1;
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

    private void CancelPreviewWork()
    {
        CancellationTokenSource? pass = _previewSizePass;
        _previewSizePass = null;
        if (pass != null) { pass.Cancel(); pass.Dispose(); }
        PreviewSizePassTask = Task.CompletedTask;
        ++_previewPrefetchGeneration;
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

    // Coalesced: one pass after the layout of a scroll step (or a new page), however many scroll events it raised.
    private void SchedulePreviewPrefetch()
    {
        if (_previewPrefetchScheduled || !_previewPrefetchHooked) return;
        _previewPrefetchScheduled = true;
        Dispatcher.UIThread.Post(RunPreviewPrefetch, DispatcherPriority.Background);
    }

    private void RunPreviewPrefetch()
    {
        _previewPrefetchScheduled = false;
        int posts = _readerPreviewPrefetchPosts;
        if (_ended || posts == 0 || ArchivePosts.ItemsSource is not IReadOnlyList<ArchivePostRow> rows || rows.Count == 0
            || !ArchivePosts.IsEffectivelyVisible || ArchiveAttachmentView.GetLoadingSuspended(ArchivePosts)
            || !VisiblePostRange(out int first, out int last))
        {
            CancelPreviewPrefetches(_ => true);
            return;
        }
        ReaderPreviewReservation.PostWindow window = ReaderPreviewReservation.PrefetchWindow(first, last, rows.Count, posts);
        // Posts scrolled out of the window are not decoded any more. A preload of a post that scrolled into view runs on: its result is
        // what the visible preview finds in the cache.
        CancelPreviewPrefetches(post => !window.Contains(post));
        int budget = ReaderPreviewReservation.PrefetchBudget(MediaTileThumbnails.Cache.Capacity), planned = 0;
        foreach (int index in ReaderPreviewReservation.NearestFirst(first, last, window, _previewPrefetchForward))
        {
            if (!rows[index].ShowsMedia) continue;
            foreach (ArchiveAttachmentRow row in rows[index].Attachments)
            {
                if (row.PrefetchThumbnail == null || row.Attachment.IsDeleted || !ReaderPreviewReservation.Prefetches(row.LargePreview, posts)
                    || SavedMediaTypes.Classify(row.Attachment.OriginalFileName ?? string.Empty) == SavedMediaKind.Video) continue;
                if (++planned > budget) return;
                if (_previewPrefetched.Contains(row) || _previewPrefetches.ContainsKey(row)) continue;
                StartPreviewPrefetch(row, index);
            }
        }
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
        _ = PrefetchPreviewAsync(row, cancellation, _previewPrefetchGeneration);
    }

    private async Task PrefetchPreviewAsync(ArchiveAttachmentRow row, CancellationTokenSource cancellation, int generation)
    {
        try
        {
            Bitmap? bitmap = await row.PrefetchThumbnail!(cancellation.Token);
            // Kept in the preview cache under its bytes; this lease is only given back.
            if (bitmap != null)
            {
                if (!cancellation.IsCancellationRequested && generation == _previewPrefetchGeneration)
                    row.KnownAspect = ReaderPreviewReservation.Settle(row.KnownAspect, bitmap.PixelSize.Width, bitmap.PixelSize.Height);
                MediaTileThumbnails.Release(bitmap);
            }
            if (generation == _previewPrefetchGeneration) _previewPrefetched.Add(row);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception)
        {
            // Not saved, damaged, timed out: the visible load reports it. Not retried on every scroll step.
            if (generation == _previewPrefetchGeneration) _previewPrefetched.Add(row);
        }
        finally
        {
            if (_previewPrefetches.TryGetValue(row, out var running) && ReferenceEquals(running.Cancellation, cancellation))
                _previewPrefetches.Remove(row);
            cancellation.Dispose();
        }
    }

    private void CancelPreviewPrefetches(Func<int, bool> cancel)
    {
        if (_previewPrefetches.Count == 0) return;
        foreach (KeyValuePair<ArchiveAttachmentRow, (int Post, CancellationTokenSource Cancellation)> entry in _previewPrefetches.ToArray())
        {
            if (!cancel(entry.Value.Post)) continue;
            _previewPrefetches.Remove(entry.Key);
            try { entry.Value.Cancellation.Cancel(); } catch (ObjectDisposedException) { }
        }
    }
}
