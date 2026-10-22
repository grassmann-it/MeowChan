namespace MeowChan.Downloader;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

/// <summary>Why a destination is waiting and when its next availability check is due.</summary>
public sealed class DownloadTargetWaitState
{
    public string Path { get; }
    public string Reason { get; }
    public DateTimeOffset NextCheckAt { get; }
    internal DownloadTargetWaitState(string path, string reason, DateTimeOffset nextCheckAt)
        => (Path, Reason, NextCheckAt) = (path, reason, nextCheckAt);
}

internal sealed class DownloadTargetGate
{
    internal const long DefaultReserveBytes = 1L << 30;
    internal static readonly TimeSpan RecheckInterval = TimeSpan.FromSeconds(60);
    private const int MaximumTargets = 1024;
    private readonly object _sync = new();
    private readonly TimeProvider _clock;
    private readonly Dictionary<string, Entry> _targets = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly SemaphoreSlim _probes = new(4, 4);
    private readonly CancellationToken _lifetime;
    private long _reserve = DefaultReserveBytes;
    private TaskCompletionSource _changed = Signal();
    internal Func<string, long, long?, CancellationToken, Task<string?>>? ProbeOverride { get; set; }
    public event Action? Changed;
    public long Reserve { get { lock (_sync) return _reserve; } }
    public Task ChangeTask { get { lock (_sync) return _changed.Task; } }

    public DownloadTargetGate(TimeProvider clock, CancellationToken lifetime) => (_clock, _lifetime) = (clock, lifetime);

    public IReadOnlyList<DownloadTargetWaitState> Snapshot()
    {
        lock (_sync) return _targets.Values.Where(e => e.Reason != null)
            .Select(e => new DownloadTargetWaitState(e.Path, e.Reason!, e.NextCheckAt)).ToArray();
    }
    public DateTimeOffset? NextCheckAt
    {
        get { lock (_sync) return _targets.Values.Where(e => e.Reason != null).Select(e => (DateTimeOffset?)e.NextCheckAt).Min(); }
    }
    public bool IsBlocked(string path) { lock (_sync) return _targets.TryGetValue(path, out Entry? entry) && entry.Reason != null; }
    public void UpdateReserve(long bytes)
    {
        if (bytes < 0) throw new ArgumentOutOfRangeException(nameof(bytes));
        lock (_sync)
        {
            _reserve = bytes;
            foreach (Entry entry in _targets.Values.Where(e => e.Reason != null)) entry.NextCheckAt = _clock.GetUtcNow();
            PulseLocked();
        }
        Changed?.Invoke();
    }

    public async Task<bool> CheckAsync(string path, long? expectedSize, CancellationToken cancellationToken, bool force = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Task<bool> check;
        lock (_sync)
        {
            Entry entry = GetEntryLocked(path);
            entry.ExpectedSize = entry.Probe == null ? expectedSize : Math.Max(entry.ExpectedSize ?? 0, expectedSize ?? 0);
            if (entry.Probe != null) check = entry.Probe;
            else
            {
                if (!force && entry.Reason != null && entry.NextCheckAt > _clock.GetUtcNow()) return false;
                // Run outside every queue/UI lock, with a fixed upper bound on physical probes.
                check = entry.Probe = Task.Run(() => ProbeAsync(entry));
            }
        }
        return await check.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task CheckBlockedAsync(bool force, CancellationToken cancellationToken)
    {
        Entry[] entries;
        lock (_sync) entries = _targets.Values.Where(e => e.Reason != null && (force || e.NextCheckAt <= _clock.GetUtcNow())).ToArray();
        // Never materialize one task per target; four physical probes are the shared upper limit.
        foreach (Entry entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await CheckAsync(entry.Path, entry.ExpectedSize, cancellationToken, force).ConfigureAwait(false);
        }
    }

    public void Block(string path, Exception failure)
    {
        lock (_sync)
        {
            Entry entry = GetEntryLocked(path);
            entry.Revision++;
            entry.Reason = IsDiskFull(failure) ? "disk-full" : "target-unavailable";
            entry.NextCheckAt = _clock.GetUtcNow() + RecheckInterval;
            PulseLocked();
        }
        Changed?.Invoke();
    }

    public static bool IsTargetFailure(Exception failure)
    {
        if (failure is DirectoryNotFoundException or DriveNotFoundException) return true;
        if (failure is not IOException || failure is InvalidDataException or System.Net.Http.HttpIOException) return false;
        int code = failure.HResult & 0xffff;
        return IsDiskFull(failure) || code is 5 or 19 or 21 or 53 or 64 or 67 or 121 or 1231 or 116;
    }
    private static bool IsDiskFull(Exception failure) => (failure.HResult & 0xffff) is 28 or 39 or 112;

    private async Task<bool> ProbeAsync(Entry entry)
    {
        bool entered = false;
        long revision;
        long reserve;
        lock (_sync) { revision = entry.Revision; reserve = _reserve; }
        try
        {
            await _probes.WaitAsync(_lifetime).ConfigureAwait(false);
            entered = true;
            string? reason = ProbeOverride != null
                ? await ProbeOverride(entry.Path, reserve, entry.ExpectedSize, _lifetime).ConfigureAwait(false)
                : ProbeFileSystem(entry, reserve);
            lock (_sync)
            {
                if (entry.Revision != revision) return entry.Reason == null;
                entry.Reason = reason;
                entry.LastUsed = _clock.GetUtcNow();
                entry.NextCheckAt = _clock.GetUtcNow() + RecheckInterval;
                PulseLocked();
                return reason == null;
            }
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            lock (_sync)
            {
                entry.Reason = "target-unavailable";
                entry.NextCheckAt = _clock.GetUtcNow() + RecheckInterval;
                PulseLocked();
            }
            return false;
        }
        finally
        {
            if (entered) _probes.Release();
            lock (_sync) { entry.Probe = null; }
            Changed?.Invoke();
        }
    }

    private static string? ProbeFileSystem(Entry entry, long reserve)
    {
        string path = System.IO.Path.GetFullPath(entry.Path);
        string? existing = path;
        while (existing != null && !Directory.Exists(existing)) existing = System.IO.Path.GetDirectoryName(existing);
        if (existing == null) return "target-unavailable";
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        DriveInfo? drive = DriveInfo.GetDrives().Where(d => IsInside(existing, d.RootDirectory.FullName, comparison))
            .OrderByDescending(d => d.RootDirectory.FullName.Length).FirstOrDefault();
        if (drive == null || !drive.IsReady) return "target-unavailable";
        // A disappearing mounted target must not silently become a write to its parent filesystem.
        if (entry.Volume != null && !string.Equals(entry.Volume, drive.Name, comparison)) return "target-unavailable";
        entry.Volume ??= drive.Name;
        long available = drive.AvailableFreeSpace;
        long size = entry.ExpectedSize ?? 0;
        return available < reserve || size > available - reserve ? "free-space-reserve" : null;
    }
    internal static bool IsInside(string path, string root, StringComparison comparison)
    {
        string prefix = System.IO.Path.EndsInDirectorySeparator(root) ? root : root + System.IO.Path.DirectorySeparatorChar;
        return string.Equals(path, root, comparison) || path.StartsWith(prefix, comparison);
    }

    private Entry GetEntryLocked(string path)
    {
        if (_targets.TryGetValue(path, out Entry? entry)) return entry;
        if (_targets.Count >= MaximumTargets)
        {
            Entry? oldest = _targets.Values.Where(e => e.Reason == null && e.Probe == null).MinBy(e => e.LastUsed);
            if (oldest != null) _targets.Remove(oldest.Path);
            else throw new IOException("The bounded destination availability table is full.");
        }
        _targets.Add(path, entry = new Entry(path));
        return entry;
    }
    private void PulseLocked() { TaskCompletionSource old = _changed; _changed = Signal(); old.TrySetResult(); }
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private sealed class Entry(string path)
    {
        public string Path { get; } = path;
        public string? Reason;
        public string? Volume;
        public DateTimeOffset NextCheckAt;
        public DateTimeOffset LastUsed;
        public long? ExpectedSize;
        public Task<bool>? Probe;
        public long Revision;
    }
}
