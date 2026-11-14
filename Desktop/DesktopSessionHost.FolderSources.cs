using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.FolderSources;

namespace MeowChan.Desktop;

/// <summary>One folder source as the Sources page shows it: the root, its counts and whether it is being scanned right now.</summary>
internal sealed record FolderSourceView(FolderSourceRoot Root, FolderSourceCounts Counts, bool Scanning);

/// <summary>What the folder sources are doing right now (status line, Sources page).</summary>
internal sealed record FolderSourceActivity(Guid? ScanningRoot, FolderSourceScanProgress? ScanProgress, FolderSourceHashStatus Hashing,
    int Files, int Hashed);

/// <summary>
/// Host side of the read-only folder sources (PLAN-POWER-TAGGEN PR2/PR3, E3): the shadow catalog in the profile
/// (<c>folder-sources.db</c>), scans one at a time, lazy hashing (view and user action first, a background pass while idle) and
/// the virtual manifests every view reads the files through. The user's folders are only listed and read: nothing is ever
/// created, written, renamed, moved or deleted there. Diagnostics name counts and codes only, never paths or file names.
/// </summary>
internal sealed partial class DesktopSessionHost
{
    private readonly object _folderSourceSync = new();
    private FolderSourceCatalog? _folderSourceCatalog;
    private FolderSourceHasher? _folderSourceHasher;
    private IDisposable? _folderSourceManifests;
    private readonly SemaphoreSlim _folderSourceScanGate = new(1, 1);
    private CancellationTokenSource? _folderSourceLifetime;
    private Guid? _folderSourceScanning;
    private FolderSourceScanProgress? _folderSourceScanProgress;
    private Timer? _folderSourceRescanTimer;
    private Timer? _folderSourceNotify;
    private int _folderSourceNotifyPending;
    private bool _folderSourcesStopped;

    /// <summary>Raised (coalesced, on a worker) when folder sources, their files or their digests changed.</summary>
    internal event Action? FolderSourcesChanged;

    /// <summary>At most this often does hashing announce new digests (the media page then rebuilds its index).</summary>
    internal static readonly TimeSpan FolderSourceNotifyInterval = TimeSpan.FromSeconds(5);

    private FolderSourceCatalog FolderSourceCatalog
    {
        get
        {
            lock (_folderSourceSync)
            {
                _folderSourceCatalog ??= new(_profileLease.ProfileDirectory, _profileLease);
                _folderSourceCatalog.LimitBytes = (long)Config.FolderSourceCatalogLimitMiB * 1024 * 1024;
                _folderSourceCatalog.MaxFiles = Config.FolderSourceMaxFiles;
                _folderSourceCatalog.MaxRoots = Config.FolderSourceMaxRoots;
                return _folderSourceCatalog;
            }
        }
    }

    private FolderSourceHasher FolderSourceHasher
    {
        get
        {
            FolderSourceCatalog catalog = FolderSourceCatalog;
            lock (_folderSourceSync)
            {
                if (_folderSourceHasher == null)
                {
                    _folderSourceHasher = new(catalog);
                    _folderSourceHasher.Hashed += QueueFolderSourcesChanged;
                }
                _folderSourceHasher.Parallelism = Config.FolderSourceHashParallelism;
                _folderSourceHasher.MaxFileBytes = (long)Config.FolderSourceHashMaxFileGiB * 1024 * 1024 * 1024;
                // Views hash only for the opened gallery: at most its window (both sides and the opened file) waits (W3-02).
                _folderSourceHasher.ViewQueueLimit = 2 * Config.FolderSourceGalleryHashWindow + 1;
                return _folderSourceHasher;
            }
        }
    }

    /// <summary>Test seam: the shadow catalog, so tests can watch when and where it is read.</summary>
    internal FolderSourceCatalog FolderSourceCatalogForTests => FolderSourceCatalog;

    /// <summary>Test seam: the hasher, so tests can replace how bytes are read.</summary>
    internal FolderSourceHasher FolderSourceHasherForTests => FolderSourceHasher;

    /// <summary>
    /// Registers the virtual manifests and starts the background pass and the rescan timer. Called once the session is ready;
    /// a profile without folder sources does nothing else (no file is created).
    /// </summary>
    internal void StartFolderSources()
    {
        lock (_folderSourceSync)
        {
            if (_folderSourcesStopped || _folderSourceManifests != null) return;
            _folderSourceLifetime = CancellationTokenSource.CreateLinkedTokenSource(_startupCancellation.Token);
        }
        FolderSourceHasher hasher = FolderSourceHasher;
        lock (_folderSourceSync)
        {
            _folderSourceManifests ??= new FolderSourceManifests(FolderSourceCatalog, hasher).Register();
            _folderSourceNotify ??= new Timer(_ => FlushFolderSourcesChanged(), null, Timeout.Infinite, Timeout.Infinite);
            _folderSourceRescanTimer ??= new Timer(_ => _ = RescanAllFolderSourcesOnTimerAsync(), null, Timeout.Infinite, Timeout.Infinite);
        }
        ApplyFolderSourceSettings();
    }

    /// <summary>Applies the folder source settings that act over time: background hashing and the rescan interval.</summary>
    internal void ApplyFolderSourceSettings()
    {
        Timer? timer;
        lock (_folderSourceSync) { if (_folderSourcesStopped || _folderSourceManifests == null) return; timer = _folderSourceRescanTimer; }
        FolderSourceHasher hasher = FolderSourceHasher;
        bool anyRoot;
        try { anyRoot = FolderSourceCatalog.ReadRoots().Count > 0; }
        catch (Exception error) when (FolderSourceFailure(error)) { anyRoot = false; }
        if (Config.FolderSourceBackgroundHashing && anyRoot) hasher.StartBackground();
        else _ = hasher.StopBackgroundAsync();
        TimeSpan interval = Config.FolderSourceRescanMinutes > 0 && anyRoot ? TimeSpan.FromMinutes(Config.FolderSourceRescanMinutes) : Timeout.InfiniteTimeSpan;
        try { timer?.Change(interval, interval); } catch (ObjectDisposedException) { }
    }

    /// <summary>Stops scans and hashing and unregisters the virtual manifests (shutdown).</summary>
    internal async Task StopFolderSourcesAsync()
    {
        FolderSourceHasher? hasher;
        IDisposable? manifests;
        Timer? rescan, notify;
        CancellationTokenSource? lifetime;
        lock (_folderSourceSync)
        {
            if (_folderSourcesStopped) return;
            _folderSourcesStopped = true;
            (hasher, manifests, rescan, notify, lifetime) = (_folderSourceHasher, _folderSourceManifests, _folderSourceRescanTimer, _folderSourceNotify, _folderSourceLifetime);
            _folderSourceManifests = null;
        }
        lifetime?.Cancel();
        rescan?.Dispose();
        notify?.Dispose();
        // Wait for a running scan to notice the stop before the profile is released.
        await _folderSourceScanGate.WaitAsync().ConfigureAwait(false);
        _folderSourceScanGate.Release();
        if (hasher != null) await hasher.DisposeAsync().ConfigureAwait(false);
        manifests?.Dispose();
    }

    /// <summary>The shadow catalog for read-only views (media index); null when it cannot be read (never "no sources").</summary>
    internal FolderSourceCatalogSnapshot? ReadFolderSources()
    {
        if (_disposed) return null;
        try { return FolderSourceCatalog.Read(); }
        catch (Exception error) when (FolderSourceFailure(error)) { return null; }
    }

    /// <summary>The folder sources without their counts (one small query). Throws like <see cref="ListFolderSources"/>.</summary>
    internal IReadOnlyList<FolderSourceRoot> ReadFolderSourceRoots() => FolderSourceCatalog.ReadRoots();

    /// <summary>Why folder sources cannot be used right now (damaged or newer catalog), or <see cref="FolderSourceError.None"/>.</summary>
    internal FolderSourceError FolderSourcesBlocked()
    {
        try { FolderSourceCatalog.ReadRoots(); return FolderSourceError.None; }
        catch (FolderSourceException error) { return error.Error; }
        catch (Exception error) when (FolderSourceFailure(error)) { return FolderSourceError.CatalogDamaged; }
    }

    /// <summary>The folder sources with their counts, in the order they were added.</summary>
    internal IReadOnlyList<FolderSourceView> ListFolderSources()
    {
        FolderSourceCatalog catalog = FolderSourceCatalog;
        IReadOnlyList<FolderSourceRoot> roots = catalog.ReadRoots();
        Dictionary<Guid, FolderSourceCounts> counts = catalog.Counts().ToDictionary(count => count.RootId);
        Guid? scanning;
        lock (_folderSourceSync) scanning = _folderSourceScanning;
        return roots.Select(root => new FolderSourceView(root, counts.GetValueOrDefault(root.Id) ?? new(root.Id, 0, 0, 0), scanning == root.Id))
            .ToArray();
    }

    internal FolderSourceActivity FolderSourceActivity
    {
        get
        {
            Guid? scanning;
            FolderSourceScanProgress? progress;
            lock (_folderSourceSync) { scanning = _folderSourceScanning; progress = _folderSourceScanProgress; }
            int files = 0, hashed = 0;
            try { foreach (FolderSourceCounts count in FolderSourceCatalog.Counts()) { files += count.Files; hashed += count.Hashed; } }
            catch (Exception error) when (FolderSourceFailure(error)) { }
            FolderSourceHashStatus hashing = _folderSourceHasher?.Status ?? new(0, 0, false, false);
            return new(scanning, progress, hashing, files, hashed);
        }
    }

    /// <summary>Why <paramref name="folder"/> cannot become a folder source, or <see cref="FolderSourceError.None"/>.</summary>
    internal FolderSourceError CheckFolderSource(string? folder)
    {
        IReadOnlyList<FolderSourceRoot> roots;
        try { roots = FolderSourceCatalog.ReadRoots(); }
        catch (FolderSourceException error) { return error.Error; }
        catch (Exception error) when (FolderSourceFailure(error)) { return FolderSourceError.CatalogDamaged; }
        if (roots.Count >= Config.FolderSourceMaxRoots) return FolderSourceError.TooManyRoots;
        return FolderSourceValidation.Check(folder, _profileLease.ProfileDirectory, Path.GetFullPath(Config.DownloadPath), roots);
    }

    /// <summary>
    /// Adds a folder source and starts its first scan in the background. Throws <see cref="FolderSourceException"/>. The check of the
    /// folder (a stat, a listing, the catalog) runs with the write on a worker, never on the caller's (UI) thread (W3-12).
    /// </summary>
    internal async Task<FolderSourceRoot> AddFolderSourceAsync(string folder, string name, bool includeSubfolders, CancellationToken token = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureReady();
        (FolderSourceRoot root, int sources) = await Task.Run(() =>
        {
            FolderSourceError check = CheckFolderSource(folder);
            if (check != FolderSourceError.None) throw new FolderSourceException(check);
            FolderSourceCatalog catalog = FolderSourceCatalog;
            FolderSourceRoot added = catalog.AddRoot(folder, name, includeSubfolders, DateTimeOffset.UtcNow, token);
            return (added, catalog.ReadRoots().Count);
        }, token).ConfigureAwait(false);
        _logger.Information("Folder source added ({Sources} in total).", sources);
        QueueFolderSourcesChanged(immediately: true);
        _ = RescanInBackgroundAsync(root.Id);
        return root;
    }

    internal async Task<FolderSourceRoot> RenameFolderSourceAsync(Guid root, string name, CancellationToken token = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        FolderSourceRoot renamed = await Task.Run(() => FolderSourceCatalog.RenameRoot(root, name, token), token).ConfigureAwait(false);
        QueueFolderSourcesChanged(immediately: true);
        return renamed;
    }

    /// <summary>
    /// Removes a folder source from the catalog. The folder and its files stay exactly as they are; tags, favorites and ratings
    /// of its files stay stored and show as orphans, like those of any loose file that is gone.
    /// </summary>
    internal async Task RemoveFolderSourceAsync(Guid root, CancellationToken token = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _folderSourceScanGate.WaitAsync(token).ConfigureAwait(false);
        try { await Task.Run(() => FolderSourceCatalog.RemoveRoot(root, token), token).ConfigureAwait(false); }
        finally { _folderSourceScanGate.Release(); }
        _logger.Information("Folder source removed.");
        QueueFolderSourcesChanged(immediately: true);
        ApplyFolderSourceSettings();
    }

    /// <summary>
    /// Scans one folder source now (one scan at a time; a second request waits). Lists files without hashing them, then
    /// applies the result to the catalog and starts background hashing. Null when the session stopped meanwhile.
    /// </summary>
    internal async Task<FolderSourceScanResult?> RescanFolderSourceAsync(Guid rootId, CancellationToken token = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        CancellationToken lifetime;
        lock (_folderSourceSync) lifetime = _folderSourceLifetime?.Token ?? _startupCancellation.Token;
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime);
        await _folderSourceScanGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            FolderSourceCatalog catalog = FolderSourceCatalog;
            FolderSourceRoot? root = catalog.ReadRoots().FirstOrDefault(candidate => candidate.Id == rootId);
            if (root == null) throw new FolderSourceException(FolderSourceError.UnknownRoot);
            lock (_folderSourceSync) { _folderSourceScanning = rootId; _folderSourceScanProgress = new(0, 0); }
            QueueFolderSourcesChanged(immediately: true);
            ScanProgressSink progress = new(this);
            string[] excluded = { _profileLease.ProfileDirectory, Path.GetFullPath(Config.DownloadPath) };
            int maximum = Math.Min(Config.FolderSourceMaxFiles, FolderSourceCatalog.MaximumItems);
            FolderSourceScan scan = await Task.Run(() => FolderSourceScanner.Scan(root.Path, root.IncludeSubfolders, maximum, excluded, progress, linked.Token),
                linked.Token).ConfigureAwait(false);
            linked.Token.ThrowIfCancellationRequested();
            FolderSourceScanResult result = await Task.Run(() => catalog.ApplyScan(rootId, scan, DateTimeOffset.UtcNow, linked.Token), linked.Token)
                .ConfigureAwait(false);
            _logger.Information("Folder source scanned: {Status}, {Files} files, {Added} new, {Changed} changed, {Replaced} replaced, {Missing} missing, {Unreadable} unreadable folders, {Links} links skipped.",
                result.Status, result.Files, result.Added, result.Changed, result.Replaced, result.Missing, result.UnreadableFolders, result.SkippedLinks);
            return result;
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { return null; }
        finally
        {
            lock (_folderSourceSync) { _folderSourceScanning = null; _folderSourceScanProgress = null; }
            _folderSourceScanGate.Release();
            QueueFolderSourcesChanged(immediately: true);
            if (!lifetime.IsCancellationRequested) ApplyFolderSourceSettings();
        }
    }

    private sealed class ScanProgressSink(DesktopSessionHost host) : IProgress<FolderSourceScanProgress>
    {
        public void Report(FolderSourceScanProgress value)
        {
            lock (host._folderSourceSync) host._folderSourceScanProgress = value;
            host.QueueFolderSourcesChanged();
        }
    }

    private async Task RescanInBackgroundAsync(Guid root)
    {
        try { await RescanFolderSourceAsync(root).ConfigureAwait(false); }
        catch (Exception error) when (FolderSourceFailure(error) || error is OperationCanceledException or InvalidOperationException)
        { _logger.Warning("A folder source scan did not finish: {Error}", Code(error)); }
    }

    private async Task RescanAllFolderSourcesOnTimerAsync()
    {
        IReadOnlyList<FolderSourceRoot> roots;
        try { roots = FolderSourceCatalog.ReadRoots(); }
        catch (Exception error) when (FolderSourceFailure(error)) { return; }
        foreach (FolderSourceRoot root in roots)
        {
            lock (_folderSourceSync) if (_folderSourcesStopped) return;
            await RescanInBackgroundAsync(root.Id).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Hashes one folder file now (a user action may hash a file above the size limit). The outcome says whether the item can
    /// be written to; never throws for the file's own state.
    /// </summary>
    internal Task<FolderSourceHashResult> HashFolderItemAsync(Guid item, FolderSourceHashPriority priority, CancellationToken token = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return FolderSourceHasher.HashAsync(item, priority, token);
    }

    /// <summary>The user did something; background hashing waits until they are idle again.</summary>
    internal void NoteFolderSourceActivity() => _folderSourceHasher?.NoteActivity();

    /// <summary>Pauses or resumes the background pass (this session only).</summary>
    internal bool FolderHashingPaused
    {
        get => _folderSourceHasher?.Paused ?? false;
        set
        {
            FolderSourceHasher.Paused = value;
            QueueFolderSourcesChanged(immediately: true);
        }
    }

    private void QueueFolderSourcesChanged() => QueueFolderSourcesChanged(immediately: false);

    private void QueueFolderSourcesChanged(bool immediately)
    {
        Timer? timer;
        lock (_folderSourceSync) timer = _folderSourceNotify;
        if (immediately || timer == null) { FlushFolderSourcesChanged(); return; }
        if (Interlocked.Exchange(ref _folderSourceNotifyPending, 1) == 0)
            try { timer.Change(FolderSourceNotifyInterval, Timeout.InfiniteTimeSpan); } catch (ObjectDisposedException) { }
    }

    private void FlushFolderSourcesChanged()
    {
        Interlocked.Exchange(ref _folderSourceNotifyPending, 0);
        Action? observers = FolderSourcesChanged;
        if (observers == null) return;
        foreach (Action observer in observers.GetInvocationList())
        {
            try { observer(); }
            catch (Exception exception) { _logger.Error(exception, "A folder source observer failed."); }
        }
    }

    private static bool FolderSourceFailure(Exception error) => error is FolderSourceException or IOException or InvalidDataException
        or UnauthorizedAccessException or ObjectDisposedException or Microsoft.Data.Sqlite.SqliteException;

    private static string Code(Exception error) => error is FolderSourceException folder ? folder.Error.ToString() : error.GetType().Name;
}
