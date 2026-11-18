using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace MeowChan.Desktop;

internal enum WatchUndoOutcome { Restored, Partial, Unavailable }

/// <summary>
/// The way back after "Remove from watch list": remembers the removed addresses and re-admits them
/// through the normal URL admission. Position and priority are not restored; downloaded files were never
/// touched. It runs at most once, so a double click or a second toast action cannot admit twice.
/// </summary>
internal sealed class WatchRemovalUndo
{
    private readonly Func<string, Task<int?>> _readmit;
    private Task<WatchUndoOutcome>? _run;

    /// <param name="readmit">Submits the URLs (one per line) and returns how many are being watched again,
    /// or null when the admission could not start (busy, closing, not restored).</param>
    internal WatchRemovalUndo(IEnumerable<string> urls, Func<string, Task<int?>> readmit)
    {
        Urls = urls.Where(url => !string.IsNullOrWhiteSpace(url)).Distinct(StringComparer.Ordinal).ToArray();
        _readmit = readmit;
    }

    internal IReadOnlyList<string> Urls { get; }
    internal int Count => Urls.Count;
    internal int Restored { get; private set; }
    internal string Text => string.Join(Environment.NewLine, Urls);

    internal Task<WatchUndoOutcome> RunAsync() => _run ??= RunCoreAsync();

    private async Task<WatchUndoOutcome> RunCoreAsync()
    {
        if (Count == 0) return WatchUndoOutcome.Unavailable;
        int? watching = await _readmit(Text);
        if (watching is not int value) return WatchUndoOutcome.Unavailable;
        Restored = Math.Min(value, Count);
        return Restored == Count ? WatchUndoOutcome.Restored : WatchUndoOutcome.Partial;
    }
}
