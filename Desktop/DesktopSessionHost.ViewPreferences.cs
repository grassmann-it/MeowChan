using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Diagnostics;
using MeowChan.Persistence;

namespace MeowChan.Desktop;

internal sealed partial class DesktopSessionHost
{
    private sealed record PreferenceValue(string[] Path, JsonNode? Value, bool Remove, long Revision = 0);
    private enum FinalWriteFamilyState { AwaitingStartup, Prepared, Open, CutDraining, Suspended, TerminalOpen, TerminalNoAuthority, Sealed, Released }
    private sealed class FinalWriteUnavailableException(string reason) : InvalidOperationException("Final preferences unavailable: " + reason) { }

    // Only this pending/parked/failed container is mutable. Frozen segments never share its payload.
    private sealed class FinalWriteBatch
    {
        internal readonly TaskCompletionSource Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly Dictionary<string, PreferenceValue> Preferences = new(StringComparer.Ordinal);
        internal LocalReadingPosition? Position;
        internal long Generation, Revision;
        internal ProfileProducerEpoch.Use? Use;
        internal Exception? Error;
    }
    private sealed class FrozenFinalWriteBatch(long id, FinalWriteBatch source)
    {
        internal readonly long Id = id;
        internal readonly IReadOnlyDictionary<string, PreferenceValue> Preferences = source.Preferences.ToDictionary(
            p => p.Key, p => p.Value with { Path = (string[])p.Value.Path.Clone(), Value = p.Value.Value?.DeepClone() }, StringComparer.Ordinal);
        internal readonly LocalReadingPosition? Position = source.Position;
        internal readonly long Generation = source.Generation, Revision = source.Revision;
        internal readonly TaskCompletionSource Completion = source.Completion;
        internal readonly ProfileProducerEpoch.Use Use = source.Use ?? throw new InvalidOperationException("Missing final-write use.");
    }
    private sealed record FinalWriteWatermark((long Id, Task Task)[] Entries)
    {
        internal Task Completion => Task.WhenAll(Entries.Select(e => e.Task));
    }
    private sealed class FinalWriteFlushRegistration(int slot)
    {
        internal readonly int Slot = slot;
        // Bound only under _finalWrites. Slot retention is separately guarded by _admission.
        internal FinalWriteWatermark? Watermark;
        internal bool CommandEnded, SegmentsEnded;
        internal int ObservationStarted;
    }
    private sealed record FinalWriteShutdownPlan(Task Restoration, TaskCompletionSource[] Rejected);
    private readonly object _finalWrites = new();
    private readonly Dictionary<string, FinalWriteBatch> _pendingFinalWrites = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FrozenFinalWriteBatch> _activeFinalWrites = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FinalWriteBatch> _failedFinalWrites = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FinalWriteBatch> _parkedFinalWrites = new(StringComparer.Ordinal);
    // Three fixed waiting slots per unit: two command boundaries and one cut boundary.
    private readonly Dictionary<string, FrozenFinalWriteBatch?[]> _frozenFinalWrites = new(StringComparer.Ordinal)
        { ["view"] = new FrozenFinalWriteBatch?[3], ["reading"] = new FrozenFinalWriteBatch?[3] };
    private readonly Dictionary<string, Task> _finalWriteLoops = new(StringComparer.Ordinal);
    private FinalWriteFamilyState _finalWriteFamilyState;
    private ProfileProducerEpoch? _finalWriteEpoch, _cutFinalWriteEpoch;
    private ProfileProducerEpoch.Use? _terminalFinalWriteUse;
    private Exception? _finalWriteSuspensionError;
    private Task[] _terminalRejectedFinalWrites = Array.Empty<Task>();
    private FinalWriteFlushRegistration[] _cutFlushRegistrations = Array.Empty<FinalWriteFlushRegistration>();
    private long _finalWriteRevision, _finalWriteBatchId;
    private Config? _confirmedConfig;
    private bool _finalWritesSealed;
    private int _finalWriteStartupState;
    internal Action<string>? BeforeFinalWriteIoForTests { get; set; }
    internal Action? BeforeCommandFinalWriteBindForTests { get; set; }
    internal Action? AfterCommandFinalWriteBindForTests { get; set; }
    internal Action? BeforeTerminalFinalWriteSettlementForTests { get; set; }
    internal Action? AtTerminalFinalWriteSettlementForTests { get; set; }

    private void CompleteStartupFinalWrites(bool permittedReady)
    {
        TaskCompletionSource[] rejected = Array.Empty<TaskCompletionSource>();
        lock (_finalWrites)
        {
            if (_finalWriteStartupState != 0) return;
            _finalWriteStartupState = permittedReady ? 1 : 2;
            if (permittedReady)
                foreach (string unit in _frozenFinalWrites.Keys) StartFinalWriteLoop(unit);
            else
            {
                rejected = _pendingFinalWrites.Values.Select(b => b.Completion).ToArray();
                foreach (var pair in _pendingFinalWrites) MergeFailedFinalWrite(pair.Key, pair.Value.Preferences, pair.Value.Position, pair.Value.Generation, pair.Value.Revision);
                _pendingFinalWrites.Clear();
                _terminalRejectedFinalWrites = rejected.Select(c => c.Task).ToArray();
            }
        }
        foreach (var completion in rejected) completion.TrySetException(new FinalWriteUnavailableException("Startup did not reach Ready."));
    }
    internal (int Active, int Pending, int Failed) PreferenceWriteCounts
    { get { lock (_finalWrites) return (_activeFinalWrites.Count, _pendingFinalWrites.Count, _failedFinalWrites.Count); } }
    internal (int Frozen, int Parked, bool HasEpoch) FinalWriteAdmissionCounts
    { get { lock (_finalWrites) return (_frozenFinalWrites.Values.Sum(a => a.Count(b => b != null)), _parkedFinalWrites.Count, _finalWriteEpoch != null); } }
    private static readonly HashSet<string> LegacyProfileAliases = new(StringComparer.Ordinal)
    { "CompactDensity", "ArchiveMediaTiles", "ReadingFontSize", "ArchiveThreadLayout", "ArchiveSplitWidth", "ColumnWidths", "ColumnVisibility", "ColumnOrder" };

    private void PublishConfig(Config candidate)
    {
        Config.CopyFrom(candidate);
        Volatile.Write(ref _confirmedConfig, candidate.Clone());
    }

    internal Task SaveViewPreferencesAsync(Action<Config> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        lock (_finalWrites)
        {
            if (_finalWritesSealed || _finalWriteFamilyState is FinalWriteFamilyState.TerminalNoAuthority or FinalWriteFamilyState.Released)
                return Task.FromException(new InvalidOperationException("Final preferences unavailable: Stopping without authority."));
            Config confirmed = (Volatile.Read(ref _confirmedConfig) ?? Config).Clone();
            Config desired = confirmed.Clone();
            var known = new Dictionary<string, PreferenceValue>(StringComparer.Ordinal);
            void Include(IReadOnlyDictionary<string, PreferenceValue> values)
            {
                foreach (var p in values)
                    if (!known.TryGetValue(p.Key, out var old) || old.Revision <= p.Value.Revision) known[p.Key] = p.Value;
            }
            if (_activeFinalWrites.TryGetValue("view", out var active)) Include(active.Preferences);
            foreach (var frozen in _frozenFinalWrites["view"]) if (frozen != null) Include(frozen.Preferences);
            if (_failedFinalWrites.TryGetValue("view", out var failed)) Include(failed.Preferences);
            if (_pendingFinalWrites.TryGetValue("view", out var pending)) Include(pending.Preferences);
            if (_parkedFinalWrites.TryGetValue("view", out var parked)) Include(parked.Preferences);
            ApplyPreferenceValues(desired, known);
            Config after = desired.Clone(); change(after); after.Validate();
            JsonNode? afterNode = JsonSerializer.SerializeToNode(after, ConfigJsonContext.Default.Config);
            var changed = new Dictionary<string, PreferenceValue>(StringComparer.Ordinal);
            DiffPreferences(JsonSerializer.SerializeToNode(desired, ConfigJsonContext.Default.Config), afterNode, Array.Empty<string>(), changed);
            if (changed.Count == 0 && failed == null)
            {
                if (_finalWriteSuspensionError != null) return Task.FromException(_finalWriteSuspensionError);
                return parked?.Completion.Task ?? pending?.Completion.Task
                    ?? _frozenFinalWrites["view"].LastOrDefault(b => b != null)?.Completion.Task ?? active?.Completion.Task ?? Task.CompletedTask;
            }
            var values = new Dictionary<string, PreferenceValue>(StringComparer.Ordinal);
            DiffPreferences(JsonSerializer.SerializeToNode(confirmed, ConfigJsonContext.Default.Config), afterNode, Array.Empty<string>(), values);
            // A revert must survive either a successful or failed unconfirmed predecessor.
            Config predecessor = confirmed.Clone();
            if (active != null) ApplyPreferenceValues(predecessor, active.Preferences);
            foreach (var frozen in _frozenFinalWrites["view"])
            { if (frozen != null) ApplyPreferenceValues(predecessor, frozen.Preferences); }
            DiffPreferences(JsonSerializer.SerializeToNode(predecessor, ConfigJsonContext.Default.Config), afterNode, Array.Empty<string>(), values);
            FinalWriteBatch batch = PendingFinalWrite("view");
            long revision = ++_finalWriteRevision;
            batch.Preferences.Clear();
            foreach (var pair in values)
                batch.Preferences[pair.Key] = pair.Value with { Revision = changed.ContainsKey(pair.Key) || !known.TryGetValue(pair.Key, out var prior) ? revision : prior.Revision };
            batch.Revision = revision;
            StartFinalWriteLoop("view");
            return _finalWriteSuspensionError != null ? Task.FromException(_finalWriteSuspensionError) : batch.Completion.Task;
        }
    }

    private bool FinalWriteAuthorityOpen => _finalWriteFamilyState is FinalWriteFamilyState.Open or FinalWriteFamilyState.TerminalOpen;
    // Caller owns _finalWrites. No acquisition from the kernel: only a use of the installed exact epoch.
    private FinalWriteBatch PendingFinalWrite(string unit)
    {
        var drafts = FinalWriteAuthorityOpen || _finalWriteStartupState == 0
            ? _pendingFinalWrites : _parkedFinalWrites;
        if (drafts.TryGetValue(unit, out var pending)) return pending;
        pending = new();
        if (FinalWriteAuthorityOpen) pending.Use = (_finalWriteEpoch ?? throw new InvalidOperationException("No final-write epoch.")).Enter();
        if (_failedFinalWrites.Remove(unit, out var failed)) CopyDraft(failed, pending);
        drafts.Add(unit, pending);
        return pending;
    }
    private static void CopyDraft(FinalWriteBatch from, FinalWriteBatch to)
    {
        foreach (var p in from.Preferences)
            if (!to.Preferences.TryGetValue(p.Key, out var old) || old.Revision <= p.Value.Revision) to.Preferences[p.Key] = p.Value;
        if (from.Position != null && (to.Position == null || from.Generation > to.Generation || from.Generation == to.Generation && from.Revision > to.Revision))
        { to.Position = from.Position; to.Generation = from.Generation; to.Revision = from.Revision; }
    }
    private void MergeFailedFinalWrite(string unit, IReadOnlyDictionary<string, PreferenceValue> values, LocalReadingPosition? position, long generation, long revision)
    {
        if (!_failedFinalWrites.TryGetValue(unit, out var failed)) _failedFinalWrites[unit] = failed = new();
        foreach (var p in values)
            if (!failed.Preferences.TryGetValue(p.Key, out var old) || old.Revision <= p.Value.Revision) failed.Preferences[p.Key] = p.Value;
        if (position != null && (failed.Position == null || generation > failed.Generation || generation == failed.Generation && revision > failed.Revision))
        { failed.Position = position; failed.Generation = generation; failed.Revision = revision; }
    }
    private FrozenFinalWriteBatch FreezePendingFinalWrite(string unit)
    {
        FinalWriteBatch pending = _pendingFinalWrites[unit];
        var frozen = new FrozenFinalWriteBatch(++_finalWriteBatchId, pending);
        _pendingFinalWrites.Remove(unit);
        return frozen;
    }
    private void FreezeWaitingFinalWrites()
    {
        foreach (string unit in _pendingFinalWrites.Keys.ToArray())
        {
            int slot = Array.FindIndex(_frozenFinalWrites[unit], b => b == null);
            if (slot < 0) throw new InvalidOperationException("Final-write frozen capacity exceeded.");
            _frozenFinalWrites[unit][slot] = FreezePendingFinalWrite(unit);
            StartFinalWriteLoop(unit);
        }
    }
    private FinalWriteWatermark SnapshotFinalWriteWatermark()
    {
        var entries = _activeFinalWrites.Values.Concat(_frozenFinalWrites.Values.SelectMany(a => a).OfType<FrozenFinalWriteBatch>())
            .Select(b => (b.Id, b.Completion.Task)).ToArray();
        return new(entries);
    }
    private void RetryFailedFinalWrites()
    {
        foreach (string unit in _failedFinalWrites.Keys.ToArray()) { PendingFinalWrite(unit); StartFinalWriteLoop(unit); }
    }
    private void StartFinalWriteLoop(string unit)
    {
        if (_finalWriteStartupState == 1 && !_finalWriteLoops.ContainsKey(unit)
            && (_pendingFinalWrites.ContainsKey(unit) || _frozenFinalWrites[unit].Any(b => b != null)))
            _finalWriteLoops.Add(unit, Task.Run(() => RunFinalWriteLoopAsync(unit)));
    }
    private async Task RunFinalWriteLoopAsync(string unit)
    {
        while (true)
        {
            FrozenFinalWriteBatch batch;
            lock (_finalWrites)
            {
                var slots = _frozenFinalWrites[unit];
                if (slots[0] is { } first)
                { batch = first; for (int i = 1; i < slots.Length; i++) slots[i - 1] = slots[i]; slots[^1] = null; }
                else if (_pendingFinalWrites.ContainsKey(unit)) batch = FreezePendingFinalWrite(unit);
                else { _finalWriteLoops.Remove(unit); return; }
                _activeFinalWrites[unit] = batch;
            }
            Exception? failure = null;
            try
            {
                using var measurement = PerformanceDiagnostics.Measure("host.preferences." + unit, epoch: _epoch);
                await _commands.WaitAsync().ConfigureAwait(false);
                try
                {
                    EnsureFinalWriteAllowed();
                    BeforeFinalWriteIoForTests?.Invoke(unit);
                    ProfileAdmissionToken admission = batch.Use.Admission ?? throw new InvalidOperationException("Missing final-write authority.");
                    if (unit == "view")
                        _profileLease.Admission.RunWithToken(admission, () =>
                        {
                            Config candidate = Config.Clone(); ApplyPreferenceValues(candidate, batch.Preferences); candidate.Validate();
                            candidate.SaveConfigAdmitted(_profileLease.ProfileDirectory, admission); PublishConfig(candidate); return true;
                        }, ProfileAdmissionKind.Ordinary);
                    else await WriteReadingPositionCoreAsync(batch.Position!, batch.Generation, admission).ConfigureAwait(false);
                }
                finally { _commands.Release(); }
                SignalChanged();
            }
            catch (Exception error)
            {
                failure = error;
                _logger.Error(error, "Could not persist final preferences for {Unit}; retaining the draft.", unit);
            }
            finally
            {
                lock (_finalWrites)
                {
                    _activeFinalWrites.Remove(unit);
                    if (failure != null)
                    {
                        MergeFailedFinalWrite(unit, batch.Preferences, batch.Position, batch.Generation, batch.Revision);
                        _failedFinalWrites[unit].Error = failure;
                    }
                    else if (_failedFinalWrites.TryGetValue(unit, out var failed))
                    {
                        foreach (var p in batch.Preferences)
                            if (failed.Preferences.TryGetValue(p.Key, out var old) && old.Revision <= p.Value.Revision) failed.Preferences.Remove(p.Key);
                        if (batch.Position != null && failed.Generation <= batch.Generation && failed.Revision <= batch.Revision) failed.Position = null;
                        if (failed.Preferences.Count == 0 && failed.Position == null) _failedFinalWrites.Remove(unit);
                    }
                }
                batch.Use.Dispose(); // Real I/O, publish and own cleanup have returned.
            }
            if (failure == null) batch.Completion.TrySetResult(); else batch.Completion.TrySetException(failure);
        }
    }

    private void BindCommandFinalWriteFlush(FinalWriteFlushRegistration registration)
    {
        lock (_finalWrites)
        {
            if (registration.Watermark == null)
            {
                if (FinalWriteAuthorityOpen)
                { RetryFailedFinalWrites(); FreezeWaitingFinalWrites(); registration.Watermark = SnapshotFinalWriteWatermark(); }
                else registration.Watermark = new(new[] { (0L, Task.FromException(new FinalWriteUnavailableException("Family suspended."))) });
            }
        }
        ObserveCommandFinalWriteFence(registration);
    }
    private Task FlushCommandPreferencesAsync(FinalWriteFlushRegistration registration)
    {
        BindCommandFinalWriteFlush(registration);
        lock (_finalWrites) return registration.Watermark!.Completion;
    }
    private void ObserveCommandFinalWriteFence(FinalWriteFlushRegistration registration)
    {
        if (Interlocked.Exchange(ref registration.ObservationStarted, 1) != 0) return;
        _ = EndFenceAsync();
        async Task EndFenceAsync()
        {
            try { await registration.Watermark!.Completion.ConfigureAwait(false); } catch { /* Caller observes the same watermark. */ }
            lock (_admission)
            {
                registration.SegmentsEnded = true;
                ReleaseFinalWriteFlushSlotIfEnded(registration);
            }
        }
    }
    internal Task FlushPreferencesAsync()
    {
        lock (_finalWrites)
        {
            if (!FinalWriteAuthorityOpen)
            {
                if (_finalWriteSuspensionError != null || _finalWriteFamilyState is FinalWriteFamilyState.TerminalNoAuthority or FinalWriteFamilyState.Sealed or FinalWriteFamilyState.Released)
                    return Task.FromException(_finalWriteSuspensionError ?? new FinalWriteUnavailableException("Family closed."));
                return Task.WhenAll(SnapshotFinalWriteWatermark().Entries.Select(e => e.Task)
                    .Concat(_pendingFinalWrites.Values.Select(b => b.Completion.Task)).Concat(_parkedFinalWrites.Values.Select(b => b.Completion.Task))
                    .Concat(_failedFinalWrites.Values.Where(b => b.Error != null).Select(b => Task.FromException(b.Error!))));
            }
            RetryFailedFinalWrites();
            return Task.WhenAll(SnapshotFinalWriteWatermark().Entries.Select(e => e.Task).Concat(_pendingFinalWrites.Values.Select(b => b.Completion.Task)));
        }
    }
    private async Task SealFinalWritesAsync()
    {
        Task[] loops; Task writes;
        lock (_finalWrites)
        {
            bool hadAuthority = FinalWriteAuthorityOpen;
            if (hadAuthority) RetryFailedFinalWrites();
            _finalWritesSealed = true;
            _finalWriteFamilyState = FinalWriteFamilyState.Sealed;
            writes = Task.WhenAll(SnapshotFinalWriteWatermark().Entries.Select(e => e.Task)
                .Concat(_pendingFinalWrites.Values.Select(b => b.Completion.Task)).Concat(_terminalRejectedFinalWrites)
                .Concat(!hadAuthority ? _failedFinalWrites.Values.Where(b => b.Error != null).Select(b => Task.FromException(b.Error!)) : Array.Empty<Task>()));
            loops = _finalWriteLoops.Values.ToArray();
        }
        AfterFinalWriteSealForTests?.Invoke();
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
