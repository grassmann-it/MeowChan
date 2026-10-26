namespace MeowChan;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Persistence;
using MeowChan.Storage;

internal sealed record ArchiveCleanupResource(ResourceId Id, string Url, IReadOnlyList<string> MediaDirectories);

public sealed partial class Registry
{
    private readonly Dictionary<ResourceKey, TaskCompletionSource> _archiveCleanupReservations = new();

    private Task? ArchiveCleanupWait(Resource resource, string outputRoot)
    {
        if (_archiveCleanupReservations.TryGetValue(resource.Key, out TaskCompletionSource? existing)) return existing.Task;
        if (resource.Thread == null || !_queue.HasArchiveCleanupReservations) return null;
        _workResources.TryGetValue(resource.Key, out WorkResource? previous);
        previous ??= (_workStore == null ? null : WorkStateAccess.For(_workStore).FindResource(resource.Url));
        string[] historicalPaths = previous == null || _workStore == null ? Array.Empty<string>()
            : _workStore.LoadMedia(previous.Url, previous.Generation).SelectMany(media => media.LegacyPath == null
                ? new[] { media.DestinationPath } : new[] { media.DestinationPath, media.LegacyPath }).ToArray();
        return _queue.ArchiveCleanupWait(new ResourceId(resource.Key.Source, resource.Key.Board, resource.Key.Thread!),
            StorageLayout.GetThreadDirectory(outputRoot, resource.Thread), historicalPaths);
    }

    // No provider resolution: an offline archive remains usable with its source disabled.
    // The host owns _commands; this gate atomically excludes new watches/retries for exact IDs.
    internal async Task<IAsyncDisposable?> TryReserveArchiveCleanupAsync(
        IReadOnlyCollection<ArchiveCleanupResource> resources, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(resources);
        ArchiveCleanupResource[] selected = resources.ToArray();
        if (selected.Length == 0 || selected.Select(item => item.Id).Distinct().Count() != selected.Length)
            return null;
        await _gate.WaitAsync(token).ConfigureAwait(false);
        IDisposable? queueReservation = null;
        try
        {
            ObjectDisposedException.ThrowIf(_stopping, this);
            EnsureRestoredForPersistence();
            ResourceKey[] keys = selected.Select(item => new ResourceKey(item.Id.Source, item.Id.Board, item.Id.Thread)).ToArray();
            lock (_state)
                if (keys.Any(key => _watches.ContainsKey(key) || _archiveCleanupReservations.ContainsKey(key))) return null;
            queueReservation = _queue.TryReserveArchiveCleanup(selected);
            if (queueReservation == null || _workStore == null) return null;
            // Read the authority, never the eventually refreshed UI cache. Read errors propagate.
            IReadOnlyList<WorkResource> work = _workStore.LoadResources();
            foreach (ArchiveCleanupResource item in selected)
            {
                WorkResource[] matches = work.Where(resource => resource.Url == item.Url).ToArray();
                if (matches.Length > 1) return null;
                foreach (WorkResource resource in matches)
                {
                    if (!resource.IsThread || resource.CapturePending
                        || resource.Intent is not (WorkIntent.Completed or WorkIntent.Suppressed)) return null;
                    if (_workStore.LoadMedia(resource.Url, resource.Generation).Any(media =>
                        media.Status is not (MediaWorkStatus.Succeeded or MediaWorkStatus.Excluded or MediaWorkStatus.Expired)
                        || media.ManualAttempt)) return null;
                }
            }
            TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            foreach (ResourceKey key in keys) _archiveCleanupReservations.Add(key, completion);
            CleanupReservation reservation = new(this, keys, completion, queueReservation);
            queueReservation = null;
            return reservation;
        }
        finally { queueReservation?.Dispose(); _gate.Release(); }
    }

    private sealed class CleanupReservation(Registry owner, ResourceKey[] keys,
        TaskCompletionSource completion, IDisposable queueReservation) : IAsyncDisposable
    {
        private int _released;
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0) return;
            await owner._gate.WaitAsync().ConfigureAwait(false);
            try
            {
                foreach (ResourceKey key in keys) owner._archiveCleanupReservations.Remove(key);
                queueReservation.Dispose();
                completion.TrySetResult();
            }
            finally { owner._gate.Release(); }
        }
    }
}
