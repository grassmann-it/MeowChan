using System;
using System.Collections.Generic;

namespace MeowChan.Desktop.Booru;

/// <summary>
/// Decides which row event the persistent notice line of a tab repeats. The cards of the Danbooru window are rebuilt on
/// every change, and a freshly built element never raises a live-region change, so a row's new state ("Added to the
/// download queue.", "Check finished: …") is mirrored into the tab's notice line, which stays the same element and is a
/// polite live region. A row event counts only when an already shown row changes to it; a row that merely appears (new
/// search, list refresh) announces nothing. A newer tab notice replaces the mirrored row, and a row that disappears
/// stops being mirrored. Pure state; the window turns it into text.
/// </summary>
internal sealed class BooruLiveMirror<TEvent>
{
    private readonly IEqualityComparer<TEvent?> _same;
    private Dictionary<string, TEvent?> _last = new(StringComparer.Ordinal);
    private object? _lastNotice;

    internal BooruLiveMirror(IEqualityComparer<TEvent?>? same = null) => _same = same ?? EqualityComparer<TEvent?>.Default;

    /// <summary>The row whose event the notice line shows, or null when it shows the tab notice.</summary>
    internal string? RowId { get; private set; }

    /// <param name="rows">Every shown row with its current event, or default when the row has nothing to say.</param>
    /// <param name="notice">The tab's own notice; compared by reference, so a repeated identical notice still counts.</param>
    /// <returns>True when the mirrored row changed in this update.</returns>
    internal bool Update(IEnumerable<(string Id, TEvent? Event)> rows, object? notice)
    {
        string? before = RowId;
        Dictionary<string, TEvent?> next = new(StringComparer.Ordinal);
        string? changed = null;
        foreach ((string id, TEvent? value) in rows)
        {
            next[id] = value;
            if (value is not null && _last.TryGetValue(id, out TEvent? previous) && !_same.Equals(previous, value)) changed = id;
        }
        if (notice is not null && !ReferenceEquals(notice, _lastNotice)) RowId = null;
        if (changed is not null) RowId = changed;
        if (RowId is not null && !next.ContainsKey(RowId)) RowId = null;
        _last = next;
        _lastNotice = notice;
        return !string.Equals(before, RowId, StringComparison.Ordinal) || changed is not null;
    }
}
