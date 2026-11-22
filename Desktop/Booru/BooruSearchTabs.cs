using System;
using System.Collections.Generic;

namespace MeowChan.Desktop.Booru;

/// <summary>
/// The extra search tabs next to "Search" and "Watchers". Slot 0 is the permanent Search tab (it cannot be closed and does not count
/// against the limit); every other slot is one opened search. The tabs share the one <see cref="BooruSearchModel"/>: leaving a tab stores
/// the model's immutable view in it, entering a tab puts that view back (never sending a request). Tabs live for the session only.
/// </summary>
internal sealed class BooruSearchTabs
{
    internal const int PrimaryId = 0;

    private readonly BooruSearchModel _search;
    private readonly Func<int> _limit;
    private readonly List<Slot> _slots = new() { new Slot(PrimaryId) };
    private int _nextId = PrimaryId + 1;
    private int _activeId = PrimaryId;

    private sealed class Slot(int id)
    {
        internal int Id { get; } = id;
        internal BooruSearchView? Snapshot { get; set; }
    }

    internal BooruSearchTabs(BooruSearchModel search, Func<int>? limit = null)
    {
        _search = search ?? throw new ArgumentNullException(nameof(search));
        _limit = limit ?? (() => MeowChan.Config.BooruSearchTabsDefault);
    }

    /// <summary>Raised after the set of tabs or the active tab changed.</summary>
    internal event Action? Changed;

    internal int ActiveId => _activeId;
    /// <summary>The ids in strip order, the permanent Search tab first.</summary>
    internal IReadOnlyList<int> Ids { get { List<int> ids = new(_slots.Count); foreach (Slot slot in _slots) ids.Add(slot.Id); return ids; } }

    /// <summary>How many extra tabs may be open (the setting, clamped to its range; read at each use).</summary>
    internal int Limit => Math.Clamp(_limit(), MeowChan.Config.BooruSearchTabsMinimum, MeowChan.Config.BooruSearchTabsMaximum);
    internal int ExtraCount => _slots.Count - 1;
    internal bool CanAdd => ExtraCount < Limit;

    /// <summary>The view a tab shows: the live one for the active tab, else the stored one (null: a blank search).</summary>
    internal BooruSearchView? ViewOf(int id)
    {
        if (id == _activeId) return _search.View;
        return Find(id)?.Snapshot;
    }

    /// <summary>Opens a new tab and makes it the active one. <paramref name="copyCurrent"/> starts it with the current search and its results.</summary>
    /// <returns>The new tab's id, or -1 when the limit is reached.</returns>
    internal int Add(bool copyCurrent)
    {
        if (!CanAdd) return -1;
        Slot slot = new(_nextId++);
        BooruSearchView? copy = copyCurrent ? _search.View : null;
        Store();
        _slots.Add(slot);
        _activeId = slot.Id;
        _search.Restore(copy);
        Changed?.Invoke();
        return slot.Id;
    }

    internal bool Select(int id)
    {
        if (id == _activeId) return true;
        Slot? target = Find(id);
        if (target is null) return false;
        Store();
        _activeId = id;
        _search.Restore(target.Snapshot);
        Changed?.Invoke();
        return true;
    }

    /// <summary>Closes an extra tab; closing the active one activates its left neighbour. The permanent tab is never closed.</summary>
    internal bool Close(int id)
    {
        int index = _slots.FindIndex(slot => slot.Id == id);
        if (id == PrimaryId || index < 0) return false;
        bool wasActive = id == _activeId;
        if (wasActive) _search.Cancel();
        _slots.RemoveAt(index);
        if (wasActive)
        {
            Slot next = _slots[Math.Max(0, index - 1)];
            _activeId = next.Id;
            _search.Restore(next.Snapshot);
        }
        Changed?.Invoke();
        return true;
    }

    /// <summary>The text of a tab: its search, else its post, else what is typed, else "new search" (the caller localizes the last two).</summary>
    internal static string Title(BooruSearchView? view, string blank, Func<string, string> post)
    {
        if (view is null) return blank;
        if (view.ExecutedQuery is { Length: > 0 } query) return query;
        if (view.ExecutedPostId is { } postId) return post(postId);
        return string.IsNullOrWhiteSpace(view.Input) ? blank : view.Input.Trim();
    }

    private Slot? Find(int id) => _slots.Find(slot => slot.Id == id);

    private void Store()
    {
        if (Find(_activeId) is { } active) active.Snapshot = _search.View;
    }
}
