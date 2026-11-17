using System;
using System.Threading;
using System.Threading.Tasks;

namespace MeowChan.Desktop.Booru;

internal enum BooruViewerImageState { Loading, Ready, OriginalOnly, NoSample, Unavailable, Cancelled }

/// <summary>The exact opened result, not a mutable selection or unsent search-field edit.</summary>
internal sealed record BooruViewerView(BooruResultRowView Row, BooruLoadOrigin Origin, string? Query,
    BooruViewerImageState ImageState, byte[]? SampleBytes = null)
{
    internal byte[]? ImageBytes => SampleBytes ?? Row.PreviewBytes;
    internal bool CanRetryImage => ImageState is BooruViewerImageState.Unavailable or BooruViewerImageState.Cancelled;
}

/// <summary>
/// Remote viewer contract. Opening loads only a distinct image sample; the thumbnail remains an explicitly labelled
/// fallback. No metadata lookup, original admission, plugin repair or prefetch occurs on open, switch, cancel or close.
/// Original commands carry the identity rendered by their caller, so an old button cannot download a newer selection.
/// Accepted originals belong to the shared queue: closing the viewer never cancels them.
/// </summary>
internal sealed class BooruViewerModel(BooruWorkbenchServices services) : IDisposable
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _imageRequest;
    private BooruViewerView? _view;
    private long _revision;
    private long _selectionRevision;
    private bool _disposed;
    internal event Action? Changed;
    internal BooruViewerView? View { get { lock (_gate) return _view; } }

    internal Task OpenAsync(BooruResultRowView row, BooruLoadOrigin origin, string? query)
    {
        lock (_gate)
        {
            if (_disposed) return Task.CompletedTask;
            _imageRequest?.Cancel();
            _revision++;
            _selectionRevision++;
            _view = new(row, origin, origin == BooruLoadOrigin.Search ? query : null, BooruViewerImageState.Loading);
        }
        return LoadImageAsync();
    }

    internal Task RetryImageAsync() => View is { CanRetryImage: true } ? LoadImageAsync() : Task.CompletedTask;

    private async Task LoadImageAsync()
    {
        BooruViewerView view;
        long revision;
        CancellationToken token;
        lock (_gate)
        {
            if (_disposed || _view is null) return;
            _imageRequest?.Cancel();
            _imageRequest = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            token = _imageRequest.Token;
            revision = ++_revision;
            view = _view;
            _view = view with { ImageState = BooruViewerImageState.Loading, SampleBytes = null };
        }
        Changed?.Invoke();
        BooruViewerImageState state;
        byte[]? bytes = null;
        try
        {
            if (!BooruHostSamplePort.HasImageSample(view.Row.Post))
                state = view.Row.Post.SampleAvailability == BooruSampleAvailability.OriginalOnly
                    ? BooruViewerImageState.OriginalOnly : BooruViewerImageState.NoSample;
            else
            {
                bytes = await services.Samples.LoadSampleAsync(view.Row.Post, token).ConfigureAwait(false);
                state = bytes is { Length: > 0 } ? BooruViewerImageState.Ready : BooruViewerImageState.Unavailable;
            }
        }
        catch (OperationCanceledException) { state = BooruViewerImageState.Cancelled; }
        catch (Exception exception) when (exception is not OutOfMemoryException) { state = BooruViewerImageState.Unavailable; }
        lock (_gate)
        {
            if (_disposed || revision != _revision || _view is null) return;
            _view = _view with { ImageState = state, SampleBytes = state == BooruViewerImageState.Ready ? bytes : null };
        }
        Changed?.Invoke();
    }

    internal void CancelImage()
    {
        lock (_gate)
        {
            if (_view?.ImageState != BooruViewerImageState.Loading) return;
            _revision++;
            _imageRequest?.Cancel();
            _view = _view with { ImageState = BooruViewerImageState.Cancelled };
        }
        Changed?.Invoke();
    }

    internal async Task DownloadAsync(string shownPostId)
    {
        BooruViewerView view;
        long selection;
        lock (_gate)
        {
            if (_disposed || _view is not { } current || current.Row.Post.PostId != shownPostId || !current.Row.CanLoad) return;
            view = current;
            selection = _selectionRevision;
            _view = current with { Row = current.Row with { Load = BooruRowLoad.Handing, LoadCode = null } };
        }
        Changed?.Invoke();
        BooruLoadResult result;
        try
        {
            result = await services.Queue.LoadOriginalAsync(view.Row.Post, view.Origin, view.Query, _lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { result = new(BooruLoadStatus.Failed, BooruCodes.Canceled); }
        catch (Exception exception) when (exception is not OutOfMemoryException) { result = new(BooruLoadStatus.Failed, BooruCodes.Exception); }
        lock (_gate)
        {
            // Switching away and back creates a new view too: no late acknowledgement may relabel it.
            if (_disposed || selection != _selectionRevision || _view is not { } current || !ReferenceEquals(current.Row.Post, view.Row.Post)
                || current.Origin != view.Origin || current.Query != view.Query || current.Row.Load != BooruRowLoad.Handing) return;
            BooruRowLoad load = result.Status switch
            {
                BooruLoadStatus.Queued => BooruRowLoad.Queued,
                BooruLoadStatus.AlreadyQueued => BooruRowLoad.AlreadyQueued,
                BooruLoadStatus.AlreadyLoaded => BooruRowLoad.AlreadyLoaded,
                BooruLoadStatus.NoOriginal => BooruRowLoad.NoOriginal,
                BooruLoadStatus.Restricted => BooruRowLoad.Restricted,
                BooruLoadStatus.Offline => BooruRowLoad.Offline,
                _ => BooruRowLoad.Failed
            };
            _view = current with { Row = current.Row with { Load = load, LoadCode = result.Code } };
        }
        Changed?.Invoke();
    }

    internal void Close()
    {
        lock (_gate)
        {
            _revision++;
            _imageRequest?.Cancel();
            _selectionRevision++;
            _view = null;
        }
        Changed?.Invoke();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _revision++;
            _lifetime.Cancel();
            _imageRequest?.Cancel();
            _selectionRevision++;
            _view = null;
        }
        Changed = null;
    }
}
