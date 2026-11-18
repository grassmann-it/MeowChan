using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MeowChan.Desktop.Controls;

/// <summary>
/// The thumbnails the filmstrip holds at one moment: only those of the tiles inside the decode window (the visible tiles plus the
/// preload on each side), each one leased from the shared preview cache. Moving the window cancels what is still loading outside it
/// and gives back what scrolled out, so a row of thousands never holds more than the window; a preview that is already in the cache
/// arrives without a decode, and the real decodes wait behind the page's decode gate (see <c>ThumbnailDecodeGate</c>). Generic so the
/// bookkeeping is tested without Avalonia bitmaps: <typeparamref name="T"/> is the leased preview, <c>release</c> gives a lease back.
/// </summary>
internal sealed class FilmstripThumbnailWindow<T> : IDisposable where T : class
{
    private readonly Func<int, CancellationToken, Task<T?>> _load;
    private readonly Action<T> _release;
    private readonly object _gate = new();
    private readonly Dictionary<int, T> _held = new();
    private readonly Dictionary<int, CancellationTokenSource> _pending = new();
    private readonly HashSet<int> _failed = new();
    private GalleryFilmstripPlan.TileRange _window = GalleryFilmstripPlan.TileRange.Empty;
    private bool _disposed;

    /// <param name="load">Loads and leases the preview of one tile; null when it has none. Cancellation throws or returns null.</param>
    /// <param name="release">Gives a leased preview back (also one that arrived too late to be shown).</param>
    internal FilmstripThumbnailWindow(Func<int, CancellationToken, Task<T?>> load, Action<T> release)
    {
        _load = load;
        _release = release;
    }

    /// <summary>Raised on the loading continuation (the UI thread in the app) after the preview of a tile arrived.</summary>
    internal event Action<int>? Loaded;

    internal int HeldCount { get { lock (_gate) return _held.Count; } }
    internal int PendingCount { get { lock (_gate) return _pending.Count; } }

    internal bool IsHeld(int index) { lock (_gate) return _held.ContainsKey(index); }

    /// <summary>The preview of a tile, or null while it is not loaded (the caller does not release it; the window owns the lease).</summary>
    internal T? TryGet(int index) { lock (_gate) return _held.GetValueOrDefault(index); }

    /// <summary>
    /// Moves the window. Tiles outside it lose their preview and any load in flight; tiles inside it that have neither start loading,
    /// nearest to <paramref name="centre"/> first. A tile whose load failed is tried again once it has been outside the window.
    /// </summary>
    internal void SetWindow(GalleryFilmstripPlan.TileRange window, int centre)
    {
        List<T> dropped = new();
        List<CancellationTokenSource> cancelled = new();
        List<int> start = new();
        lock (_gate)
        {
            if (_disposed) return;
            // The same window with everything either held, loading or given up on has nothing to do (a pointer move calls this often).
            if (window == _window && _held.Count + _pending.Count + _failed.Count >= window.Count) return;
            _window = window;
            foreach (int index in _held.Keys.Where(index => !window.Contains(index)).ToArray())
            { dropped.Add(_held[index]); _held.Remove(index); }
            foreach (int index in _pending.Keys.Where(index => !window.Contains(index)).ToArray())
            { cancelled.Add(_pending[index]); _pending.Remove(index); }
            _failed.RemoveWhere(index => !window.Contains(index));
            foreach (int index in GalleryFilmstripPlan.OutwardOrder(window, centre))
            {
                if (_held.ContainsKey(index) || _pending.ContainsKey(index) || _failed.Contains(index)) continue;
                CancellationTokenSource source = new();
                _pending[index] = source;
                start.Add(index);
            }
        }
        foreach (CancellationTokenSource source in cancelled) { source.Cancel(); source.Dispose(); }
        foreach (T item in dropped) _release(item);
        foreach (int index in start) _ = RunAsync(index);
    }

    private async Task RunAsync(int index)
    {
        CancellationTokenSource? source;
        lock (_gate) _pending.TryGetValue(index, out source);
        if (source == null) return;
        CancellationToken token = source.Token;
        T? result = null;
        bool failed = false;
        try { result = await _load(index, token); }
        catch (OperationCanceledException) { }
        catch (Exception) { failed = true; }
        bool keep, registered;
        lock (_gate)
        {
            registered = _pending.TryGetValue(index, out CancellationTokenSource? found) && ReferenceEquals(found, source);
            if (registered) _pending.Remove(index);
            keep = registered && result != null && !_disposed && !token.IsCancellationRequested && _window.Contains(index) && !_held.ContainsKey(index);
            if (keep) _held[index] = result!;
            else if (registered && (failed || result == null) && !token.IsCancellationRequested && _window.Contains(index)) _failed.Add(index);
        }
        // A source that was cancelled was already disposed by whoever cancelled it; only a finished load disposes its own.
        if (registered) source.Dispose();
        if (keep) Loaded?.Invoke(index);
        else if (result != null) _release(result);
    }

    /// <summary>Drops every preview and every load (the list the tiles belong to changed).</summary>
    internal void Reset()
    {
        List<T> dropped;
        List<CancellationTokenSource> cancelled;
        lock (_gate)
        {
            dropped = _held.Values.ToList(); _held.Clear();
            cancelled = _pending.Values.ToList(); _pending.Clear();
            _failed.Clear();
            _window = GalleryFilmstripPlan.TileRange.Empty;
        }
        foreach (CancellationTokenSource source in cancelled) { source.Cancel(); source.Dispose(); }
        foreach (T item in dropped) _release(item);
    }

    public void Dispose()
    {
        lock (_gate) _disposed = true;
        Reset();
    }
}
