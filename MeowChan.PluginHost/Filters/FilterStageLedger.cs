using MeowChan.Downloader;
using MeowChan.Diagnostics;

namespace MeowChan.PluginHost.Filters;

/// <summary>Per-path upper bounds, including writes in flight. Only confirmed deletion or rename releases a promise.</summary>
internal sealed class FilterStageLedger(string directory, FilterTemporaryBudget budget, long maximum, Action? beforeAdoption = null)
{
    private readonly Dictionary<string, long> _owned = new(PathComparer);
    private Dictionary<string, long> _inventory = new(PathComparer);
    private readonly object _reconcile = new();
    private long _version, _scans, _bytes;
    private int _files;
    private bool _valid;
    private readonly long _diagnosticId = PerformanceDiagnostics.NextId();
    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    internal long ScanCount => Interlocked.Read(ref _scans);
    internal bool IsCurrent { get { lock (budget.Sync) return _valid; } }
    internal long Bytes { get { lock (budget.Sync) return _valid ? _bytes : Math.Max(maximum + 1, _bytes); } }
    internal int Files { get { lock (budget.Sync) return _files; } }
    private bool InDirectory(string path) => PathComparer.Equals(Path.GetDirectoryName(path), directory);
    private IEnumerable<string> PathsCore() => _owned.Keys.Union(_inventory.Keys, PathComparer);
    private long Charge(string path) => Math.Max(_owned.GetValueOrDefault(path), _inventory.GetValueOrDefault(path));
    private bool Contains(string path) => _owned.ContainsKey(path) || _inventory.ContainsKey(path);
    private void Publish() { _version++; budget.Stage(this, _valid ? _bytes : Math.Max(maximum + 1, _bytes)); }
    private void Recount()
    {
        _bytes = 0; _files = 0;
        foreach (string path in PathsCore())
        {
            long charge = Charge(path);
            if (charge > long.MaxValue - _bytes) { _bytes = long.MaxValue; _valid = false; }
            else _bytes += charge;
            if (InDirectory(path)) _files++;
        }
    }
    internal void Check()
    {
        lock (budget.Sync)
            if (!_valid || Files > 1024 || !budget.Fits()) throw Limit();
    }
    internal void Reserve(string path, long bytes)
    {
        lock (budget.Sync)
        {
            Check();
            long before = Math.Max(_owned.GetValueOrDefault(path), _inventory.GetValueOrDefault(path));
            long after = Math.Max(before, bytes);
            if (bytes < 0 || !budget.Fits(after - before)
                || InDirectory(path) && !_owned.ContainsKey(path) && !_inventory.ContainsKey(path) && Files >= 1024) throw Limit();
            bool present = Contains(path);
            _owned[path] = after; _bytes += after - before;
            if (!present && InDirectory(path)) _files++;
            Publish();
        }
    }
    internal void Grow(string path, int bytes)
    {
        lock (budget.Sync) Reserve(path, checked(_owned.GetValueOrDefault(path) + bytes));
    }
    internal void Retain(string path, long bytes)
    {
        lock (budget.Sync) { _owned[path] = Math.Max(_owned.GetValueOrDefault(path), Math.Clamp(bytes, 0, maximum + 1)); Recount(); Publish(); }
    }
    internal void Deleted(string path)
    {
        lock (budget.Sync) { _owned.Remove(path); _inventory.Remove(path); Recount(); Publish(); }
    }
    internal void Renamed(string temporary, string destination)
    {
        lock (budget.Sync)
        {
            long bytes = _owned[temporary];
            _owned.Remove(temporary); _inventory.Remove(temporary); _inventory.Remove(destination);
            _owned[destination] = bytes; Recount(); Publish();
        }
    }
    internal void Invalidate() { lock (budget.Sync) { _valid = false; Publish(); } }
    internal void Reconcile()
    {
        // Serial scans, outside the budget and coordinator state locks. A concurrent
        // reservation invalidates the scan, so adoption never erases an in-flight write.
        lock (_reconcile)
        {
            using var measurement = PerformanceDiagnostics.Measure("filter.inventory", _diagnosticId);
            for (int attempt = 0; attempt < 16; attempt++)
            {
                long version; string[] external;
                lock (budget.Sync) { version = _version; external = _owned.Keys.Where(path => !InDirectory(path)).ToArray(); }
                Dictionary<string, long> observed = new(PathComparer);
                bool valid = true;
                Interlocked.Increment(ref _scans);
                try
                {
                    if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new IOException("Stage directory became a link.");
                    int entries = 0;
                    foreach (string path in Directory.EnumerateFileSystemEntries(directory))
                    {
                        if (++entries > 1024) { valid = false; break; }
                        Observe(path);
                    }
                    foreach (string path in external) Observe(path);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or OverflowException) { valid = false; }
                beforeAdoption?.Invoke(); // Deterministic race seam; production supplies no callback.
                lock (budget.Sync)
                {
                    if (version != _version)
                    {
                        long merged = 0;
                        foreach (string path in observed.Keys.Union(_owned.Keys, PathComparer))
                        {
                            long charge = Math.Max(observed.GetValueOrDefault(path), _owned.GetValueOrDefault(path));
                            if (charge > maximum || merged > maximum - charge) { merged = maximum + 1; break; }
                            merged += charge;
                        }
                        if (!valid || !budget.Fits(Math.Max(0, merged - Bytes))) Invalidate();
                        continue;
                    }
                    _inventory = observed; _valid = valid; Recount(); Publish();
                    PerformanceDiagnostics.Record("filter.inventory.bytes", valid ? "current" : "unknown", _diagnosticId, value: _bytes);
                    PerformanceDiagnostics.Record("filter.inventory.files", valid ? "current" : "unknown", _diagnosticId, value: _files);
                    PerformanceDiagnostics.Record("filter.inventory.scans", "total", _diagnosticId, value: ScanCount);
                    return;
                }
                void Observe(string path)
                {
                    try
                    {
                        FileAttributes attributes = File.GetAttributes(path);
                        if ((attributes & FileAttributes.ReparsePoint) != 0) { observed[path] = 0; return; }
                        if ((attributes & FileAttributes.Directory) != 0) { observed[path] = 0; valid = false; return; }
                        observed[path] = new FileInfo(path).Length;
                    }
                    catch (FileNotFoundException) { }
                    catch (DirectoryNotFoundException) { }
                }
            }
            Invalidate(); // No coherent scan: deny new reservations until a later reconciliation succeeds.
        }
    }
    private static MediaFilterOmissionException Limit() => new(false, "filter_temp_limit", "Filter temporary data or file limit exceeded.");
}
