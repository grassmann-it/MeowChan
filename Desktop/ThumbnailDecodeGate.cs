using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MeowChan.Desktop;

/// <summary>Who is waiting for a decode slot. Image previews are what the user is looking at; posters are background work.</summary>
internal enum ThumbnailDecodePriority { Image, Poster }

/// <summary>
/// Bounds the thumbnail decodes of one page. Only real decodes take a slot (a memory-cache hit never does), and image previews are
/// served before queued video posters so the extra poster work cannot starve them. A poster is not starved forever either: after
/// <see cref="MaximumConsecutiveImages"/> image grants in a row, a waiting poster goes next. Waiting is asynchronous and cancellable.
/// </summary>
internal sealed class ThumbnailDecodeGate
{
    /// <summary>Fairness constant, not a user-visible limit: how many images may pass a waiting poster in a row.</summary>
    internal const int MaximumConsecutiveImages = 8;

    private sealed class Waiter
    {
        internal readonly TaskCompletionSource<bool> Signal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal LinkedListNode<Waiter>? Node;
    }

    private readonly object _lock = new();
    private readonly LinkedList<Waiter> _images = new(), _posters = new();
    private int _free;
    private int _imagesInARow;

    internal ThumbnailDecodeGate(int slots)
    {
        if (slots < 1) throw new ArgumentOutOfRangeException(nameof(slots));
        _free = slots;
    }

    /// <summary>Slots currently free (diagnostic and test aid).</summary>
    internal int Free { get { lock (_lock) return _free; } }

    /// <summary>Runs <paramref name="work"/> once a slot is free and gives the slot back afterwards, also on failure or cancellation.</summary>
    internal async Task<T> RunAsync<T>(ThumbnailDecodePriority priority, Func<Task<T>> work, CancellationToken token)
    {
        await WaitAsync(priority, token).ConfigureAwait(false);
        try
        {
            token.ThrowIfCancellationRequested(); // Coverage can change while queued for a slot.
            return await work().ConfigureAwait(false);
        }
        finally { Release(); }
    }

    private async Task WaitAsync(ThumbnailDecodePriority priority, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Waiter waiter = new();
        lock (_lock)
        {
            if (_free > 0) { _free--; return; }
            LinkedList<Waiter> queue = priority == ThumbnailDecodePriority.Image ? _images : _posters;
            waiter.Node = queue.AddLast(waiter);
        }
        using CancellationTokenRegistration registration = token.Register(() =>
        {
            lock (_lock)
            {
                if (waiter.Node?.List is not { } list) return; // Already granted: the owner releases the slot itself.
                list.Remove(waiter.Node);
                waiter.Node = null;
            }
            waiter.Signal.TrySetCanceled(token);
        });
        await waiter.Signal.Task.ConfigureAwait(false);
    }

    private void Release()
    {
        Waiter? next = null;
        lock (_lock)
        {
            bool posterTurn = _posters.Count > 0 && (_images.Count == 0 || _imagesInARow >= MaximumConsecutiveImages);
            LinkedList<Waiter> queue = posterTurn ? _posters : _images;
            if (queue.Count == 0) { _free++; return; }
            next = queue.First!.Value;
            queue.RemoveFirst();
            next.Node = null;
            _imagesInARow = posterTurn ? 0 : _imagesInARow + (_posters.Count > 0 ? 1 : 0);
        }
        next.Signal.TrySetResult(true);
    }
}
