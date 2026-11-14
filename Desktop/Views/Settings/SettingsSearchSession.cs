using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using MeowChan.Desktop.Controls;

namespace MeowChan;

/// <summary>
/// The behaviour of the settings search apart from its controls. Typing only produces a ranked list with the first hit
/// highlighted; moving the highlight (arrow keys, pointer) changes nothing on the page either. Only a commit (Enter, a
/// click on a hit) calls <see cref="Committed"/>, which is where the page switches, groups open and the row is marked.
/// </summary>
internal sealed class SettingsSearchSession
{
    /// <summary>
    /// The result list is computed this long after the last keystroke (QueryDebounce, trailing edge): fast typing costs one
    /// search instead of one per character. Between 120 and 150 ms the list still feels live.
    /// </summary>
    internal static readonly TimeSpan QueryDelay = TimeSpan.FromMilliseconds(130);

    private readonly Func<SettingsSearchIndex> _index;
    private readonly Action<SettingsSearchHit> _committed;

    internal SettingsSearchSession(Func<SettingsSearchIndex> index, Action<SettingsSearchHit> committed)
    {
        _index = index ?? throw new ArgumentNullException(nameof(index));
        _committed = committed ?? throw new ArgumentNullException(nameof(committed));
    }

    internal IReadOnlyList<SettingsSearchHit> Hits { get; private set; } = Array.Empty<SettingsSearchHit>();

    /// <summary>The highlighted hit, or -1 without hits.</summary>
    internal int Highlight { get; private set; } = -1;

    internal int Searches { get; private set; }

    /// <summary>The search field delays its query by <see cref="QueryDelay"/>.</summary>
    internal static void Configure(SearchBox box)
    {
        ArgumentNullException.ThrowIfNull(box);
        box.Debounce = QueryDelay;
    }

    /// <summary>A new query: the list and the first hit's highlight; nothing else moves. A blank query empties the list.</summary>
    internal IReadOnlyList<SettingsSearchHit> Search(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            Hits = Array.Empty<SettingsSearchHit>();
            Highlight = -1;
            return Hits;
        }
        Searches++;
        Hits = SettingsSearchMatcher.Search(_index(), query);
        Highlight = Hits.Count > 0 ? 0 : -1;
        return Hits;
    }

    /// <summary>Arrow keys: the highlight moves by <paramref name="delta"/> hits, clamped to the list. Returns whether it moved.</summary>
    internal bool Move(int delta)
    {
        if (Hits.Count == 0) return false;
        int next = Math.Clamp(Math.Max(Highlight, 0) + delta, 0, Hits.Count - 1);
        bool moved = next != Highlight;
        Highlight = next;
        return moved;
    }

    /// <summary>The pointer or a programmatic selection puts the highlight on a hit; this is no commit.</summary>
    internal void Select(int index)
    {
        if (index >= 0 && index < Hits.Count) Highlight = index;
    }

    /// <summary>Enter: commits the highlighted hit (the first when none is). Returns false without hits.</summary>
    internal bool Commit()
    {
        if (Hits.Count == 0) return false;
        _committed(Hits[Highlight >= 0 ? Highlight : 0]);
        return true;
    }

    /// <summary>A click on a hit commits exactly that hit.</summary>
    internal void Commit(SettingsSearchHit hit) => _committed(hit);
}

/// <summary>
/// The groups a search commit opened. They close again with the end of the search (cleared query, page left) unless the user
/// changed a value in them meanwhile: then the group stays as the user left it. A group the user had already opened is never
/// recorded and so never closed.
/// </summary>
internal sealed class SettingsSearchGroups
{
    private readonly HashSet<Expander> _opened = new();

    internal int OpenedCount => _opened.Count;

    internal bool IsOpenedBySearch(Expander group) => _opened.Contains(group);

    /// <summary>
    /// Unfolds <paramref name="groups"/> for a commit. Groups an earlier commit opened and this one does not need close first.
    /// </summary>
    internal void Open(IReadOnlyList<Expander> groups)
    {
        if (_opened.Count > 0)
        {
            var needed = new HashSet<Expander>(groups);
            foreach (Expander group in _opened.ToArray())
                if (!needed.Contains(group)) { group.IsExpanded = false; _opened.Remove(group); }
        }
        foreach (Expander group in groups)
        {
            if (group.IsExpanded) continue;
            group.IsExpanded = true;
            _opened.Add(group);
        }
    }

    /// <summary>The user changed a value inside these groups (the source's expander ancestors): they stay open for good.</summary>
    internal void NoteEdited(IEnumerable<Expander> containing)
    {
        if (_opened.Count == 0) return;
        foreach (Expander group in containing) _opened.Remove(group);
    }

    /// <summary>The end of the search: every group it opened and the user did not edit folds again. Returns how many closed.</summary>
    internal int Release()
    {
        int closed = 0;
        foreach (Expander group in _opened)
        {
            if (!group.IsExpanded) continue;
            group.IsExpanded = false;
            closed++;
        }
        _opened.Clear();
        return closed;
    }
}
