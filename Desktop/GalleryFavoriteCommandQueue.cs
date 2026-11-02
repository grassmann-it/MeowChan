using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace MeowChan.Desktop;

/// <summary>
/// UI-owner-only pending intents, not a second favorites store. Coalesce repeats and
/// serialize writes per exact key; an opposite intent during a save is not dropped.
/// The saved-state display always reads the session's persisted index, never this queue.
/// </summary>
internal sealed class GalleryFavoriteCommandQueue<TKey> where TKey : notnull
{
    private sealed class Pending(bool desired)
    {
        internal bool Desired = desired;
        internal readonly TaskCompletionSource Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private readonly Dictionary<TKey, Pending> _pending = new();
    internal bool IsPending(TKey key) => _pending.ContainsKey(key);
    internal bool Target(TKey key, bool saved) => _pending.TryGetValue(key, out Pending? pending) ? pending.Desired : saved;

    internal Task SetAsync(TKey key, bool selected, Func<bool, Task> save, Action changed)
    {
        if (_pending.TryGetValue(key, out Pending? existing))
        {
            existing.Desired = selected;
            return existing.Completion.Task;
        }
        var pending = new Pending(selected);
        _pending.Add(key, pending);
        _ = DrainAsync(key, pending, save, changed);
        return pending.Completion.Task;
    }

    private async Task DrainAsync(TKey key, Pending pending, Func<bool, Task> save, Action changed)
    {
        Exception? failure = null;
        try
        {
            changed();
            bool written;
            do
            {
                written = pending.Desired;
                await save(written);
                changed();
            } while (written != pending.Desired);
        }
        catch (Exception error) { failure = error; }
        finally
        {
            _pending.Remove(key);
            try { changed(); }
            catch (Exception error) { failure ??= error; }
        }
        if (failure != null) pending.Completion.TrySetException(failure);
        else pending.Completion.TrySetResult();
    }
}
