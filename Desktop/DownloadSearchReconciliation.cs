using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using MeowChan.Diagnostics;

namespace MeowChan.Desktop;

/// <summary>
/// A bounded literal-filter/result application over stable passive rows. Freeze the source capture
/// for this pass; the shell retains only its newest pending projection/search revision. Every match,
/// removal, insertion and reorder lookup consumes an operation, including long reorder searches.
/// The shell gates row commands and restores retained grid selection on completion. No focus or
/// collection Reset is performed; beforeRemove lets the shell remove only genuine non-results from
/// selection and record its existing filter-exclusion revision.
/// </summary>
internal sealed class DownloadSearchReconciliation<T>(IReadOnlyList<T> source, ObservableCollection<T> visible,
    Func<T, bool> matches, Action<T>? beforeRemove = null, long epoch = 0, long revision = 0,
    TimeProvider? clock = null) where T : class
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly List<T> _result = [];
    private readonly HashSet<T> _included = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<T> _present = new(ReferenceEqualityComparer.Instance);
    private readonly long _request = PerformanceDiagnostics.NextId();
    private int _phase, _index, _search = -1;
    internal bool IsCompleted => _phase == 3;
    internal bool HasFiltered => _phase != 0;
    internal IReadOnlyList<T> Result => _result;
    internal bool Includes(T row) => _included.Contains(row);
    internal long Operations { get; private set; }
    internal long Slices { get; private set; }
    internal TimeSpan LongestSlice { get; private set; }

    internal bool Step(int maxOperations = 64, TimeSpan? timeBudget = null)
    {
        if (maxOperations <= 0) throw new ArgumentOutOfRangeException(nameof(maxOperations));
        TimeSpan budget = timeBudget ?? TimeSpan.FromMilliseconds(4);
        if (budget <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeBudget));
        if (IsCompleted) return true;
        using var measurement = PerformanceDiagnostics.Measure("ui.download-search.slice", _request, epoch: epoch, revision: revision);
        long started = _clock.GetTimestamp();
        int operations = 0;
        try
        {
            while (operations < maxOperations && (operations == 0 || _clock.GetElapsedTime(started) < budget))
            {
                if (_phase == 0)
                {
                    if (_index == source.Count) { _phase = 1; _index = visible.Count - 1; continue; }
                    T row = source[_index++];
                    if (matches(row))
                    {
                        if (!_included.Add(row)) throw new ArgumentException("Duplicate row in search capture.", nameof(source));
                        _result.Add(row);
                    }
                }
                else if (_phase == 1)
                {
                    if (_index < 0) { _phase = 2; _index = 0; continue; }
                    T row = visible[_index];
                    if (!_included.Contains(row)) { beforeRemove?.Invoke(row); visible.RemoveAt(_index); }
                    else _present.Add(row);
                    _index--;
                }
                else
                {
                    if (_index == _result.Count) { _phase = 3; break; }
                    T row = _result[_index];
                    if (_search >= 0)
                    {
                        if (_search >= visible.Count) throw new InvalidOperationException("Search rows changed during reconciliation.");
                        if (ReferenceEquals(visible[_search], row))
                        { visible.Move(_search, _index++); _search = -1; }
                        else _search++;
                    }
                    else if (_index < visible.Count && ReferenceEquals(visible[_index], row)) _index++;
                    else if (!_present.Contains(row)) { visible.Insert(_index++, row); _present.Add(row); }
                    else _search = _index + 1;
                }
                operations++;
            }
            if (_phase == 2 && _index == _result.Count) _phase = 3;
            return IsCompleted;
        }
        finally
        {
            Operations += operations;
            Slices++;
            TimeSpan elapsed = _clock.GetElapsedTime(started);
            if (elapsed > LongestSlice) LongestSlice = elapsed;
            if (IsCompleted)
                PerformanceDiagnostics.Record("ui.download-search", "applied", _request, epoch: epoch, revision: revision, value: _result.Count);
        }
    }
}
