using System;
using System.Collections.Generic;
using System.Linq;
using MeowChan.PluginHost.Http;
using MeowChan.PluginSdk;

namespace MeowChan.Desktop.Booru;

/// <summary>
/// The extra tabs next to the permanent search. Slot 0 is the permanent Search tab (it cannot be closed and does not count
/// against the limit); every other slot is one opened search. The tabs share the one <see cref="BooruSearchModel"/>: leaving a tab stores
/// the model's immutable view in it, entering a tab puts that view back without sending a request.
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
        internal BooruSearchTabTarget? UnboundTarget { get; set; }
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
        Slot slot = new(_nextId++) { UnboundTarget = copyCurrent ? Find(_activeId)?.UnboundTarget : null };
        BooruSearchView? copy = copyCurrent ? _search.View : null;
        Store();
        _slots.Add(slot);
        _activeId = slot.Id;
        _search.Restore(copy, _activeId);
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
        _search.Restore(target.Snapshot, _activeId);
        Changed?.Invoke();
        return true;
    }

    /// <summary>Closes an extra tab; closing the active one activates its left neighbour. The permanent tab is never closed.</summary>
    internal bool Close(int id)
    {
        int index = _slots.FindIndex(slot => slot.Id == id);
        if (id == PrimaryId || index < 0) return false;
        bool wasActive = id == _activeId;
        _search.CancelBulk(id);
        _slots.RemoveAt(index);
        if (wasActive)
        {
            Slot next = _slots[Math.Max(0, index - 1)];
            _activeId = next.Id;
            _search.Restore(next.Snapshot, _activeId);
        }
        Changed?.Invoke();
        return true;
    }

    internal BooruSearchTabState CaptureTargets(int tabLimit = Config.BooruSavedTabsMaximum,
        int resultLimit = Config.BooruSavedResultsPerTabDefault)
    {
        int activeIndex = _slots.FindIndex(slot => slot.Id == _activeId);
        int[] indices = RetainedIndices(_slots.Count, activeIndex, Math.Min(tabLimit, Limit + 1));
        BooruSearchTabTarget?[] targets = Array.ConvertAll(indices, index => CaptureTarget(_slots[index], resultLimit));
        return new(Array.IndexOf(indices, activeIndex), targets);
    }

    private BooruSearchTabTarget? CaptureTarget(Slot slot, int resultLimit)
    {
        if (ViewOf(slot.Id) is not { } view) return null;
        string input = view.Input.Length <= BooruSearchTabTarget.MaximumDraftLength
            ? view.Input : view.Input[..BooruSearchTabTarget.MaximumDraftLength];
        if (slot.UnboundTarget is { } unbound)
            return unbound with { Mode = view.Mode, Input = input, Rows = unbound.Rows?.Take(resultLimit).ToArray() };
        if (input.Length == 0 && view.Mode == BooruInputMode.Tags && view.ExecutedQuery is null && view.ExecutedPostId is null)
            return null;
        BooruSearchTabRow[] rows = view.Rows.Take(resultLimit).Select(row => new BooruSearchTabRow(row.Post.Post,
            row.Preview == BooruPreviewState.Ready && row.PreviewBytes is { Length: > 0 and <= BooruPreviewHttp.MaximumPreviewBytes } bytes
                ? bytes : null)).ToArray();
        return new(view.ProviderId, view.InstanceId, view.ExecutedPostIdentity?.PostId, view.ExecutedQuery, view.Page,
            view.Mode, input, view.IsEnd, view.Status == BooruSearchStatus.Empty, rows);
    }

    internal void RestoreTargets(BooruSearchTabState state, int tabLimit = Config.BooruSavedTabsMaximum,
        int resultLimit = Config.BooruSavedResultsPerTabDefault, SourceDescription? source = null)
    {
        int[] indices = RetainedIndices(state.Tabs.Length, state.ActiveIndex, Math.Min(tabLimit, Limit + 1));
        _slots.Clear();
        foreach (int index in indices)
        {
            BooruSearchTabTarget? target = state.Tabs[index];
            BooruSearchView? view = target is null ? null : _search.ViewFromTarget(target, resultLimit, source);
            _slots.Add(new Slot(index == 0 ? PrimaryId : _nextId++)
            {
                Snapshot = view,
                UnboundTarget = view?.Status == BooruSearchStatus.Unavailable ? target : null
            });
        }
        Slot active = _slots[Array.IndexOf(indices, state.ActiveIndex)];
        _activeId = active.Id;
        _search.Restore(active.Snapshot, _activeId);
        Changed?.Invoke();
    }

    // Keep the permanent and active tabs in strip order when applying a limit.
    private static int[] RetainedIndices(int count, int activeIndex, int limit) =>
        activeIndex < limit ? Enumerable.Range(0, Math.Min(count, limit)).ToArray()
            : Enumerable.Range(0, limit - 1).Append(activeIndex).ToArray();

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
