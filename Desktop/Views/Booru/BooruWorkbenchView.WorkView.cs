using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Automation;
using Avalonia.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.WorkViews;

namespace MeowChan.Desktop.Booru;

/// <summary>
/// Applies the shared work-view contract to the hosted workbench. The workbench keeps the same
/// controls and handlers in every mode; Barebones only forwards secondary actions to its existing
/// More surface, so no provider or queue behavior is duplicated.
/// </summary>
internal sealed partial class BooruWorkbenchView
{
    private readonly Button _more = new() { Name = "BooruMore", IsVisible = false };
    private readonly List<Button> _resultWorkViewActions = new();
    private readonly List<Button> _watcherWorkViewActions = new();
    private WorkView _workView = WorkView.Advanced;
    private bool _workViewRegistered;
    private bool _catalogUsageRevealed;

    internal WorkView CurrentWorkView => _workView;

    /// <summary>Applies a window work view; changing it closes only the local catalog disclosure.</summary>
    internal void ApplyWorkView(WorkView view, bool viewChanged)
    {
        if (_closed) return;
        bool changed = viewChanged || view != _workView;
        _workView = view;
        if (changed) _catalogUsageRevealed = false;
        RefreshWorkViewPresentation();
    }

    private void RegisterWorkViewIds()
    {
        WorkViewElement.SetId(_input, "booru.search.input");
        WorkViewElement.SetId(_mode, "booru.search.mode");
        WorkViewElement.SetId(_submit, "booru.search.submit");
        WorkViewElement.SetId(_cancel, "booru.search.cancel");
        WorkViewElement.SetId(_statusHost, "booru.search.status");
        WorkViewElement.SetId(_results, "booru.results");
        WorkViewElement.SetId(_watchers, "booru.watchers.list");
        WorkViewElement.SetId(_noticeHost, "booru.notice");
        WorkViewElement.SetId(_storageHost, "booru.catalog.usage");
        _more.Click += (_, _) => WorkViewMoreSurface.Open(_more, MoreEntries());
        _workViewRegistered = true;
        WorkViewMoreSurface.Configure(_more, 0);
    }

    private void RefreshWorkViewPresentation()
    {
        if (!_workViewRegistered || _closed) return;

        WorkViewGate.Apply(_input, _workView);
        WorkViewGate.Apply(_mode, _workView);
        WorkViewGate.Apply(_submit, _workView);
        WorkViewGate.Apply(_cancel, _workView, stateVisible: _model.Search.View.IsBusy);
        WorkViewGate.Apply(_statusHost, _workView, WorkViewElement.GetStateVisible(_statusHost));
        WorkViewGate.Apply(_results, _workView);
        WorkViewGate.Apply(_watchers, _workView);
        WorkViewGate.Apply(_noticeHost, _workView, WorkViewElement.GetStateVisible(_noticeHost));
        WorkViewGate.Apply(_storageHost, _workView, WorkViewElement.GetStateVisible(_storageHost), _catalogUsageRevealed);

        foreach (Button button in _resultWorkViewActions.Concat(_watcherWorkViewActions))
            WorkViewGate.Apply(button, _workView, WorkViewElement.GetStateVisible(button));

        WorkViewMoreSurface.Configure(_more, MoreEntries().Count);
    }

    private IReadOnlyList<WorkViewMoreSurface.Entry> MoreEntries()
    {
        List<WorkViewMoreSurface.Entry> entries = WorkViewMoreSurface
            .ForButtons(_resultWorkViewActions.Concat(_watcherWorkViewActions), _workView).ToList();
        WorkViewEntry storage = WorkViewPolicy.Get("booru.catalog.usage");
        if (WorkViewElement.GetStateVisible(_storageHost)
            && WorkViewPolicy.Resolve(storage.Id, _workView, _catalogUsageRevealed) == Presence.Overflow)
        {
            entries.Add(new(storage.OverflowOrder, null, () => Texts.T("budgets.booruCatalog"), RevealCatalogUsage,
                () => WorkViewElement.GetStateVisible(_storageHost), "info"));
        }
        return entries.OrderBy(entry => entry.Order).ToArray();
    }

    private void RevealCatalogUsage()
    {
        if (_closed || !WorkViewElement.GetStateVisible(_storageHost)) return;
        _catalogUsageRevealed = true;
        RefreshWorkViewPresentation();
        _storageHost.BringIntoView();
    }

    private void RegisterWorkViewAction(Button button, string id, ICollection<Button> owner)
    {
        WorkViewElement.SetId(button, id);
        owner.Add(button);
        WorkViewGate.Apply(button, _workView, WorkViewElement.GetStateVisible(button));
    }
}
