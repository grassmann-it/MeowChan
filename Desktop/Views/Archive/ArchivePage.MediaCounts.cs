using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.VisualTree;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Persistence;

namespace MeowChan;

public partial class ArchivePage
{
    private readonly SemaphoreSlim _archiveMediaCountGate = new(1, 1);
    private CancellationTokenSource? _archiveMediaCountCancellation;
    private int _archiveMediaCountRevision;
    private string? _archiveMediaCountDatabasePath;
    private bool _archiveMediaCountWasVisible;
    private IReadOnlyDictionary<string, MediaCountWorkStamp> _archiveMediaCountWork = new Dictionary<string, MediaCountWorkStamp>();
    internal Task ArchiveMediaCountTask { get; private set; } = Task.CompletedTask;
    internal Func<IEnumerable<string>, CancellationToken, Task<ArchiveThreadMediaCount>> ReadArchiveMediaInventoryAsync { get; set; } =
        (directories, token) => ArchiveThreadMediaInventory.ReadAsync(directories, token);
    private sealed record MediaCountWorkStamp(WorkIntent Intent, int Count, int Saved, long Attempts, DateTimeOffset? LastAttempt);

    private void InitializeArchiveMediaCounts()
    {
        AttachedToVisualTree += (_, _) => ObserveArchiveMediaCountVisibility();
        PropertyChanged += (_, change) =>
        {
            if (change.Property == IsVisibleProperty) ObserveArchiveMediaCountVisibility();
        };
        // Avalonia 11 has no public effective-visibility event. Layout catches an ancestor
        // becoming visible; the edge check avoids inventory/cache work on ordinary layouts.
        LayoutUpdated += (_, _) => ObserveArchiveMediaCountVisibility();
    }
    private void ObserveArchiveMediaCountVisibility()
    {
        bool visible = IsEffectivelyVisible && this.IsAttachedToVisualTree();
        if (visible == _archiveMediaCountWasVisible) return;
        _archiveMediaCountWasVisible = visible;
        if (visible) NotifyArchiveMediaCountsChanged();
        else CancelArchiveMediaCounts();
    }

    private IReadOnlyDictionary<string, ThreadWorkSnapshot> CurrentMediaCountWork(ArchiveThreadRow[] rows)
    {
        HashSet<string> urls = rows.Select(row => row.Summary.Url).ToHashSet(StringComparer.Ordinal);
        return (GetWorkSnapshots?.Invoke() ?? Array.Empty<ThreadWorkSnapshot>()).Where(work => urls.Contains(work.Url))
            .GroupBy(work => work.Url, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
    }
    private static IReadOnlyDictionary<string, MediaCountWorkStamp> MediaCountWorkStamps(IReadOnlyDictionary<string, ThreadWorkSnapshot> work) =>
        work.ToDictionary(pair => pair.Key, pair => new MediaCountWorkStamp(pair.Value.Intent, pair.Value.Media.Count,
            pair.Value.SavedMedia, pair.Value.Media.Sum(item => (long)item.Attempts),
            pair.Value.Media.Select(item => item.LastAttemptAt).DefaultIfEmpty().Max()), StringComparer.Ordinal);

    // Session notifications refresh only the displayed inventory, never the applied query,
    // frozen work-filter membership, reader filters, list items or selection.
    internal void NotifyArchiveMediaCountsChanged()
    {
        if (_ended || !_available || _busy || !IsEffectivelyVisible || !this.IsAttachedToVisualTree() || _openReader == null || !_listLoaded) return;
        ArchiveThreadRow[] rows = ArchiveVisibleRows;
        var work = CurrentMediaCountWork(rows);
        var stamps = MediaCountWorkStamps(work);
        bool sameWork = stamps.Count == _archiveMediaCountWork.Count
            && stamps.All(pair => _archiveMediaCountWork.TryGetValue(pair.Key, out var prior) && prior == pair.Value);
        // An initially hidden/unattached page has no inventory even when both empty work stamps
        // compare equal. A cancelled old scan must not suppress the first visible scan either.
        if (sameWork && (rows.All(row => row.MediaCount != null) || _archiveMediaCountCancellation != null)) return;
        BeginArchiveMediaCounts(rows, work);
    }

    private void BeginArchiveMediaCounts(ArchiveThreadRow[] rows, IReadOnlyDictionary<string, ThreadWorkSnapshot>? work = null)
    {
        CancelArchiveMediaCounts();
        if (_ended || !IsEffectivelyVisible || !this.IsAttachedToVisualTree() || _openReader == null || _appliedSearch == null || _archiveMediaCountDatabasePath == null || rows.Length == 0) return;
        work ??= CurrentMediaCountWork(rows);
        _archiveMediaCountWork = MediaCountWorkStamps(work);
        int revision = _archiveMediaCountRevision;
        ArchiveSearchRequest query = _appliedSearch;
        string database = _archiveMediaCountDatabasePath;
        var inventory = ReadArchiveMediaInventoryAsync;
        CancellationTokenSource loading = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _archiveMediaCountCancellation = loading;
        ArchiveMediaCountTask = LoadArchiveMediaCountsAsync(rows, work, inventory, query, database, revision, loading);
    }

    private async Task LoadArchiveMediaCountsAsync(ArchiveThreadRow[] rows, IReadOnlyDictionary<string, ThreadWorkSnapshot> work,
        Func<IEnumerable<string>, CancellationToken, Task<ArchiveThreadMediaCount>> inventory,
        ArchiveSearchRequest query, string database, int revision, CancellationTokenSource loading)
    {
        CancellationToken token = loading.Token;
        try
        {
            var counts = await Task.Run(async () =>
            {
                // A cancelled network-filesystem read may still be inside the OS; serialize scans
                // and let newer pages wait without blocking the UI or starting unbounded scans.
                await _archiveMediaCountGate.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    await using ArchiveReader reader = await _openReader!(token).ConfigureAwait(false);
                    if (!StringComparer.Ordinal.Equals(reader.DatabasePath, database)) return null;
                    Dictionary<ResourceId, ArchiveThreadMediaCount> values = new();
                    foreach (ArchiveThreadRow row in rows.Take(PageSize))
                    {
                        token.ThrowIfCancellationRequested();
                        IReadOnlyList<string> directories = await reader.GetMediaDirectoriesAsync(row.Summary.Id, token).ConfigureAwait(false);
                        // Old catalogs may have only the summary's registered location.
                        values[row.Summary.Id] = await inventory(directories.Append(row.Summary.MediaDirectory), token).ConfigureAwait(false);
                    }
                    return values;
                }
                finally { _archiveMediaCountGate.Release(); }
            }, token);
            if (counts == null || _ended || token.IsCancellationRequested || revision != _archiveMediaCountRevision
                || !ReferenceEquals(query, _appliedSearch)
                || !StringComparer.Ordinal.Equals(database, _archiveMediaCountDatabasePath)
                || !rows.SequenceEqual(ArchiveVisibleRows, ReferenceEqualityComparer.Instance)) return;
            foreach (ArchiveThreadRow row in rows)
                if (counts.TryGetValue(row.Summary.Id, out var count)) row.SetMediaCount(count, work.GetValueOrDefault(row.Summary.Url));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (!_ended && !token.IsCancellationRequested && revision == _archiveMediaCountRevision
                && ReferenceEquals(query, _appliedSearch) && rows.SequenceEqual(ArchiveVisibleRows, ReferenceEqualityComparer.Instance))
                foreach (ArchiveThreadRow row in rows) row.SetMediaCount(new(null, Error: error.Message), work.GetValueOrDefault(row.Summary.Url));
        }
        finally
        {
            if (ReferenceEquals(_archiveMediaCountCancellation, loading)) _archiveMediaCountCancellation = null;
            loading.Dispose();
        }
    }

    private void CancelArchiveMediaCounts()
    {
        ++_archiveMediaCountRevision;
        _archiveMediaCountCancellation?.Cancel();
        _archiveMediaCountCancellation = null;
    }
}
