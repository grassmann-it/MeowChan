using System;
using System.Collections.Generic;
using System.Linq;

namespace MeowChan.Desktop.Booru;

/// <summary>Searches share one model. Leaving a tab stores its view; entering a tab restores it without sending a request.</summary>
internal sealed class BooruSearchTabs
{
    private readonly BooruSearchModel _search;
    private readonly Func<int> _limit;
    private readonly List<Slot> _slots = new();
    private int _nextId = 1;
    private int? _activeId;

    private sealed class Slot(int id, BooruSearchView? snapshot)
    {
        internal int Id { get; } = id;
        internal BooruSearchView? Snapshot { get; set; } = snapshot;
    }

    internal BooruSearchTabs(BooruSearchModel search, Func<int>? limit = null)
    {
        _search = search ?? throw new ArgumentNullException(nameof(search));
        _search.EnsureSearchTab = EnsureActive;
        _limit = limit ?? (() => Config.BooruSearchTabsDefault);
    }

    internal event Action? Changed;
    internal int? ActiveId => _activeId;
    internal IReadOnlyList<int> Ids => _slots.Select(slot => slot.Id).ToArray();
    internal int Limit => Math.Clamp(_limit(), Config.BooruSearchTabsMinimum, Config.BooruSearchTabsMaximum);
    internal bool CanAdd => _slots.Count < Limit;

    internal BooruTabSnapshot Capture(int limit)
    {
        int activeIndex = _slots.FindIndex(slot => slot.Id == _activeId);
        BooruSearchView current = _search.View;
        if (activeIndex < 0)
            return new BooruTabSnapshot(current.ProviderId, -1, [], current);

        // Keep the active tab and the earliest remaining tabs in strip order.
        int[] indices = RetainedIndices(_slots.Count, activeIndex, limit);
        return new BooruTabSnapshot(current.ProviderId, Array.IndexOf(indices, activeIndex),
            indices.Select(index => index == activeIndex ? current : _slots[index].Snapshot).ToArray());
    }

    private static int[] RetainedIndices(int count, int activeIndex, int limit) => activeIndex < limit
        ? Enumerable.Range(0, Math.Min(count, limit)).ToArray()
        : Enumerable.Range(0, limit - 1).Append(activeIndex).ToArray();

    internal void Restore(BooruTabSnapshot snapshot)
    {
        foreach (Slot slot in _slots) _search.CancelBulk(slot.Id);
        _slots.Clear();
        Slot? active = null;
        foreach (int index in RetainedIndices(snapshot.Tabs.Length, snapshot.ActiveIndex, Limit))
        {
            Slot slot = new(_nextId++, snapshot.Tabs[index]);
            _slots.Add(slot);
            if (index == snapshot.ActiveIndex) active = slot;
        }
        _activeId = active?.Id;
        _search.Restore(active?.Snapshot ?? snapshot.Draft, _activeId);
        Changed?.Invoke();
    }

    internal BooruSearchView? ViewOf(int id) => id == _activeId ? _search.View : Find(id)?.Snapshot;

    /// <summary>Opens a blank tab or copies a tab's draft and results. Returns -1 if the tab cannot be opened.</summary>
    internal int Add(bool copyCurrent) => Add(copyCurrent ? _activeId : null);

    internal int Add(int? copyFrom)
    {
        if (!CanAdd) return -1;
        BooruSearchView? snapshot = copyFrom is { } id ? ViewOf(id) : null;
        BooruSearchView current = _search.View;
        if (_activeId is null && (current.Input.Length > 0 || current.Mode != BooruInputMode.Tags))
            _slots.Add(new Slot(_nextId++, current));
        else Store();
        Slot slot = new(_nextId++, snapshot);
        _slots.Add(slot);
        Activate(slot);
        return slot.Id;
    }

    private void EnsureActive()
    {
        if (_activeId.HasValue) return;
        Slot slot = new(_nextId++, null);
        _slots.Add(slot);
        _activeId = slot.Id;
        _search.SetSearchTabOwner(slot.Id);
        Changed?.Invoke();
    }

    internal bool Select(int id)
    {
        if (id == _activeId) return true;
        if (Find(id) is not { } target) return false;
        Store();
        Activate(target);
        return true;
    }

    internal bool Close(int id)
    {
        int index = _slots.FindIndex(slot => slot.Id == id);
        if (index < 0) return false;
        _search.CancelBulk(id);
        _slots.RemoveAt(index);
        if (id == _activeId) Activate(_slots.Count == 0 ? null : _slots[Math.Max(0, index - 1)]);
        else Changed?.Invoke();
        return true;
    }

    internal static string Title(BooruSearchView? view, string blank, Func<string, string> post)
    {
        if (view?.ExecutedQuery is { Length: > 0 } query) return query;
        if (view?.ExecutedPostId is { } postId) return post(postId);
        return blank;
    }

    private void Activate(Slot? slot)
    {
        _activeId = slot?.Id;
        _search.Restore(slot?.Snapshot, _activeId);
        Changed?.Invoke();
    }

    private Slot? Find(int id) => _slots.Find(slot => slot.Id == id);

    private void Store()
    {
        if (_activeId is { } id && Find(id) is { } active) active.Snapshot = _search.View;
    }
}
