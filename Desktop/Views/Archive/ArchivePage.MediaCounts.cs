using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.VisualTree;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Diagnostics;
using MeowChan.Persistence;

namespace MeowChan;

public partial class ArchivePage
{
    private readonly SemaphoreSlim _archiveMediaCountGate = new(1, 1);
    private LatestUiWork<MediaCountRequest>? _archiveMediaCounts;
    private int _archiveMediaCountRevision;
    private string? _archiveMediaCountDatabasePath;
    private bool _archiveMediaCountWasVisible;
    private bool _archiveMediaCountsDirty = true;
    private bool _obscuredByGallery;
    private ArchiveThreadRow[] _archiveMediaCountRows = [];
    private ArchiveSearchRequest? _archiveMediaCountQuery;
    internal bool IsObscuredByGallery => _obscuredByGallery;
    internal long ArchiveMediaCountScansSkippedWhileObscured { get; private set; }
    internal long ArchiveMediaCountScanStarts => _archiveMediaCounts?.Starts ?? 0;
    internal long ArchiveMediaCountScanCancellations => _archiveMediaCounts?.Cancellations ?? 0;
    internal long ArchiveMediaCountFollowUpReplacements => _archiveMediaCounts?.Replacements ?? 0;
    internal long ArchiveMediaCountApplications { get; private set; }
    internal long ArchiveMediaCountStaleResults { get; private set; }

    /// <summary>Cancel once on coverage; keep the active slot until lingering I/O actually returns.</summary>
    internal void SetObscuredByGallery(bool obscured)
    {
        if (_obscuredByGallery == obscured) return;
        _obscuredByGallery = obscured;
        ArchiveAttachmentView.SetLoadingSuspended(this, obscured);
        ReaderCoverageChanged();
        if (obscured) CancelArchiveMediaCounts();
        else NotifyArchiveMediaCountsChanged();
    }

    private IReadOnlyDictionary<string, MediaCountWorkStamp> _archiveMediaCountWork = new Dictionary<string, MediaCountWorkStamp>();
    internal Task ArchiveMediaCountTask { get; private set; } = Task.CompletedTask;
    internal Func<IEnumerable<string>, CancellationToken, Task<ArchiveThreadMediaCount>> ReadArchiveMediaInventoryAsync { get; set; } =
        (directories, token) => ArchiveThreadMediaInventory.ReadAsync(directories, token);
    private sealed record MediaCountItemStamp(long Generation, string Identity, string Variant, string? Acquisition,
        string? Asset, string Destination, MediaWorkStatus Status, int Attempts, DateTimeOffset? LastAttempt);
    private sealed record MediaCountWorkStamp(WorkIntent Intent, ThreadSourceStateValue Source, bool CapturePending,
        DateTimeOffset? EndObserved, MediaCountItemStamp[] Items)
    {
        internal bool SameAs(MediaCountWorkStamp other) => Intent == other.Intent && Source == other.Source
            && CapturePending == other.CapturePending && EndObserved == other.EndObserved && Items.SequenceEqual(other.Items);
    }
    private sealed record MediaCountRequest(ArchiveThreadRow[] Rows, IReadOnlyDictionary<string, ThreadWorkSnapshot> Work,
        Func<IEnumerable<string>, CancellationToken, Task<ArchiveThreadMediaCount>> Inventory,
        Func<CancellationToken, Task<ArchiveReader>> OpenReader, ArchiveSearchRequest Query, string Database, int Revision, long Id);

    private void InitializeArchiveMediaCounts()
    {
        AttachedToVisualTree += (_, _) => ObserveArchiveMediaCountVisibility();
        DetachedFromVisualTree += (_, _) => ObserveArchiveMediaCountVisibility();
        PropertyChanged += (_, change) =>
        {
            if (change.Property == IsVisibleProperty) ObserveArchiveMediaCountVisibility();
        };
        // Layout catches effective ancestor visibility. The edge check avoids ordinary layout work.
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
        work.ToDictionary(pair => pair.Key, pair => new MediaCountWorkStamp(pair.Value.Intent, pair.Value.Source,
            pair.Value.CapturePending, pair.Value.EndObservedAt, pair.Value.Media.Select(item => new MediaCountItemStamp(
                item.Generation, item.MediaIdentifier, item.Variant, item.AcquisitionRevisionKey, item.CommittedAssetId,
                item.DestinationPath, item.Status, item.Attempts, item.LastAttemptAt)).ToArray()), StringComparer.Ordinal);

    // Updates inventory only: no applied-query, work-filter, row, selection or focus replacement.
    internal void NotifyArchiveMediaCountsChanged()
    {
        if (_ended || !_available || _busy || !IsEffectivelyVisible || !this.IsAttachedToVisualTree() || _openReader == null || !_listLoaded) return;
        if (_obscuredByGallery)
        {
            _archiveMediaCountsDirty = true;
            ArchiveMediaCountScansSkippedWhileObscured++;
            PerformanceDiagnostics.Record("archive.counts", "covered-skip", revision: _archiveMediaCountRevision);
            return;
        }
        ArchiveThreadRow[] rows = ArchiveVisibleRows;
        var work = CurrentMediaCountWork(rows);
        var stamps = MediaCountWorkStamps(work);
        bool sameWork = stamps.Count == _archiveMediaCountWork.Count
            && stamps.All(pair => _archiveMediaCountWork.TryGetValue(pair.Key, out var prior) && pair.Value.SameAs(prior));
        if (!_archiveMediaCountsDirty && sameWork && ReferenceEquals(_archiveMediaCountQuery, _appliedSearch)
            && rows.SequenceEqual(_archiveMediaCountRows, ReferenceEqualityComparer.Instance)) return;
        BeginArchiveMediaCounts(rows, work);
    }

    private void BeginArchiveMediaCounts(ArchiveThreadRow[] rows, IReadOnlyDictionary<string, ThreadWorkSnapshot>? work = null)
    {
        _archiveMediaCountsDirty = true;
        if (_ended || _obscuredByGallery || !IsEffectivelyVisible || !this.IsAttachedToVisualTree() || _openReader == null
            || _appliedSearch == null || _archiveMediaCountDatabasePath == null || rows.Length == 0) return;
        work ??= CurrentMediaCountWork(rows);
        _archiveMediaCountWork = MediaCountWorkStamps(work);
        _archiveMediaCountRows = rows;
        _archiveMediaCountQuery = _appliedSearch;
        _archiveMediaCountsDirty = false;
        var request = new MediaCountRequest(rows, work, ReadArchiveMediaInventoryAsync, _openReader, _appliedSearch,
            _archiveMediaCountDatabasePath, ++_archiveMediaCountRevision, PerformanceDiagnostics.NextId());
        _archiveMediaCounts ??= new LatestUiWork<MediaCountRequest>(LoadArchiveMediaCountsAsync, _lifetime.Token);
        // Visible invalidations replace one pending request; they never cancel/restart the active scan.
        ArchiveMediaCountTask = _archiveMediaCounts.Request(request);
    }

    private bool CanApplyMediaCount(MediaCountRequest request, CancellationToken token) => !_ended && !_obscuredByGallery
        && !token.IsCancellationRequested && request.Revision == _archiveMediaCountRevision
        && ReferenceEquals(request.Query, _appliedSearch)
        && StringComparer.Ordinal.Equals(request.Database, _archiveMediaCountDatabasePath)
        && request.Rows.SequenceEqual(ArchiveVisibleRows, ReferenceEqualityComparer.Instance);

    private async Task LoadArchiveMediaCountsAsync(MediaCountRequest request, CancellationToken token)
    {
        using var measurement = PerformanceDiagnostics.Measure("archive.counts.scan", request.Id, revision: request.Revision);
        try
        {
            var counts = await Task.Run(async () =>
            {
                // Keep the gate even for a cancelled, non-cooperative filesystem call.
                await _archiveMediaCountGate.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    await using ArchiveReader reader = await request.OpenReader(token).ConfigureAwait(false);
                    if (!StringComparer.Ordinal.Equals(reader.DatabasePath, request.Database)) return null;
                    Dictionary<ResourceId, ArchiveThreadMediaCount> values = new();
                    foreach (ArchiveThreadRow row in request.Rows.Take(PageSize))
                    {
                        token.ThrowIfCancellationRequested();
                        IReadOnlyList<string> directories = await reader.GetMediaDirectoriesAsync(row.Summary.Id, token).ConfigureAwait(false);
                        values[row.Summary.Id] = await request.Inventory(directories.Append(row.Summary.MediaDirectory), token).ConfigureAwait(false);
                    }
                    return values;
                }
                finally { _archiveMediaCountGate.Release(); }
            }, token);
            if (counts == null || !CanApplyMediaCount(request, token))
            {
                ArchiveMediaCountStaleResults++;
                PerformanceDiagnostics.Record("archive.counts", "stale", request.Id, revision: request.Revision);
                return;
            }
            foreach (ArchiveThreadRow row in request.Rows)
                if (counts.TryGetValue(row.Summary.Id, out var count)) row.SetMediaCount(count, request.Work.GetValueOrDefault(row.Summary.Url));
            ArchiveMediaCountApplications++;
            PerformanceDiagnostics.Record("archive.counts", "applied", request.Id, revision: request.Revision, value: counts.Count);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { PerformanceDiagnostics.Record("archive.counts", "cancelled", request.Id, revision: request.Revision); }
        catch (Exception error)
        {
            if (CanApplyMediaCount(request, token))
                foreach (ArchiveThreadRow row in request.Rows) row.SetMediaCount(new(null, Error: error.Message), request.Work.GetValueOrDefault(row.Summary.Url));
            PerformanceDiagnostics.Record("archive.counts", "failed", request.Id, revision: request.Revision);
        }
    }

    private void CancelArchiveMediaCounts()
    {
        ++_archiveMediaCountRevision;
        _archiveMediaCountsDirty = true;
        _archiveMediaCounts?.Cancel();
    }
}
