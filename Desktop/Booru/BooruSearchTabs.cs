using System;
using System.Collections.Generic;
using System.Linq;
using MeowChan.PluginHost.Http;
using MeowChan.PluginSdk;

namespace MeowChan.Desktop.Booru;

/// <summary>Search tabs share one model. Leaving a tab stores its view and entering it restores it without sending a request.</summary>
internal sealed class BooruSearchTabs
{
    internal const int PrimaryId = 0;

    private readonly BooruSearchModel _search;
    private readonly Func<int> _limit;
    private readonly List<Slot> _slots = new();
    private int _nextId = PrimaryId;
    private int? _activeId;
    private int _restoredResultLimit;
    private SourceDescription? _restoredSource;

    private sealed class Slot(int id)
    {
        internal int Id { get; } = id;
        internal BooruSearchView? Snapshot { get; set; }
        internal BooruSearchTabTarget? SavedTarget { get; set; }
        internal bool IsDeferred { get; set; }
    }

    internal BooruSearchTabs(BooruSearchModel search, Func<int>? limit = null)
    {
        _search = search ?? throw new ArgumentNullException(nameof(search));
        _search.EnsureSearchTab = EnsureActive;
        _limit = limit ?? (() => Config.BooruSearchTabsDefault);
    }

    internal event Action? Changed;
    internal int? ActiveId => _activeId;
    internal IReadOnlyList<int> Ids => _slots.Where(slot => !slot.IsDeferred).Select(slot => slot.Id).ToArray();
    internal int Limit => Math.Clamp(_limit(), Config.BooruSearchTabsMinimum, Config.BooruSearchTabsMaximum);
    internal bool CanAdd => _slots.Count(slot => !slot.IsDeferred) < Limit && _slots.Count < Config.BooruSavedTabsMaximum;

    internal BooruSearchView? ViewOf(int id) => id == _activeId ? _search.View : Find(id)?.Snapshot;

    /// <summary>Opens a new tab or duplicates an existing tab. Returns -1 at the limit.</summary>
    internal int Add(bool copyCurrent) => Add(copyCurrent ? _activeId : null);

    internal int Add(int? copyFrom)
    {
        if (!CanAdd) return -1;
        BooruSearchView current = _search.View;
        if (_activeId is null && HasStartDraft(current))
            _slots.Add(new Slot(_nextId++) { Snapshot = current });
        else
            Store();
        // The first implicit or restored search can use ID zero. Explicit opens return a positive ID.
        if (_nextId == PrimaryId) _nextId++;
        Slot slot = new(_nextId++)
        {
            Snapshot = copyFrom is { } id ? ViewOf(id) : _search.NewSourceView(current.ProviderId, current.InstanceId),
            SavedTarget = Find(copyFrom)?.SavedTarget
        };
        _slots.Add(slot);
        Activate(slot);
        return slot.Id;
    }

    private void EnsureActive()
    {
        if (_activeId.HasValue) return;
        Slot slot = new(_nextId++);
        _slots.Add(slot);
        _activeId = slot.Id;
        _search.SetSearchTabOwner(slot.Id);
        Changed?.Invoke();
    }

    internal void SelectSource(string sourceId, string instanceId)
    {
        BooruSearchView current = _search.View;
        if (current.ProviderId == sourceId && current.InstanceId == instanceId) return;
        if (_search.NewSourceView(sourceId, instanceId, current) is not { } next) return;
        _search.CancelBulk();
        if (Find(_activeId) is { } slot) slot.SavedTarget = null;
        _search.Restore(next, _activeId);
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
        int index = _slots.FindIndex(slot => slot.Id == id && !slot.IsDeferred);
        if (index < 0) return false;
        _search.CancelBulk(id);
        _slots.RemoveAt(index);
        if (id == _activeId)
        {
            Slot? next = _slots.Take(index).LastOrDefault(slot => !slot.IsDeferred)
                ?? _slots.Skip(index).FirstOrDefault(slot => !slot.IsDeferred);
            Activate(next);
        }
        else Changed?.Invoke();
        return true;
    }

    internal BooruSearchTabState CaptureTargets(int tabLimit = Config.BooruSavedTabsMaximum,
        int resultLimit = Config.BooruSavedResultsPerTabDefault)
    {
        BooruSearchView current = _search.View;
        if (_slots.Count == 0) return new(0, [null], CaptureView(current, 0));

        int count = _slots.Count;
        int activeIndex = _slots.FindIndex(slot => slot.Id == _activeId);
        if (activeIndex < 0)
        {
            // With hidden tabs the start draft is stored as a regular tab, in strip order.
            activeIndex = HasStartDraft(current) ? count++ : 0;
        }

        int[] retained = RetainedIndices(count, activeIndex, tabLimit);
        return new(Array.IndexOf(retained, activeIndex), Array.ConvertAll(retained, index =>
        {
            if (index == _slots.Count) return CaptureView(current, 0);
            Slot slot = _slots[index];
            return slot.IsDeferred ? slot.SavedTarget : CaptureTarget(slot, resultLimit);
        }));
    }

    private BooruSearchTabTarget? CaptureTarget(Slot slot, int resultLimit)
    {
        if (ViewOf(slot.Id) is not { } view) return null;
        if (slot.SavedTarget is { } unbound)
            return unbound with { Mode = view.Mode, Input = BoundedInput(view), Rows = unbound.Rows?.Take(resultLimit).ToArray() };
        return CaptureView(view, resultLimit);
    }

    private static string BoundedInput(BooruSearchView view) => view.Input.Length <= BooruSearchTabTarget.MaximumDraftLength
        ? view.Input : view.Input[..BooruSearchTabTarget.MaximumDraftLength];

    private static BooruSearchTabTarget CaptureView(BooruSearchView view, int resultLimit)
    {
        BooruSearchTabRow[] rows = view.Rows.Take(resultLimit).Select(row => new BooruSearchTabRow(row.Post.Post,
            row.Preview == BooruPreviewState.Ready && row.PreviewBytes is { Length: > 0 and <= BooruPreviewHttp.MaximumPreviewBytes } bytes
                ? bytes : null)).ToArray();
        return new(view.ProviderId, view.InstanceId, view.ExecutedPostIdentity?.PostId, view.ExecutedQuery, view.Page,
            view.Mode, BoundedInput(view), view.IsEnd, view.Status == BooruSearchStatus.Empty, rows,
            view.PagerLimits.Pages, view.PagerLimits.PostsPerPage, view.LastResultPage);
    }

    internal void RestoreTargets(BooruSearchTabState state, int tabLimit = Config.BooruSavedTabsMaximum,
        int resultLimit = Config.BooruSavedResultsPerTabDefault, SourceDescription? source = null)
    {
        foreach (Slot slot in _slots) _search.CancelBulk(slot.Id);
        _slots.Clear();
        _restoredResultLimit = resultLimit;
        _restoredSource = source;
        if (state.StartDraft is { } draft)
        {
            _activeId = null;
            _search.Restore(_search.ViewFromTarget(draft, 0, source) with { NeedsLoad = false }, null);
            Changed?.Invoke();
            return;
        }
        HashSet<int> visible = RetainedIndices(state.Tabs.Length, state.ActiveIndex, Math.Min(tabLimit, Limit)).ToHashSet();
        for (int index = 0; index < state.Tabs.Length; index++)
        {
            Slot slot = new(_nextId++)
            {
                IsDeferred = !visible.Contains(index),
                SavedTarget = state.Tabs[index]
            };
            if (!slot.IsDeferred) Materialize(slot);
            _slots.Add(slot);
        }
        Activate(_slots[state.ActiveIndex]);
    }

    private void Materialize(Slot slot)
    {
        if (slot.SavedTarget is not { } target) return;
        slot.Snapshot = _search.ViewFromTarget(target, _restoredResultLimit, _restoredSource);
        if (slot.Snapshot.Status != BooruSearchStatus.Unavailable) slot.SavedTarget = null;
    }

    private void Activate(Slot? slot)
    {
        _activeId = slot?.Id;
        _search.Restore(slot?.Snapshot, _activeId);
        Changed?.Invoke();
    }

    private static int[] RetainedIndices(int count, int activeIndex, int limit) =>
        activeIndex < limit ? Enumerable.Range(0, Math.Min(count, limit)).ToArray()
            : Enumerable.Range(0, limit - 1).Append(activeIndex).ToArray();

    internal static string Title(BooruSearchView? view, string blank, Func<string, string> post)
    {
        if (view?.ExecutedQuery is { Length: > 0 } query) return query;
        if (view?.ExecutedPostId is { } postId) return post(postId);
        return blank;
    }

    private static bool HasStartDraft(BooruSearchView view) => view.Input.Length > 0 || view.Mode != BooruInputMode.Tags;

    private Slot? Find(int? id) => _slots.Find(slot => slot.Id == id && !slot.IsDeferred);

    private void Store()
    {
        if (Find(_activeId) is { } active) active.Snapshot = _search.View;
    }
}
