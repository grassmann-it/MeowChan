namespace MeowChan.Downloader;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Interfaces;

public partial class DownloadQueue
{
    private readonly List<CleanupTargetReservation> _cleanupReservations = new();
    // Scheduler entries remain visible while outside _stateLock awaiting target admission.
    private readonly HashSet<DownloadItem> _cleanupTransits = new(ReferenceEqualityComparer.Instance);

    internal IDisposable? TryReserveArchiveCleanup(IReadOnlyList<ArchiveCleanupResource> resources)
    {
        CleanupTargetReservation candidate = new(this, resources);
        lock (_stateLock)
        {
            if (_stopping || _cleanupReservations.Any(existing => existing.Overlaps(candidate))) return null;
            if (CleanupItemsLocked().Any(candidate.Matches)) return null;
            _cleanupReservations.Add(candidate);
            return candidate;
        }
    }

    private IEnumerable<DownloadItem> CleanupItemsLocked() => _jobs.Values.Select(job => job.Item)
        .Concat(_jobs.Values.Where(job => job.Replacement != null).Select(job => job.Replacement!))
        .Concat(_pendingDownloads.Select(job => job.Item))
        .Concat(_interruptedDownloads.Select(job => job.Item))
        .Concat(_scheduled.Values.Select(scheduled => scheduled.Item))
        .Concat(_admissionWaiters.Select(waiter => waiter.Item)).Concat(_cleanupTransits);

    internal IReadOnlyList<ResourceId> ReadArchiveCleanupActiveThreadIds()
    {
        lock (_stateLock) return Array.AsReadOnly(CleanupItemsLocked().Select(item => new ResourceId(
                item.Thread.Board.ImageBoard.Name, item.Thread.Board.BoardCode, item.Thread.ThreadId)).Distinct()
            .OrderBy(id => id.Source, StringComparer.Ordinal).ThenBy(id => id.Board, StringComparer.Ordinal)
            .ThenBy(id => id.Thread, StringComparer.Ordinal).ToArray());
    }

    internal bool HasArchiveCleanupReservations
    {
        get { lock (_stateLock) return _cleanupReservations.Count != 0; }
    }

    internal Task? ArchiveCleanupWait(ResourceId id, string directory, IReadOnlyList<string>? historicalPaths = null)
    {
        lock (_stateLock) return _cleanupReservations.FirstOrDefault(reservation =>
            reservation.Contains(id) || reservation.OverlapsDirectory(directory)
            || historicalPaths != null && historicalPaths.Any(reservation.MatchesPath))?.Released.Task;
    }

    private Task? CleanupWaitLocked(DownloadItem item)
        => _cleanupReservations.FirstOrDefault(reservation => reservation.Matches(item))?.Released.Task;

    private sealed class CleanupTargetReservation : IDisposable
    {
        private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        private readonly DownloadQueue _owner;
        private readonly HashSet<ResourceId> _ids;
        private readonly string[] _directories;
        internal TaskCompletionSource Released { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal CleanupTargetReservation(DownloadQueue owner, IReadOnlyList<ArchiveCleanupResource> resources)
        {
            _owner = owner;
            _ids = resources.Select(resource => resource.Id).ToHashSet();
            _directories = resources.SelectMany(resource => resource.MediaDirectories)
                .Select(directory => Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory))).ToArray();
        }
        internal bool Contains(ResourceId id) => _ids.Contains(id);
        internal bool Matches(IThread thread) => _ids.Contains(new ResourceId(thread.Board.ImageBoard.Name,
            thread.Board.BoardCode, thread.ThreadId));
        internal bool Matches(DownloadItem item) => Matches(item.Thread) || MatchesPath(item.DestinationPath)
            || item.LegacyPath != null && MatchesPath(item.LegacyPath);
        internal bool MatchesPath(string path)
        {
            string full = Path.GetFullPath(path);
            return _directories.Any(directory => string.Equals(full, directory, PathComparison)
                || full.StartsWith(directory + Path.DirectorySeparatorChar, PathComparison));
        }
        internal bool OverlapsDirectory(string directory)
        {
            string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
            return MatchesPath(full) || _directories.Any(reserved => reserved.StartsWith(full + Path.DirectorySeparatorChar, PathComparison));
        }
        internal bool Overlaps(CleanupTargetReservation other) => _ids.Overlaps(other._ids)
            || _directories.Any(other.MatchesPath) || other._directories.Any(MatchesPath);
        public void Dispose()
        {
            lock (_owner._stateLock)
            {
                if (!_owner._cleanupReservations.Remove(this)) return;
                Released.TrySetResult();
                _owner.PulseCapacityLocked();
            }
        }
    }
}
