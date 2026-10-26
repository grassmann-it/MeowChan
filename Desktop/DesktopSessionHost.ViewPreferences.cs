using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Diagnostics;

namespace MeowChan.Desktop;

internal sealed partial class DesktopSessionHost
{
    private sealed record PreferenceValue(string[] Path, JsonNode? Value, bool Remove);
    private sealed class FinalWriteBatch
    {
        internal readonly TaskCompletionSource Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly Dictionary<string, PreferenceValue> Preferences = new(StringComparer.Ordinal);
        internal LocalReadingPosition? Position;
        internal long Generation;
    }
    private readonly object _finalWrites = new();
    // Two fixed Save units: config.json and the single conscious reading position. Each has one active batch
    // and at most one pending value; pending callers share its completion instead of accumulating waiter nodes.
    private readonly Dictionary<string, FinalWriteBatch> _pendingFinalWrites = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FinalWriteBatch> _activeFinalWrites = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FinalWriteBatch> _failedFinalWrites = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task> _finalWriteLoops = new(StringComparer.Ordinal);
    private Config? _confirmedConfig;
    private bool _finalWritesSealed;
    internal (int Active, int Pending, int Failed) PreferenceWriteCounts
    { get { lock (_finalWrites) return (_activeFinalWrites.Count, _pendingFinalWrites.Count, _failedFinalWrites.Count); } }
    private static readonly HashSet<string> LegacyProfileAliases = new(StringComparer.Ordinal)
    { "CompactDensity", "ArchiveMediaTiles", "ReadingFontSize", "ArchiveThreadLayout", "ArchiveSplitWidth", "ColumnWidths", "ColumnVisibility", "ColumnOrder" };

    // Called only by a background command owner after a successful durable publication.
    private void PublishConfig(Config candidate)
    {
        Config.CopyFrom(candidate);
        Volatile.Write(ref _confirmedConfig, candidate.Clone());
    }

    /// <summary>
    /// Captures the preference edit without IO; config is the existing save unit. Coalescing merges the latest
    /// value per changed field, including dictionary removals, so independent edits and mode switches survive.
    /// Completion confirms persistence of this value or a newer value in the same pending batch. Failure retains
    /// that batch for retry and faults its observers; it never publishes the requested value as confirmed.
    /// </summary>
    internal Task SaveViewPreferencesAsync(Action<Config> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        lock (_finalWrites)
        {
            if (_finalWritesSealed) return Task.FromException(new InvalidOperationException("Final preference writes are closed."));
            Config confirmed = (Volatile.Read(ref _confirmedConfig) ?? Config).Clone();
            Config before = confirmed.Clone();
            if (_activeFinalWrites.TryGetValue("view", out var active)) ApplyPreferenceValues(before, active.Preferences);
            Config activeValue = before.Clone();
            if (_failedFinalWrites.TryGetValue("view", out var failed)) ApplyPreferenceValues(before, failed.Preferences);
            if (_pendingFinalWrites.TryGetValue("view", out var pending)) ApplyPreferenceValues(before, pending.Preferences);
            Config after = before.Clone();
            change(after);
            after.Validate();
            JsonNode? beforeNode = JsonSerializer.SerializeToNode(before, ConfigJsonContext.Default.Config);
            JsonNode? afterNode = JsonSerializer.SerializeToNode(after, ConfigJsonContext.Default.Config);
            if (JsonNode.DeepEquals(beforeNode, afterNode) && !_failedFinalWrites.ContainsKey("view"))
                return pending?.Completion.Task ?? active?.Completion.Task ?? Task.CompletedTask;
            var values = new Dictionary<string, PreferenceValue>(StringComparer.Ordinal);
            // Normalize the complete desired pending value against BOTH possible predecessors: confirmed
            // storage and the active write. This retains a deliberate revert of an active value, while
            // removing tombstones for never-committed keys instead of retaining an unbounded action history.
            DiffPreferences(JsonSerializer.SerializeToNode(confirmed, ConfigJsonContext.Default.Config), afterNode,
                Array.Empty<string>(), values);
            DiffPreferences(JsonSerializer.SerializeToNode(activeValue, ConfigJsonContext.Default.Config), afterNode,
                Array.Empty<string>(), values);
            FinalWriteBatch batch = PendingFinalWrite("view");
            batch.Preferences.Clear();
            foreach (var pair in values) batch.Preferences[pair.Key] = pair.Value;
            StartFinalWriteLoop("view");
            return batch.Completion.Task;
        }
    }

    private FinalWriteBatch PendingFinalWrite(string unit)
    {
        if (_pendingFinalWrites.TryGetValue(unit, out var pending)) return pending;
        pending = new();
        if (_failedFinalWrites.Remove(unit, out var failed))
        {
            foreach (var pair in failed.Preferences) pending.Preferences[pair.Key] = pair.Value;
            pending.Position = failed.Position; pending.Generation = failed.Generation;
        }
        _pendingFinalWrites.Add(unit, pending);
        return pending;
    }

    private void StartFinalWriteLoop(string unit)
    {
        if (!_finalWriteLoops.ContainsKey(unit))
            _finalWriteLoops.Add(unit, Task.Run(() => RunFinalWriteLoopAsync(unit)));
    }

    private async Task RunFinalWriteLoopAsync(string unit)
    {
        while (true)
        {
            FinalWriteBatch batch;
            lock (_finalWrites)
            {
                if (!_pendingFinalWrites.Remove(unit, out batch!)) { _finalWriteLoops.Remove(unit); return; }
                _activeFinalWrites[unit] = batch;
            }
            try
            {
                using var measurement = PerformanceDiagnostics.Measure("host.preferences." + unit, epoch: _epoch);
                await _commands.WaitAsync().ConfigureAwait(false);
                try
                {
                    EnsureFinalWriteAllowed();
                    if (unit == "view")
                    {
                        Config candidate = Config.Clone();
                        ApplyPreferenceValues(candidate, batch.Preferences);
                        candidate.Validate();
                        candidate.SaveConfig(_profileLease.ProfileDirectory);
                        PublishConfig(candidate);
                    }
                    else await WriteReadingPositionCoreAsync(batch.Position!, batch.Generation).ConfigureAwait(false);
                }
                finally { _commands.Release(); }
                lock (_finalWrites) _activeFinalWrites.Remove(unit);
                batch.Completion.TrySetResult();
                SignalChanged();
            }
            catch (Exception error)
            {
                _logger.Error(error, "Could not persist final preferences for {Unit}; retaining the draft.", unit);
                lock (_finalWrites)
                {
                    _activeFinalWrites.Remove(unit);
                    // A newer pending batch must also contain fields whose failed write never committed.
                    if (_pendingFinalWrites.TryGetValue(unit, out var newer))
                    {
                        foreach (var pair in batch.Preferences)
                            newer.Preferences.TryAdd(pair.Key, pair.Value);
                    }
                    else _failedFinalWrites[unit] = batch;
                }
                batch.Completion.TrySetException(error);
            }
        }
    }

    /// <summary>Waits for the final values, retrying a retained failed draft once per flush request.</summary>
    internal Task FlushPreferencesAsync()
    {
        lock (_finalWrites)
        {
            foreach (string unit in _failedFinalWrites.Keys.ToArray())
            {
                PendingFinalWrite(unit);
                StartFinalWriteLoop(unit);
            }
            return Task.WhenAll(_activeFinalWrites.Values.Concat(_pendingFinalWrites.Values)
                .Select(batch => batch.Completion.Task).ToArray());
        }
    }

    private async Task SealFinalWritesAsync()
    {
        Task writes;
        Task[] loops;
        lock (_finalWrites)
        {
            _finalWritesSealed = true;
            writes = FlushPreferencesAsync();
            loops = _finalWriteLoops.Values.ToArray();
        }
        await Task.WhenAll(loops.Append(writes)).ConfigureAwait(false);
    }

    private static void DiffPreferences(JsonNode? before, JsonNode? after, string[] path,
        Dictionary<string, PreferenceValue> values)
    {
        if (JsonNode.DeepEquals(before, after)) return;
        if (before is JsonObject oldObject && after is JsonObject newObject)
        {
            foreach (string key in oldObject.Select(pair => pair.Key).Union(newObject.Select(pair => pair.Key)))
            {
                if (path.Length == 0 && LegacyProfileAliases.Contains(key)) continue;
                string[] next = path.Append(key).ToArray();
                if (!newObject.ContainsKey(key)) values[JsonSerializer.Serialize(next)] = new(next, null, true);
                else DiffPreferences(oldObject[key], newObject[key], next, values);
            }
        }
        else values[JsonSerializer.Serialize(path)] = new(path, after?.DeepClone(), false);
    }

    private static void ApplyPreferenceValues(Config config, IReadOnlyDictionary<string, PreferenceValue> values)
    {
        if (values.Count == 0) return;
        JsonObject document = (JsonObject)JsonSerializer.SerializeToNode(config, ConfigJsonContext.Default.Config)!;
        foreach (PreferenceValue value in values.Values)
        {
            JsonObject parent = document;
            foreach (string key in value.Path.Take(value.Path.Length - 1))
            {
                if (parent[key] is not JsonObject child) { child = new(); parent[key] = child; }
                parent = child;
            }
            if (value.Remove) parent.Remove(value.Path[^1]);
            else parent[value.Path[^1]] = value.Value?.DeepClone();
        }
        Config updated = document.Deserialize(ConfigJsonContext.Default.Config)
            ?? throw new InvalidOperationException("Invalid preference draft.");
        config.CopyFrom(updated);
    }
}
