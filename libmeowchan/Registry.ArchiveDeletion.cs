namespace MeowChan;

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Persistence;

public sealed partial class Registry
{
    /// <summary>Waits for callbacks scheduled before the quiesced archive operation.</summary>
    public async Task DrainBackgroundTasksAsync(CancellationToken token = default)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            Task[] pending;
            lock (_state) pending = _backgroundTasks.ToArray();
            if (pending.Length == 0) return;
            await Task.WhenAll(pending).WaitAsync(token).ConfigureAwait(false);
        }
    }

    /// <summary>Persists exclusion even when an archived thread has no active watch.</summary>
    public async Task SuppressArchivedThreadAsync(ResourceId id, string url, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_stopping, this);
            EnsureRestoredForPersistence();
            ResourceKey key = new(id.Source, id.Board, id.Thread);
            SessionSnapshot next = RemovalSnapshot(url, thread: true);
            if (!_durableWork) await SaveSessionAsync(next).ConfigureAwait(false);
            if (_workStore != null)
            {
                _workResources.TryGetValue(key, out WorkResource? previous);
                previous ??= WorkStateAccess.For(_workStore).FindResource(url);
                WorkResource suppressed = CommitWorkIntent(new WorkResource
                {
                    Url = url,
                    OutputRoot = previous?.OutputRoot ?? _outputDirectory,
                    Generation = (previous?.Generation ?? 0) + 1,
                    Intent = WorkIntent.Suppressed,
                    IsThread = true,
                    Discovered = previous?.Discovered ?? false
                });
                _workResources[key] = suppressed;
            }
            if (_durableWork)
            {
                try { await SaveSessionAsync(next).ConfigureAwait(false); }
                catch (Exception exception) { Warn($"Session projection could not be saved: {exception.Message}"); }
            }
            _session = next;
            _suppressed.Add(key);
            _hasMutations = true;
        }
        finally { _gate.Release(); }
        SignalChanged();
    }
}
