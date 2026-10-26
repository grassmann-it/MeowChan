using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using MeowChan.Diagnostics;

namespace MeowChan.Desktop;

/// <summary>
/// A dispatcher-owned application of ONE immutable capture, in bounded slices. The owner keeps row
/// commands/filtering/inspector work gated until completion and retains only the newest pending capture.
/// Existing rows retain identity and insertion order (including a DataGrid's independent user sort).
/// No Reset, replacement, selection or focus operation is emitted. Only removed rows are disposed.
/// </summary>
internal sealed class IncrementalRowReconciliation<TKey, TEntry, TRow> where TKey : notnull where TRow : class
{
    private readonly IReadOnlyList<TEntry> _entries;
    private readonly IDictionary<TKey, TRow> _rows;
    private readonly ObservableCollection<TRow> _visible;
    private readonly Func<TEntry, TKey> _entryKey;
    private readonly Func<TRow, TKey> _rowKey;
    private readonly Func<TEntry, TRow> _create;
    private readonly Action<TRow, TEntry> _update;
    private readonly Action<TRow> _remove;
    private readonly HashSet<TKey> _included;
    private readonly TimeProvider _clock;
    private readonly long _request = PerformanceDiagnostics.NextId();
    private readonly long _epoch, _version;
    private int _phase, _index;
    internal bool IsCompleted => _phase == 3;
    internal long Operations { get; private set; }
    internal long Slices { get; private set; }
    internal TimeSpan LongestSlice { get; private set; }

    internal IncrementalRowReconciliation(IReadOnlyList<TEntry> entries, IDictionary<TKey, TRow> rows,
        ObservableCollection<TRow> visible, Func<TEntry, TKey> entryKey, Func<TRow, TKey> rowKey,
        Func<TEntry, TRow> create, Action<TRow, TEntry> update, Action<TRow> remove,
        long epoch = 0, long version = 0, TimeProvider? clock = null, IEqualityComparer<TKey>? comparer = null)
    {
        _entries = entries;
        _rows = rows;
        _visible = visible;
        _entryKey = entryKey;
        _rowKey = rowKey;
        _create = create;
        _update = update;
        _remove = remove;
        _clock = clock ?? TimeProvider.System;
        _included = new HashSet<TKey>(comparer);
        _epoch = epoch;
        _version = version;
    }

    /// <summary>
    /// At most maxOperations row operations, yielding after the time budget. One indivisible row
    /// notification may exceed the budget; its measured cost is exposed instead of claiming a hard deadline.
    /// The owner must not mutate the dictionaries, collection or capture between slices.
    /// </summary>
    internal bool Step(int maxOperations = 64, TimeSpan? timeBudget = null)
    {
        if (maxOperations <= 0) throw new ArgumentOutOfRangeException(nameof(maxOperations));
        TimeSpan budget = timeBudget ?? TimeSpan.FromMilliseconds(4);
        if (budget <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeBudget));
        if (IsCompleted) return true;
        using var measurement = PerformanceDiagnostics.Measure("ui.rows.slice", _request, epoch: _epoch, revision: _version);
        long started = _clock.GetTimestamp();
        int operations = 0;
        try
        {
            while (operations < maxOperations && (operations == 0 || _clock.GetElapsedTime(started) < budget))
            {
                if (_phase == 0)
                {
                    if (_index == _entries.Count) { _phase = 1; _index = _visible.Count - 1; continue; }
                    if (!_included.Add(_entryKey(_entries[_index++])))
                        throw new ArgumentException("A capture contains a duplicate row identity.", nameof(_entries));
                }
                else if (_phase == 1)
                {
                    if (_index < 0) { _phase = 2; _index = 0; continue; }
                    TRow row = _visible[_index];
                    TKey key = _rowKey(row);
                    if (!_included.Contains(key))
                    {
                        _visible.RemoveAt(_index);
                        _rows.Remove(key);
                        _remove(row);
                    }
                    _index--;
                }
                else
                {
                    if (_index == _entries.Count) { _phase = 3; break; }
                    TEntry entry = _entries[_index++];
                    TKey key = _entryKey(entry);
                    if (!_rows.TryGetValue(key, out TRow? row))
                    {
                        row = _create(entry);
                        _rows.Add(key, row);
                        _visible.Add(row);
                    }
                    else _update(row, entry);
                }
                operations++;
            }
            // Mark the final entry complete in the same slice, even when the operation limit was hit.
            if (_phase == 2 && _index == _entries.Count) _phase = 3;
            return IsCompleted;
        }
        finally
        {
            Operations += operations;
            Slices++;
            TimeSpan elapsed = _clock.GetElapsedTime(started);
            if (elapsed > LongestSlice) LongestSlice = elapsed;
            if (IsCompleted)
                PerformanceDiagnostics.Record("ui.rows", "applied", _request, epoch: _epoch, revision: _version, value: _entries.Count);
        }
    }
}
