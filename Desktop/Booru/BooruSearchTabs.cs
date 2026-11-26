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

    private sealed class Slot(int id, BooruSearchView? snapshot, bool isHidden = false)
    {
        internal int Id { get; } = id;
        internal BooruSearchView? Snapshot { get; set; } = snapshot;
        internal bool IsHidden { get; } = isHidden;
    }

    internal BooruSearchTabs(BooruSearchModel search, Func<int>? limit = null)
    {
        _search = search ?? throw new ArgumentNullException(nameof(search));
        _search.EnsureSearchTab = EnsureActive;
        _limit = limit ?? (() => Config.BooruSearchTabsDefault);
    }

    internal event Action? Changed;
    internal int? ActiveId => _activeId;
    internal IReadOnlyList<int> Ids => _slots.Where(slot => !slot.IsHidden).Select(slot => slot.Id).ToArray();
    internal int Limit => Math.Clamp(_limit(), Config.BooruSearchTabsMinimum, Config.BooruSearchTabsMaximum);
    internal bool CanAdd => _slots.Count(slot => !slot.IsHidden) < Limit;

    internal BooruTabSnapshot Capture(int limit)
    {
        BooruSearchView current = _search.View;
        if (_slots.Count == 0)
            return new BooruTabSnapshot(current.ProviderId, -1, [], current);

        int activeIndex = _slots.FindIndex(slot => slot.Id == _activeId);
        List<BooruSearchView?> views = _slots.Select(slot => slot.Id == _activeId ? current : slot.Snapshot).ToList();
        if (activeIndex < 0 && HasStartDraft(current))
        {
            // V1 stores a separate draft only for an empty strip. Keep this draft as the active saved tab.
            activeIndex = views.Count;
            views.Add(current);
        }
        else if (activeIndex < 0)
        {
            activeIndex = 0;
            // A lone null entry is V1's empty-strip placeholder.
            views[0] ??= current;
        }

        // The storage limit applies to visible and hidden tabs together, in their original order.
        int[] indices = RetainedIndices(views.Count, activeIndex, limit);
        return new BooruTabSnapshot(current.ProviderId, Array.IndexOf(indices, activeIndex),
            Array.ConvertAll(indices, index => views[index]));
    }

    private static int[] RetainedIndices(int count, int activeIndex, int limit) => activeIndex < limit
        ? Enumerable.Range(0, Math.Min(count, limit)).ToArray()
        : Enumerable.Range(0, limit - 1).Append(activeIndex).ToArray();

    internal void Restore(BooruTabSnapshot snapshot)
    {
        foreach (Slot slot in _slots) _search.CancelBulk(slot.Id);
        _slots.Clear();
        Slot? active = null;
        HashSet<int> visible = RetainedIndices(snapshot.Tabs.Length, snapshot.ActiveIndex, Limit).ToHashSet();
        for (int index = 0; index < snapshot.Tabs.Length; index++)
        {
            Slot slot = new(_nextId++, snapshot.Tabs[index], isHidden: !visible.Contains(index));
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
        if (_activeId is null && HasStartDraft(current))
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
        int index = _slots.FindIndex(slot => slot.Id == id && !slot.IsHidden);
        if (index < 0) return false;
        _search.CancelBulk(id);
        _slots.RemoveAt(index);
        if (id == _activeId)
            Activate(_slots.Take(index).LastOrDefault(slot => !slot.IsHidden) ?? _slots.Find(slot => !slot.IsHidden));
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

    private Slot? Find(int id) => _slots.Find(slot => slot.Id == id && !slot.IsHidden);

    private static bool HasStartDraft(BooruSearchView view) => view.Input.Length > 0 || view.Mode != BooruInputMode.Tags;

    private void Store()
    {
        if (_activeId is { } id && Find(id) is { } active) active.Snapshot = _search.View;
    }
}
